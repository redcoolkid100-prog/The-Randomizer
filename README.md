# Random Kit Trader (SPT 4.1.5, C#)

This folder now contains two unrelated things bundled together for convenience (so there's only
one folder to build/install): the Random Kit trader described below, and a small standalone
Icebreaker-unlock feature described near the bottom under "Bonus: unlocking Icebreaker".

Adds a trader called **The Randomizer** who sells one item: **Random Kit**, priced anywhere
from 1 to 200,000 roubles (the price rerolls itself every 2 minutes on its own, forever, for
as long as the server is running). Buying it mails you a completely randomized loadout -
weapon (with random compatible attachments, a loaded magazine, and a couple of correctly
matching loaded spare mags), armor vest, tactical rig, helmet, face cover, eyewear, earpiece,
armband, backpack, a handful of random meds and grenades, and 0-4 completely wildcard bonus
items from the entire item database. Nothing is preset: every single piece of equipment is
rolled independently against its own separate, randomly generated throwaway bot, so a kit's
helmet, armor, and weapon almost never come from the same donor - it's genuinely mixed and
matched, never a recognisable "preset." Each individual roll still goes through SPT's own
bot-equipment generator (the same system that dresses scavs/PMCs/bosses), which is what
guarantees a weapon's attachments/ammo actually fit instead of spawning broken combinations -
and since every slot's donor is drawn from literally every bot type in the game, essentially
every weapon/armor piece that's normally obtainable can show up. Any spare mags/ammo that
happened to be sitting in the independently-rolled rig or backpack are stripped out (they'd
belong to a completely unrelated donor's weapon), and replaced with a couple of mags cloned
straight off the weapon's own loaded magazine - guaranteed to fit and loaded with the same
ammo it's already carrying. The mod also remembers the exact build (every part + ammo) of the
last weapon it sold and keeps re-rolling if a new one comes out identical - some bot roles
(certain signature-weapon bosses especially) have such a narrow loadout pool that without this,
two purchases in a row could otherwise hand you the exact same gun.

## Important - please read before installing

Your first install attempt failed because this mod was originally written for the old
TypeScript-based SPT server (3.11.x), and it turns out your server is actually **SPT 4.1.5**,
which uses a completely different, C#-based server. This folder is the from-scratch C#
rewrite targeting 4.1.5.

**I could not compile or test this code myself.** This sandbox has no .NET SDK installed and
no network access to install one, so unlike the original TypeScript version (which I did
compile-check successfully), this C# version was written by carefully cross-referencing the
actual SPT 4.1.5 server source code and the official `sp-tarkov/server-mod-examples`
repository for every class, method signature, and field name it uses - but it has never
actually been run through `dotnet build`. If it fails to compile, please paste me the exact
compiler error(s) and I will fix them immediately; that's the fastest path to a working mod.

## Requirements

- The **.NET 10 SDK** installed on the machine you'll use to build this mod (not necessarily
  your SPT server machine - you can build elsewhere and just copy the output over).
  Download: https://dotnet.microsoft.com/download
- Your SPT 4.1.5 server already installed and working.

## Building

1. Open a terminal in this folder (the one with `RandomKitTrader.csproj`).
2. Run:
   ```
   dotnet build -c Release
   ```
3. NuGet will pull down `SPTarkov.Common`, `SPTarkov.DI`, `SPTarkov.Server.Core`, and
   `SPTarkov.Reflection` (all version 4.1.5) automatically - you need an internet connection
   for this step.
4. If it fails with a version-resolution error (NuGet can't find an exact `4.1.5` package),
   open `RandomKitTrader.csproj` and try the closest version NuGet does list for those four
   packages (check https://www.nuget.org/packages/SPTarkov.Server.Core for what's actually
   published), then rebuild.
5. If it fails with any other compiler error, send me the full error text - I'll patch the
   code.

## Installing

After a successful build, look inside `bin/Release/` - you'll find a folder containing
`RandomKitTrader.dll` plus a handful of other `.dll` files it depends on, and copies of
`base.json`/`trader_icon.jpg`. Copy **that entire folder's contents** into a new folder here:

```
<your SPT server folder>/user/mods/RandomKitTrader/
```

So you should end up with `user/mods/RandomKitTrader/RandomKitTrader.dll` and its sibling
files sitting right alongside it. Then start (or restart) your SPT server. Look for a log line
like `[RandomKitTrader] Randomizer trader and Random Kit registered successfully` - if you see
that, it worked. The Randomizer should now show up as a trader in Escape from Tarkov with one
item for sale: Random Kit, free, unlimited stock.

## How it works, if you want to tweak it

- **`RandomKitTraderMod.cs`** - registers the trader (name, icon, locale text) and creates the
  free "Random Kit" item by cloning an existing harmless item (duct tape) and renaming it. It
  also wires up a small static "context" object so the purchase-detection code below can reach
  the services it needs.
- **`BuyItemPatch.cs`** - SPT 4.x doesn't let mods intercept methods through the DI container
  the way the old TypeScript server could, so this uses SPTarkov's Harmony wrapper to watch
  every item purchase server-wide, and only reacts when it's specifically our Random Kit being
  bought.
- **`KitGenerator.cs`** - the actual randomization. Each equipment slot (weapon, holster,
  armor vest, rig, helmet, mask, eyewear, earpiece, armband, backpack) is generated
  *independently*: for every single slot, it picks a fresh random "donor" bot role out of
  *every* bot type the server knows about, forces that bot's equipment/attachment/ammo roll
  chances to 100%, asks SPT's own `BotInventoryGenerator` to fully dress a throwaway bot with
  that template, then lifts *just that one slot* back out as a standalone mail item and throws
  the rest of that generated bot away. That's what makes every kit a genuine mix-and-match
  instead of a recognisable preset - your helmet and your rig are very likely never from the
  same donor bot. Because the donor pool for every slot is every bot in the game rather than a
  small curated list, essentially any weapon or armor piece that's normally obtainable is
  reachable. On top it adds a handful of random meds and grenades, plus 0-4 completely wildcard
  bonus items pulled from literally the entire item database with no restrictions at all. A few
  things you can safely tweak yourself:
  - `RareRoleChancePercent` - currently 15. Bosses and their followers vastly outnumber the
    "normal" bot roles (every boss fight adds several unique follower role keys), so rolling a
    role with flat-equal odds across every role in the game meant boss-tier gear dominated
    almost every kit. This constant is the % chance any given slot's roll draws from the
    boss/follower pool instead of the normal one - raise it for more frequent fancy gear, lower
    it (or set to 0) for basically none.
  - `IsRareRole` - the boss/follower detection is just a name check (anything with "boss" or
    "follower" in its role key). If you notice a specific role landing in the wrong bucket, this
    is where to special-case it.
  - `OptionalSlots` / `MandatorySlot` - which equipment slots get rolled at all, and which one
    (the primary weapon) is required for a kit to count as valid.
  - The `2, 4` in `GenerateRandomMeds()` - how many random meds get included per kit.
  - The `1, 3` in `GenerateRandomGrenades()` - how many random grenades get included per kit.
  - The `0, 4` in `GenerateRandomBonusItems()` - the range of extra wildcard items per kit.
  - The `2, 3` in `GenerateSpareMagazines()` - how many extra loaded spare mags get cloned for the
    kit's weapon, on top of the one already loaded in it.
  - `LootContainerSlots` - which slots get their pre-rolled spare mags/ammo stripped out (currently
    just the rig and backpack, since those are the only slots SPT's bot generator stocks with loot).
  - The `8` weapon-roll attempt cap near the top of `GenerateKit()` - how many times it'll re-roll a
    fresh donor role trying to avoid repeating the exact previous build before it just gives up and
    ships whatever it last rolled anyway (so a kit is never held up indefinitely).
- **`RandomKitPriceRotator.cs`** - rerolls the Random Kit's rouble price on a timer. The
  `MinPriceRoubles`/`MaxPriceRoubles` constants (currently 1 and 200,000) and the
  `RotationInterval` (currently 2 minutes) are the two things you'd want to change here. Note
  the new price only becomes visible next time a player opens/re-opens the trader window -
  this changes the underlying server data on schedule, it doesn't push a live update to a
  trade window a player already has open.
- **`TraderRegistrationHelper.cs`** / **`FluentTraderAssortCreator.cs`** - boilerplate trader
  registration/assort-building helpers, adapted from SPT's own official mod examples.
- **`db/base.json`** - the trader's stats (name, balance, refresh timer, etc).

## Bonus: unlocking Icebreaker

**`IcebreakerBoreasUnlocker.cs`** is a completely separate feature from the trader above, bundled
into this same folder purely so there's only one thing to build and install. It only does anything
if you also have the community **ManimalIcebreaker** mod installed (the one that backports the
Icebreaker map into SPT).

That mod locks Icebreaker behind a long, multi-trader "Boreas" questline - you're only allowed to
pick it directly from the map menu (instead of transiting in from Shoreline) after finishing the
very last step, a quest called "Boreas - Part 3". Its own `maplock.json` file names that exact
quest id as the thing it checks. Rather than grinding the whole questline, this file injects a
completed record for that one quest directly into every profile on your server, once, the first
time the server starts after you install it. It doesn't touch ManimalIcebreaker's files at all -
it just edits your player profile(s), the same file the game itself writes to.

This is a shortcut, not the intended way to unlock the map, and it comes with two honest caveats:
- Your in-game quest log will likely still show the earlier Boreas quests (and quests from other
  traders that lead into it) as incomplete, since this skips straight to the finish line instead
  of actually completing each step - it just looks a little inconsistent, nothing more.
- You won't get whatever item/rep rewards those earlier quests would have given you for legitimately
  completing them.

Aside from that it's safe - it only adds one small entry to your profile's quest list, nothing else
is touched, and it's safe to leave installed permanently (it checks first and does nothing once
you're already unlocked, so it won't re-run or duplicate anything on later restarts). If you'd
rather not have this at all, just delete `IcebreakerBoreasUnlocker.cs` before building, or unlock
the map through the questline normally and never touch this feature again - either way is fine.

If ManimalIcebreaker gets updated and starts checking a different quest id, you'll see this feature
quietly do nothing (the map will still be locked) - open `IcebreakerBoreasUnlocker.cs`, check that
mod's current `maplock.json` for its new `finalQuestId`, and update the `FinalBoreasQuestId`
constant near the top of the file to match, then rebuild.

## Troubleshooting

- **"No Assemblies found in path..."** - this means the server found the mod folder but no
  `.dll` inside it. Make sure you copied the *build output* (from `bin/Release/...`), not this
  source folder, into `user/mods/RandomKitTrader/`.
- **Server logs an SPT-version-mismatch warning on load** - open `RandomKitTraderMod.cs` and
  change the `SptVersion` range (currently `~4.1.0`) to match your server more precisely, then
  rebuild.
- **Random Kit doesn't appear in The Randomizer's inventory** - check the server console log
  around startup for `[RandomKitTrader]` messages; an error there (e.g. "Failed to create the
  Random Kit item") will explain what went wrong.
- **Buying it does nothing** - check the server console for `[RandomKitTrader] Failed to
  generate/send a random kit: ...` - that'll show the actual exception.
- **Icebreaker still shows locked after installing this** - check the server console at startup for
  `[IcebreakerUnlock]` messages. If you see the "Marked ... complete" success message but the map
  still won't select directly in-game, restart the client/relaunch the game so it re-reads your
  profile, and make sure you're loading the same profile this ran against (if you have multiple
  profiles, it unlocks all of them, but double check you're not accidentally on a different one).
  If you see no `[IcebreakerUnlock]` line at all, the mod isn't loading - check for build/copy
  mistakes the same way as above.
