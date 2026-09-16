using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Cloners;

namespace RandomKitTrader;

/// <summary>
/// Small helper used to register our custom trader with the server - based on the official
/// SPT trader mod example (13.1AddTraderWithDynamicAssorts).
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration)]
public class TraderRegistrationHelper(ICloner cloner, TradersTable tradersTable, LocaleTable localeTable)
{
    /// <summary>
    /// Add the trader's stock-refresh timing to the trader config
    /// </summary>
    public void SetTraderUpdateTime(TraderConfig traderConfig, TraderBase baseJson, int refreshTimeSecondsMin, int refreshTimeSecondsMax)
    {
        var traderRefreshRecord = new UpdateTime
        {
            TraderId = baseJson.Id,
            Seconds = new MinMax<int>(refreshTimeSecondsMin, refreshTimeSecondsMax),
        };

        traderConfig.UpdateTime.Add(traderRefreshRecord);
    }

    /// <summary>
    /// Add our trader's base data (no assort items yet) to the server database
    /// </summary>
    public void AddTraderWithEmptyAssortToDb(TraderBase traderDetailsToAdd)
    {
        var emptyTraderItemAssortObject = new TraderAssort
        {
            Items = [],
            BarterScheme = new Dictionary<MongoId, List<List<BarterScheme>>>(),
            LoyalLevelItems = new Dictionary<MongoId, int>(),
        };

        var traderDataToAdd = new Trader
        {
            Assort = emptyTraderItemAssortObject,
            Base = cloner.Clone(traderDetailsToAdd),
            QuestAssort = new()
            {
                { "Started", new() },
                { "Success", new() },
                { "Fail", new() },
            },
            Dialogue = [],
        };

        if (!tradersTable.TryAdd(traderDetailsToAdd.Id, traderDataToAdd))
        {
            // Failed to add trader - id likely already in use
        }
    }

    /// <summary>
    /// Add the trader's name/nickname/location/description to every locale so it displays correctly
    /// regardless of the player's language setting
    /// </summary>
    public void AddTraderToLocales(TraderBase baseJson, string firstName, string description)
    {
        var locales = localeTable.Global;
        var newTraderId = baseJson.Id;
        var fullName = baseJson.Name;
        var nickName = baseJson.Nickname;
        var location = baseJson.Location;

        foreach (var (_, localeKvP) in locales)
        {
            localeKvP.AddTransformer(lazyLoadedLocaleData =>
            {
                lazyLoadedLocaleData.Add($"{newTraderId} FullName", fullName);
                lazyLoadedLocaleData.Add($"{newTraderId} FirstName", firstName);
                lazyLoadedLocaleData.Add($"{newTraderId} Nickname", nickName);
                lazyLoadedLocaleData.Add($"{newTraderId} Location", location);
                lazyLoadedLocaleData.Add($"{newTraderId} Description", description);
                return lazyLoadedLocaleData;
            });
        }
    }
}
