using SphereNet.Game.Diagnostics.Behaviors;

namespace SphereNet.Game.Diagnostics.Scenarios;

public sealed class CombatSoakScenario : IBotScenario
{
    public string Name => "CombatSoak";
    public string Description => "50% Combat + 30% Walker + 20% Social — combat dungeon simulation";
    public int DefaultBotCount => 100;
    public int DefaultDurationMinutes => 30;
    public BotSpawnCity SpawnCity => BotSpawnCity.All;

    public IReadOnlyList<(IBotBehavior behavior, int count)> GetBotDistribution(int totalBots)
    {
        int combat = totalBots / 2;
        int walker = totalBots * 30 / 100;
        int social = totalBots - combat - walker;
        return
        [
            (new Behaviors.CombatBot(1), combat),
            (new WalkerBot(2), walker),
            (new SocialBot(3), social),
        ];
    }
}
