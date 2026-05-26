using SphereNet.Game.Diagnostics.Behaviors;

namespace SphereNet.Game.Diagnostics.Scenarios;

public sealed class LoginStormScenario : IBotScenario
{
    public string Name => "LoginStorm";
    public string Description => "All bots login within 10 seconds — login server stress test";
    public int DefaultBotCount => 100;
    public int DefaultDurationMinutes => 5;
    public BotSpawnCity SpawnCity => BotSpawnCity.Britain;

    public IReadOnlyList<(IBotBehavior behavior, int count)> GetBotDistribution(int totalBots)
    {
        return [(new WalkerBot(1), totalBots)];
    }
}
