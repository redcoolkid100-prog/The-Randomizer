using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace RandomKitTrader;

/// <summary>
/// Not related to the Random Kit trader - this is a small standalone convenience feature bundled
/// into the same mod folder so there's nothing extra to install: it silently unlocks Icebreaker for
/// direct map selection (the ManimalIcebreaker mod's own feature - see its "maplock.json", which
/// names a single quest id as the "finalQuestId" that has to be marked Success before its
/// IcebreakerLockRouter will let the map be picked directly instead of requiring a transit from
/// Shoreline). That quest is "Boreas - Part 3" (id 9d5e3f7d6320a7fd139a2772), the end of a long,
/// multi-trader questline. Rather than actually grinding that questline, this just injects a
/// completed ("Success") record for that one quest id directly into every profile on the server -
/// it doesn't touch ManimalIcebreaker's own files at all, so it keeps working across updates to that
/// mod as long as it keeps checking the same quest id.
///
/// This is a shortcut, not the intended way to unlock the map - your quest log will likely still
/// show the earlier Boreas quests as not done, since this skips straight to the end state rather
/// than legitimately completing each step (and any rewards those earlier quests would have given
/// you won't be granted). Purely cosmetic/inventory-reward side effects aside, this shouldn't harm
/// your save - it only adds one quest-status entry, it doesn't touch anything else.
///
/// Runs once per server start, after profiles have loaded (SaveCallbacks, which is what actually
/// loads them into SaveServer, runs earlier - at OnLoadOrder.SaveCallbacks - so registering this at
/// OnLoadOrder.PostLoad guarantees profiles already exist by the time this runs). It's safe to leave
/// installed indefinitely - once a profile already has the quest marked Success, it's skipped.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad)]
public class IcebreakerBoreasUnlocker(ISptLogger<IcebreakerBoreasUnlocker> logger, SaveServer saveServer, TimeUtil timeUtil) : IOnLoad
{
    /// <summary>
    /// "Boreas - Part 3" - the exact quest id ManimalIcebreaker's own maplock.json names as the
    /// thing that has to be marked complete before Icebreaker can be picked directly from the map
    /// menu. If a future ManimalIcebreaker update changes which quest gates the map, update this to
    /// match its new maplock.json.
    /// </summary>
    private static readonly MongoId FinalBoreasQuestId = new("9d5e3f7d6320a7fd139a2772");

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        foreach (var (sessionId, profile) in saveServer.GetProfiles())
        {
            var pmc = profile.CharacterData?.PmcData;
            if (pmc is null)
            {
                continue;
            }

            pmc.Quests ??= [];

            if (pmc.Quests.Any(quest => quest.QId == FinalBoreasQuestId && quest.Status == QuestStatusEnum.Success))
            {
                // Already unlocked (either legitimately, or by this same code on a previous start) - nothing to do.
                continue;
            }

            // Remove any partial/in-progress record for this quest before adding the completed one.
            pmc.Quests.RemoveAll(quest => quest.QId == FinalBoreasQuestId);

            var now = timeUtil.GetTimeStamp();
            pmc.Quests.Add(
                new QuestStatus
                {
                    QId = FinalBoreasQuestId,
                    StartTime = now,
                    Status = QuestStatusEnum.Success,
                    StatusTimers = new Dictionary<QuestStatusEnum, double> { { QuestStatusEnum.Started, now }, { QuestStatusEnum.Success, now } },
                    CompletedConditions = [],
                }
            );

            try
            {
                await saveServer.SaveProfileAsync(sessionId, cancellationToken);
                logger.Success($"[IcebreakerUnlock] Marked 'Boreas - Part 3' complete for profile {sessionId} - Icebreaker should now be directly selectable from the map menu.");
            }
            catch (Exception ex)
            {
                logger.Error($"[IcebreakerUnlock] Updated profile {sessionId} in memory but failed to save it to disk: {ex.Message}");
            }
        }
    }
}
