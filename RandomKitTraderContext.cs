using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services.Commerce;

namespace RandomKitTrader;

/// <summary>
/// Harmony patches (see <see cref="BuyItemPatch"/>) run as plain static methods outside of the DI
/// container, so they can't take constructor-injected dependencies the way every other class in this
/// mod can. This static class is populated once, from <see cref="RandomKitTraderMod.OnLoadAsync"/>
/// (which *does* get real DI-injected services), and the patch reads back out of it at purchase time.
/// </summary>
internal static class RandomKitTraderContext
{
    public static MongoId TraderId { get; private set; }
    public static MongoId AssortItemId { get; private set; }
    public static KitGenerator? KitGenerator { get; private set; }
    public static MailSendService? MailSendService { get; private set; }
    public static ISptLogger<RandomKitTraderMod>? Logger { get; private set; }

    public static bool IsReady => KitGenerator is not null && MailSendService is not null;

    public static void Configure(
        MongoId traderId,
        MongoId assortItemId,
        KitGenerator kitGenerator,
        MailSendService mailSendService,
        ISptLogger<RandomKitTraderMod> logger
    )
    {
        TraderId = traderId;
        AssortItemId = assortItemId;
        KitGenerator = kitGenerator;
        MailSendService = mailSendService;
        Logger = logger;
    }
}
