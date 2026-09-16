using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils;
using Path = System.IO.Path;

namespace RandomKitTrader;

/// <summary>
/// Mod metadata - the C# replacement for the old package.json. Every field here must be filled in.
/// </summary>
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.connor.randomkittrader";
    public string Name { get; init; } = "RandomKitTrader";
    public string Author { get; init; } = "Connor";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");

    // SPT 4.1.5 - if the server reports an incompatible version when it loads, bump this to match.
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");

    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}

/// <summary>
/// Registers the "Randomizer" trader, creates the free "Random Kit" item it sells, and stashes
/// references the (necessarily static) Harmony purchase-detection patch needs into
/// <see cref="RandomKitTraderContext"/>, since Harmony patch methods can't use constructor injection.
/// </summary>
// Custom items must be created before player profiles load - registering this at TraderRegistration
// (well before PostLoad) satisfies that on SPT 4.1.5's stricter CustomItemService check.
[Injectable(TypePriority = OnLoadOrder.TraderRegistration)]
public class RandomKitTraderMod(
    ISptLogger<RandomKitTraderMod> logger,
    ModHelper modHelper,
    ImageRouter imageRouter,
    TraderConfig traderConfig,
    TimeUtil timeUtil,
    TraderRegistrationHelper traderRegistrationHelper,
    FluentTraderAssortCreator fluentTraderAssortCreator,
    TemplateTable templateTable,
    TradersTable tradersTable,
    RandomUtil randomUtil,
    CustomItemService customItemService,
    KitGenerator kitGenerator,
    SPTarkov.Server.Core.Services.Commerce.MailSendService mailSendService
) : IOnLoad
{
    /// <summary>Fixed, unique tpl id for the "Random Kit" item this mod creates. Must be a valid 24-char hex mongo id.</summary>
    private static readonly MongoId RandomKitItemTplId = new("68c00d000000000000000001");

    /// <summary>Fixed, unique id for the single assort entry the trader sells (used to detect the purchase in <see cref="BuyItemPatch"/>).</summary>
    private static readonly MongoId RandomKitAssortItemId = new("68c00d000000000000000002");

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var traderImagePath = Path.Combine(pathToMod, "db", "trader_icon.jpg");
        var traderBase = modHelper.GetJsonDataFromFile<TraderBase>(pathToMod, Path.Combine("db", "base.json"));

        // Register the trader's icon, refresh timer, empty assort slot and locale text
        imageRouter.AddRoute(traderBase.Avatar!.Replace(".jpg", string.Empty), traderImagePath);
        traderRegistrationHelper.SetTraderUpdateTime(traderConfig, traderBase, timeUtil.GetHoursAsSeconds(1), timeUtil.GetHoursAsSeconds(1));
        traderRegistrationHelper.AddTraderWithEmptyAssortToDb(traderBase);
        traderRegistrationHelper.AddTraderToLocales(
            traderBase,
            "The Randomizer",
            "Nobody knows where The Randomizer gets their gear, and The Randomizer isn't telling. Buy a Random Kit and find out what you get - it's always a surprise, even to them."
        );

        // Clone an existing, harmless item (duct tape) to use as the base for our free "Random Kit" item.
        // We look the donor item's category/handbook parent up dynamically rather than hardcoding them so
        // this keeps working even if those category ids ever change in a future game update.
        var donorTpl = ItemTpl.BARTER_DUCT_TAPE;
        var donorItem = templateTable.Items.GetValueOrDefault(donorTpl);
        var donorHandbookParentId = templateTable.Handbook.Items.FirstOrDefault(entry => entry.Id == donorTpl)?.ParentId;
        // "Barter items" handbook category - used only if the donor item's own handbook entry can't be found
        const string fallbackHandbookParentId = "5b47574386f77428ca22b2ee";

        var createResult = customItemService.CreateItemFromClone(
            new NewItemFromCloneDetails
            {
                ItemTplToClone = donorTpl,
                NewId = RandomKitItemTplId,
                NewItemName = "randomkit_random_kit",
                ParentId = donorItem?.Parent ?? donorTpl,
                HandbookParentId = donorHandbookParentId?.ToString() ?? fallbackHandbookParentId,
                HandbookPriceRoubles = 1,
                AddToFleaPriceDb = false, // it's not a "real" item, keep it off the flea market
                AddToWeaponShelf = false,
                Locales = new Dictionary<string, LocaleDetails>
                {
                    {
                        "en",
                        new LocaleDetails
                        {
                            Name = "Random Kit",
                            ShortName = "Random Kit",
                            Description =
                                "A mysterious, unmarked kit. Nobody - including The Randomizer - knows what's inside until you open the mail. "
                                + "Could be a full loadout: weapon, armor, helmet, mask, rig, backpack, meds and grenades - every piece rolled "
                                + "completely independently, so it's never a matching set.",
                        }
                    },
                },
            }
        );

        if (!createResult.Success)
        {
            logger.Error($"[RandomKitTrader] Failed to create the Random Kit item: {string.Join(", ", createResult.Errors)}");
            return Task.CompletedTask;
        }

        // Add the Random Kit as the trader's single assort item. The price here is just a
        // placeholder (a 0 cost breaks the client's purchase-requirement check) - RandomKitPriceRotator
        // rerolls it to a real random value immediately below, then keeps rerolling it every 2 minutes.
        fluentTraderAssortCreator
            .CreateSingleAssortItem(RandomKitItemTplId, RandomKitAssortItemId)
            .AddUnlimitedStackCount()
            .AddMoneyCost(Money.ROUBLES, 1)
            .AddLoyaltyLevel(1)
            .Export(traderBase.Id);

        // Kick off the price rotator: 1-200,000 roubles, rerolled every 2 minutes for as long as the server runs
        RandomKitPriceRotator.Start(tradersTable, randomUtil, traderBase.Id, RandomKitAssortItemId, logger);

        // Hand off everything the static Harmony patch needs, since it can't use DI itself
        RandomKitTraderContext.Configure(traderBase.Id, RandomKitAssortItemId, kitGenerator, mailSendService, logger);

        // Enable the purchase-detection patch now that the context above is ready
        new BuyItemPatch().Enable();

        logger.Success("[RandomKitTrader] Randomizer trader and Random Kit registered successfully");

        return Task.CompletedTask;
    }
}
