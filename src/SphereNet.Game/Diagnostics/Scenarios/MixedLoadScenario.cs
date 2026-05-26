using SphereNet.Game.Diagnostics.Behaviors;

namespace SphereNet.Game.Diagnostics.Scenarios;

public sealed class MixedLoadScenario : IBotScenario
{
    public string Name => "MixedLoad";
    public string Description => "20% each role — comprehensive mixed load test";
    public int DefaultBotCount => 100;
    public int DefaultDurationMinutes => 30;
    public BotSpawnCity SpawnCity => BotSpawnCity.All;

    public IReadOnlyList<(IBotBehavior behavior, int count)> GetBotDistribution(int totalBots)
    {
        int perRole = totalBots / 5;
        int remainder = totalBots - perRole * 5;
        return
        [
            (new WalkerBot(1), perRole + remainder),
            (new Behaviors.CombatBot(2), perRole),
            (new VendorBot(3), perRole),
            (new LootBot(4), perRole),
            (new SocialBot(5), perRole),
        ];
    }
}
