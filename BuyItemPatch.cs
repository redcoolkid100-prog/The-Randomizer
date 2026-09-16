using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Enums;

namespace RandomKitTrader;

/// <summary>
/// SPT's DI container can't intercept a non-virtual method the way tsyringe could in the old
/// TypeScript server, so instead this uses SPTarkov.Reflection's Harmony wrapper to postfix
/// <see cref="TradeHelper.BuyItem"/>: after a purchase goes through cleanly, check whether it was our
/// "Random Kit" assort item, and if so mail the player a freshly randomised kit.
/// </summary>
public class BuyItemPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod()
    {
        return typeof(TradeHelper).GetMethod(nameof(TradeHelper.BuyItem));
    }

    [PatchPostfix]
    public static void Postfix(PmcData pmcData, ProcessBuyTradeRequestData buyRequestData, MongoId sessionId, bool foundInRaid, ItemEventRouterResponse output)
    {
        if (!RandomKitTraderContext.IsReady)
        {
            return;
        }

        // Only fire for purchases from our trader, of our specific "Random Kit" assort entry
        if (buyRequestData.TransactionId != RandomKitTraderContext.TraderId || buyRequestData.ItemId != RandomKitTraderContext.AssortItemId)
        {
            return;
        }

        // Something went wrong with the purchase itself (e.g. not enough space) - don't reward a kit
        if (output.Warnings is { Count: > 0 })
        {
            return;
        }

        try
        {
            var kitItems = RandomKitTraderContext.KitGenerator!.GenerateKit(sessionId);

            if (kitItems.Count == 0)
            {
                RandomKitTraderContext.Logger?.Warning("[RandomKitTrader] Generated an empty kit, nothing was mailed to the player");
                return;
            }

            RandomKitTraderContext.MailSendService!.SendDirectNpcMessageToPlayer(
                sessionId,
                RandomKitTraderContext.TraderId,
                MessageType.MessageWithItems,
                "You bought a Random Kit! Here's what you got - good luck out there.",
                kitItems
            );
        }
        catch (Exception ex)
        {
            RandomKitTraderContext.Logger?.Error($"[RandomKitTrader] Failed to generate/send a random kit: {ex}");
        }
    }
}
