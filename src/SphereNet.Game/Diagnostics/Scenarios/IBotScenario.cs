using SphereNet.Game.Diagnostics.Behaviors;

namespace SphereNet.Game.Diagnostics.Scenarios;

public interface IBotScenario
{
    string Name { get; }
    string Description { get; }
    int DefaultBotCount { get; }
    int DefaultDurationMinutes { get; }
    BotSpawnCity SpawnCity { get; }

    IReadOnlyList<(IBotBehavior behavior, int count)> GetBotDistribution(int totalBots);
}
