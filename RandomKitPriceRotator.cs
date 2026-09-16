using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace RandomKitTrader;

/// <summary>
/// Keeps the Random Kit's rouble price constantly shifting - somewhere between 1 and 200,000
/// roubles, rerolled every 2 minutes for as long as the server stays up. Runs off a plain
/// System.Threading.Timer rather than anything DI-managed, since nothing else needs to hold a
/// reference to this over time; the Timer instance itself is kept in a static field so it isn't
/// garbage collected between ticks.
/// </summary>
internal static class RandomKitPriceRotator
{
    private const int MinPriceRoubles = 1;
    private const int MaxPriceRoubles = 200_000;
    private static readonly TimeSpan RotationInterval = TimeSpan.FromMinutes(2);

    private static System.Threading.Timer? _timer;

    public static void Start(TradersTable tradersTable, RandomUtil randomUtil, MongoId traderId, MongoId assortItemId, ISptLogger<RandomKitTraderMod> logger)
    {
        // Roll a real price immediately (the assort was created with a throwaway placeholder cost),
        // then keep rerolling on a fixed interval for as long as the server process is alive.
        RerollPrice(tradersTable, randomUtil, traderId, assortItemId, logger);

        _timer = new System.Threading.Timer(
            _ => RerollPrice(tradersTable, randomUtil, traderId, assortItemId, logger),
            null,
            RotationInterval,
            RotationInterval
        );
    }

    private static void RerollPrice(TradersTable tradersTable, RandomUtil randomUtil, MongoId traderId, MongoId assortItemId, ISptLogger<RandomKitTraderMod> logger)
    {
        try
        {
            var trader = tradersTable.GetValueOrDefault(traderId);
            var moneyCost = trader?.Assort.BarterScheme.GetValueOrDefault(assortItemId)?.FirstOrDefault()?.FirstOrDefault();

            if (moneyCost is null)
            {
                logger.Warning("[RandomKitTrader] Could not find the Random Kit's price entry to reroll - skipping this cycle");
                return;
            }

            var newPrice = randomUtil.GetInt(MinPriceRoubles, MaxPriceRoubles);
            moneyCost.Template = Money.ROUBLES;
            moneyCost.Count = newPrice;

            logger.Info($"[RandomKitTrader] Random Kit price rerolled to {newPrice} roubles");
        }
        catch (Exception ex)
        {
            logger.Error($"[RandomKitTrader] Failed to reroll the Random Kit's price: {ex}");
        }
    }
}
