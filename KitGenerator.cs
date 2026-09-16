using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Constants;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace RandomKitTrader;

/// <summary>
/// Builds a completely randomised "kit" - no two pieces come from the same preset or even the same
/// donor bot. Every equipment slot (weapon, armor, rig, helmet, mask, eyewear, earpiece, armband,
/// backpack, holster) is rolled independently against a freshly, randomly generated throwaway bot,
/// so the combination you get is never a recognisable "preset" - it's mixed and matched from
/// completely separate rolls. The weapon still comes out fully kitted with random (but compatible)
/// attachments and magazines loaded with a single randomly chosen matching ammo type, because each
/// individual roll still goes through SPT's own bot equipment generator (the same code that dresses
/// scavs/PMCs/bosses) rather than hand-rolled attachment/ammo compatibility rules - that's what
/// guarantees mods fit their slots and magazines are filled with a caliber the weapon can actually
/// chamber. A couple of extra loaded spare mags for that same weapon get cloned in afterwards too
/// (any mismatched mags/ammo that happened to be sitting in an independently-rolled rig/backpack are
/// stripped out first, since those belonged to a completely different donor's weapon). On top of the
/// equipment, every kit also gets a handful of random meds, a couple of random grenades, and a few
/// completely wildcard bonus items pulled from the entire item database.
/// </summary>
[Injectable]
public class KitGenerator(
    ISptLogger<KitGenerator> logger,
    BotTable botTable,
    TemplateTable templateTable,
    BotInventoryGenerator botInventoryGenerator,
    ItemHelper itemHelper,
    ItemFilterService itemFilterService,
    RandomUtil randomUtil,
    ICloner cloner
)
{
    /// <summary>The one slot that's mandatory - a kit with no weapon at all doesn't get sent.</summary>
    private const EquipmentSlots MandatorySlot = EquipmentSlots.FirstPrimaryWeapon;

    /// <summary>Every other slot we try to fill, each from its own independent random roll.</summary>
    private static readonly EquipmentSlots[] OptionalSlots =
    [
        EquipmentSlots.Holster,
        EquipmentSlots.ArmorVest,
        EquipmentSlots.TacticalVest,
        EquipmentSlots.Headwear,
        EquipmentSlots.FaceCover,
        EquipmentSlots.Eyewear,
        EquipmentSlots.Earpiece,
        EquipmentSlots.ArmBand,
        EquipmentSlots.Backpack,
    ];

    /// <summary>Every slot that gets forced to a 100% roll chance on whichever bot template we clone.</summary>
    private static readonly EquipmentSlots[] AllRolledSlots = [MandatorySlot, .. OptionalSlots];

    private static readonly MongoId[] MedsBaseClasses =
    [
        BaseClasses.MED_KIT,
        BaseClasses.MEDICAL,
        BaseClasses.MEDS,
        BaseClasses.DRUGS,
        BaseClasses.STIMULATOR,
    ];

    /// <summary>
    /// Used to strip spare magazines/ammo that came pre-loaded in a rig/backpack out of a slot's
    /// subtree - those belonged to that slot's own (unrelated) donor bot's weapon, not the one that
    /// actually ends up in the kit, so they'd almost never be the right caliber.
    /// </summary>
    private static readonly MongoId[] MagazineAndAmmoBaseClasses = [BaseClasses.MAGAZINE, BaseClasses.AMMO, BaseClasses.AMMO_BOX];

    /// <summary>Rig/backpack slots specifically, since those are the only ones that come pre-loaded with spare mags/ammo.</summary>
    private static readonly EquipmentSlots[] LootContainerSlots = [EquipmentSlots.TacticalVest, EquipmentSlots.Backpack];

    private List<MongoId>? _medsPool;
    private List<MongoId>? _grenadePool;
    private List<MongoId>? _bonusItemPool;
    private List<string>? _commonRoles;
    private List<string>? _rareRoles;

    /// <summary>
    /// A fingerprint of the last kit's weapon (every item tpl + count in it, order-independent).
    /// This mod's one instance lives for the whole life of the server (it's handed off once into the
    /// static purchase-detection context at startup), so this genuinely remembers across purchases -
    /// it's what lets us notice "hey, that's the exact same build as last time" and re-roll.
    /// </summary>
    private string? _lastWeaponSignature;

    /// <summary>
    /// How often any single slot's donor roll is a boss/follower role instead of a normal one.
    /// Bosses and their followers vastly outnumber the "normal" bot roles in SPT's bot database
    /// (every boss encounter typically adds several unique follower role keys), so picking a role
    /// uniformly at random across every role in the game ends up drawing boss-tier gear almost every
    /// time. Weighting it like this keeps boss loot possible - every boss weapon/armor piece can
    /// still show up - without it dominating every single kit.
    /// </summary>
    private const int RareRoleChancePercent = 15;

    /// <summary>"Normal" bot roles - scavs, raiders, PMCs, marksmen, etc.</summary>
    private List<string> CommonRoles => _commonRoles ??= botTable.Types.Where(kv => kv.Value is not null && !IsRareRole(kv.Key)).Select(kv => kv.Key).ToList();

    /// <summary>Boss and boss-follower roles - the rare/fancy end of the gear pool.</summary>
    private List<string> RareRoles => _rareRoles ??= botTable.Types.Where(kv => kv.Value is not null && IsRareRole(kv.Key)).Select(kv => kv.Key).ToList();

    private static bool IsRareRole(string role) => role.Contains("boss", StringComparison.OrdinalIgnoreCase) || role.Contains("follower", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Roll which bot role to draw from next, weighted towards common roles. `excludeRoles`, when
    /// given, is steered away from (falls back to the full pool if excluding it would leave nothing
    /// to pick from) - used to stop the weapon roll from grabbing the exact same donor role twice in
    /// a row.
    /// </summary>
    private string PickRole(ISet<string>? excludeRoles = null)
    {
        var commonRoles = CommonRoles;
        var rareRoles = RareRoles;

        if (excludeRoles is { Count: > 0 })
        {
            var filteredCommon = commonRoles.Where(role => !excludeRoles.Contains(role)).ToList();
            var filteredRare = rareRoles.Where(role => !excludeRoles.Contains(role)).ToList();
            if (filteredCommon.Count > 0 || filteredRare.Count > 0)
            {
                commonRoles = filteredCommon;
                rareRoles = filteredRare;
            }
        }

        if (rareRoles.Count > 0 && (commonRoles.Count == 0 || randomUtil.GetInt(1, 100) <= RareRoleChancePercent))
        {
            return randomUtil.GetArrayValue(rareRoles);
        }

        return randomUtil.GetArrayValue(commonRoles);
    }

    /// <summary>
    /// Generate one completely randomised kit, ready to be attached to a mail message. Nothing here
    /// is a preset - every equipment slot is an independent roll against its own randomly generated
    /// throwaway bot, so a helmet, a rig, and a weapon in the same kit will almost always have come
    /// from entirely different, unrelated rolls.
    /// </summary>
    public List<Item> GenerateKit(MongoId sessionId)
    {
        if (CommonRoles.Count == 0 && RareRoles.Count == 0)
        {
            logger.Error("[RandomKitTrader] No bot roles exist in the bot database, unable to generate a kit");
            return [];
        }

        var items = new List<Item>();

        // The weapon is mandatory - keep independently re-rolling (a fresh random role each time,
        // steering away from roles already tried this call) until one produces a weapon. On top of
        // that, some bot roles - signature-weapon bosses especially - have a very narrow, almost
        // fixed loadout pool, so it's entirely possible to roll a build that's byte-for-byte
        // identical to the previous kit sold. If that happens, keep re-rolling (still capped, so a
        // kit always eventually gets sent) until it's different from last time.
        List<Item> weaponItems = [];
        string? weaponSignature = null;
        var triedRoles = new HashSet<string>();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var role = PickRole(triedRoles);
            triedRoles.Add(role);

            var candidate = TryGenerateSlotFromRole(sessionId, role, MandatorySlot);
            if (candidate.Count == 0)
            {
                continue;
            }

            weaponItems = candidate;
            weaponSignature = BuildWeaponSignature(candidate);

            if (weaponSignature != _lastWeaponSignature)
            {
                break;
            }
        }

        _lastWeaponSignature = weaponSignature ?? _lastWeaponSignature;

        if (weaponItems.Count == 0)
        {
            logger.Warning("[RandomKitTrader] Could not roll a primary weapon after several attempts - kit will be missing one");
        }
        else
        {
            items.AddRange(weaponItems);
            // A couple of extra loaded spare mags for the actual rolled weapon - cloned straight off
            // its own already-correct loaded magazine, so they're guaranteed to fit and carry the
            // same ammo, rather than whatever mismatched mags happened to be sitting in the rig/backpack.
            items.AddRange(GenerateSpareMagazines(weaponItems));
        }

        // Every other slot: its own fresh random role, a couple of attempts in case that particular
        // roll doesn't have anything for the slot, then just move on and leave it empty.
        foreach (var slot in OptionalSlots)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var role = PickRole();
                var slotItems = TryGenerateSlotFromRole(sessionId, role, slot);
                if (slotItems.Count > 0)
                {
                    items.AddRange(slotItems);
                    break;
                }
            }
        }

        items.AddRange(GenerateRandomMeds());
        items.AddRange(GenerateRandomGrenades());
        items.AddRange(GenerateRandomBonusItems());

        return items;
    }

    /// <summary>
    /// Clone a bot template for the given role, force every slot we care about to a 100% roll
    /// chance, generate one full throwaway bot inventory from it, and pull just the single
    /// requested equipment slot (plus its children/attachments) back out as a standalone item tree.
    /// Everything else that bot rolled is thrown away - this is deliberately wasteful so that a
    /// weapon and, say, a helmet almost never come from the same generated bot.
    /// </summary>
    private List<Item> TryGenerateSlotFromRole(MongoId sessionId, string role, EquipmentSlots targetSlot)
    {
        var inventory = GenerateBotInventory(sessionId, role);
        if (inventory is null)
        {
            return [];
        }

        var allItems = inventory.Items ?? [];
        // ParentId/SlotId on Item are plain strings, while inventory.Equipment is a MongoId - compare as strings
        var equipmentRootId = inventory.Equipment?.ToString();
        var targetSlotName = targetSlot.ToString();

        var root = allItems.FirstOrDefault(item => item.ParentId == equipmentRootId && item.SlotId == targetSlotName);
        if (root is null)
        {
            return [];
        }

        var subtree = new List<Item> { root };
        subtree.AddRange(itemHelper.FindAndReturnChildrenByAssort(root.Id, allItems));

        // Rigs/backpacks generated by SPT's bot generator often come pre-stocked with spare mags and
        // loose ammo for THAT donor bot's own weapon. Since every slot in a kit is rolled from a
        // completely separate, independent donor, those mags/ammo are almost never the right caliber
        // for whichever weapon actually ends up in the kit - strip them here; correct spare mags for
        // the real rolled weapon are added separately in GenerateSpareMagazines.
        if (LootContainerSlots.Contains(targetSlot))
        {
            subtree = StripMismatchedMagazinesAndAmmo(subtree);
        }

        // Give the whole subtree a fresh standalone root with a brand new unique id (so it can't
        // collide with anything already in the player's inventory, or with items pulled from any of
        // the other independent rolls that make up the rest of this kit), while preserving the
        // parent/child relationships between the item and its mods.
        var newRoot = new Item
        {
            Id = new MongoId(),
            Template = root.Template,
            ParentId = null,
            SlotId = null,
        };

        return itemHelper.ReparentItemAndChildren(newRoot, subtree);
    }

    /// <summary>
    /// A simple order-independent fingerprint of a weapon subtree - every item's tpl plus its stack
    /// count (so ammo quantity counts too), sorted so it doesn't matter what order the items came
    /// back in. Two builds with this same signature are, for all practical purposes, the same build.
    /// </summary>
    private static string BuildWeaponSignature(List<Item> weaponItems)
    {
        return string.Join(
            "|",
            weaponItems.Select(item => $"{item.Template.ToString()}:{item.Upd?.StackObjectsCount ?? 1}").OrderBy(signature => signature, StringComparer.Ordinal)
        );
    }

    /// <summary>
    /// Removes any magazine/ammo/ammo-box items from a slot's extracted subtree, along with anything
    /// nested inside them (e.g. cartridges stacked inside a removed magazine). Leaves everything else
    /// (pouches, the rig/backpack itself, non-ammo loot, etc) untouched.
    /// </summary>
    private List<Item> StripMismatchedMagazinesAndAmmo(List<Item> subtree)
    {
        var toRemove = new HashSet<string>();

        foreach (var item in subtree)
        {
            if (itemHelper.IsOfBaseclasses(item.Template, MagazineAndAmmoBaseClasses))
            {
                toRemove.Add(item.Id);
            }
        }

        if (toRemove.Count == 0)
        {
            return subtree;
        }

        // Also catch anything parented to a removed item (e.g. ammo inside a removed magazine).
        bool changedThisPass;
        do
        {
            changedThisPass = false;
            foreach (var item in subtree)
            {
                if (item.ParentId != null && toRemove.Contains(item.ParentId) && toRemove.Add(item.Id))
                {
                    changedThisPass = true;
                }
            }
        } while (changedThisPass);

        return subtree.Where(item => !toRemove.Contains(item.Id)).ToList();
    }

    /// <summary>
    /// A couple of extra loaded spare magazines for the weapon that actually ended up in the kit -
    /// each one a fresh standalone clone of the weapon's own already-correct loaded magazine (and its
    /// ammo), so they're guaranteed to fit the weapon and carry the same ammo it's already loaded with.
    /// </summary>
    private List<Item> GenerateSpareMagazines(List<Item> weaponItems)
    {
        var loadedMagazine = weaponItems.FirstOrDefault(item => itemHelper.IsOfBaseclass(item.Template, BaseClasses.MAGAZINE));
        if (loadedMagazine is null)
        {
            return [];
        }

        var loadedMagazineId = loadedMagazine.Id.ToString();
        var ammoChildren = weaponItems.Where(item => item.ParentId == loadedMagazineId).ToList();

        var spareMags = new List<Item>();
        var spareMagCount = randomUtil.GetInt(2, 3);

        for (var i = 0; i < spareMagCount; i++)
        {
            // Deep clone before reparenting - ReparentItemAndChildren mutates ids in place, and we
            // must not touch the magazine that's still actually attached to the weapon in `items`.
            var subtreeClone = cloner.Clone(new List<Item> { loadedMagazine }.Concat(ammoChildren).ToList());
            if (subtreeClone is null || subtreeClone.Count == 0)
            {
                continue;
            }

            var newRoot = new Item
            {
                Id = new MongoId(),
                Template = loadedMagazine.Template,
                ParentId = null,
                SlotId = null,
            };

            spareMags.AddRange(itemHelper.ReparentItemAndChildren(newRoot, subtreeClone));
        }

        return spareMags;
    }

    /// <summary>Clone a bot template, force our slots to always roll, and generate one throwaway bot's full inventory.</summary>
    private BotBaseInventory? GenerateBotInventory(MongoId sessionId, string role)
    {
        var originalTemplate = botTable.Types.GetValueOrDefault(role);
        if (originalTemplate is null)
        {
            return null;
        }

        // Deep clone so we never mutate the real bot template used for actual raids
        var template = cloner.Clone(originalTemplate);
        if (template is null)
        {
            return null;
        }

        ForceChancesToMax(template.BotChances);

        var isPmc = role is "usec" or "bear";
        var side = role switch
        {
            "usec" => Sides.Usec,
            "bear" => Sides.Bear,
            _ => Sides.Savage,
        };

        var botLevel = randomUtil.GetInt(10, 45);
        var botId = new MongoId();

        var generationDetails = new BotGenerationDetails
        {
            IsPmc = isPmc,
            Role = role,
            RoleLowercase = role.ToLowerInvariant(),
            Side = side,
            PlayerLevel = botLevel,
            BotRelativeLevelDeltaMax = 0,
            BotRelativeLevelDeltaMin = 0,
            BotCountToGenerate = 1,
            IsPlayerScav = false,
            AllPmcsHaveSameNameAsPlayer = false,
            ClearBotContainerCacheAfterGeneration = true,
            BotLevel = botLevel,
            GameVersion = GameEditions.STANDARD,
        };

        try
        {
            return botInventoryGenerator.GenerateInventory(botId, sessionId, template, generationDetails);
        }
        catch (Exception ex)
        {
            logger.Error($"[RandomKitTrader] Failed generating inventory for role '{role}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Set every equipment/weaponMod/equipmentMod chance we care about to 100% so the roll always happens</summary>
    private void ForceChancesToMax(Chances chances)
    {
        foreach (var slot in AllRolledSlots)
        {
            chances.EquipmentChances[slot.ToString()] = 100;
        }

        foreach (var key in chances.WeaponModsChances.Keys.ToList())
        {
            chances.WeaponModsChances[key] = 100;
        }

        foreach (var key in chances.EquipmentModsChances.Keys.ToList())
        {
            chances.EquipmentModsChances[key] = 100;
        }
    }

    /// <summary>Pick 2-4 random medical items (bandages/meds/painkillers/stims) as standalone mail items</summary>
    private List<Item> GenerateRandomMeds()
    {
        _medsPool ??= templateTable
            .Items.Values.Where(item => item.Properties?.QuestItem != true && itemHelper.IsOfBaseclasses(item.Id, MedsBaseClasses))
            .Select(item => item.Id)
            .ToList();

        return PickRandomStandaloneItems(_medsPool, randomUtil.GetInt(2, 4));
    }

    /// <summary>Pick 1-3 random grenades/throwables as standalone mail items</summary>
    private List<Item> GenerateRandomGrenades()
    {
        _grenadePool ??= templateTable
            .Items.Values.Where(item => item.Properties?.QuestItem != true && itemHelper.IsOfBaseclass(item.Id, BaseClasses.THROW_WEAP))
            .Select(item => item.Id)
            .ToList();

        return PickRandomStandaloneItems(_grenadePool, randomUtil.GetInt(1, 3));
    }

    /// <summary>
    /// 0-4 completely wildcard items pulled from the entire item database - no base-class
    /// restriction at all, unlike the meds/grenades above. This is separate from (and in addition
    /// to) the guaranteed weapon/armor/meds/grenades - just a random grab-bag stuffed in alongside
    /// everything else.
    /// </summary>
    private List<Item> GenerateRandomBonusItems()
    {
        _bonusItemPool ??= templateTable
            .Items.Values.Where(item =>
                item.Type == "Item"
                && item.Parent != MongoId.Empty()
                && item.Properties?.QuestItem != true
                && !itemFilterService.IsItemBlacklisted(item.Id)
            )
            .Select(item => item.Id)
            .ToList();

        return PickRandomStandaloneItems(_bonusItemPool, randomUtil.GetInt(0, 4));
    }

    /// <summary>Pick `count` random tpls from `pool`, each as its own standalone single-stack mail item.</summary>
    private List<Item> PickRandomStandaloneItems(List<MongoId> pool, int count)
    {
        if (pool.Count == 0 || count <= 0)
        {
            return [];
        }

        var picked = new List<Item>();
        for (var i = 0; i < count; i++)
        {
            var tpl = randomUtil.GetArrayValue(pool);
            picked.Add(
                new Item
                {
                    Id = new MongoId(),
                    Template = tpl,
                    Upd = new Upd { StackObjectsCount = 1 },
                }
            );
        }

        return picked;
    }
}
