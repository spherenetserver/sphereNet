using SphereNet.Game.Diagnostics.Behaviors;

namespace SphereNet.Game.Diagnostics.Scenarios;

public sealed class VendorCycleScenario : IBotScenario
{
    public string Name => "VendorCycle";
    public string Description => "60% Vendor + 20% Walker + 20% Social — vendor buy/sell loop";
    public int DefaultBotCount => 100;
    public int DefaultDurationMinutes => 20;
    public BotSpawnCity SpawnCity => BotSpawnCity.Britain;

    public IReadOnlyList<(IBotBehavior behavior, int count)> GetBotDistribution(int totalBots)
    {
        int vendor = totalBots * 60 / 100;
        int walker = totalBots * 20 / 100;
        int social = totalBots - vendor - walker;
        return
        [
            (new VendorBot(1), vendor),
            (new WalkerBot(2), walker),
            (new SocialBot(3), social),
        ];
    }
}
