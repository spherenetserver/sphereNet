using SphereNet.Game.Diagnostics.Behaviors;

namespace SphereNet.Game.Diagnostics.Scenarios;

public sealed class WalkTalkScenario : IBotScenario
{
    public string Name => "WalkTalk";
    public string Description => "80% Walker + 20% Social — basic movement and chat stress";
    public int DefaultBotCount => 100;
    public int DefaultDurationMinutes => 15;
    public BotSpawnCity SpawnCity => BotSpawnCity.Britain;

    public IReadOnlyList<(IBotBehavior behavior, int count)> GetBotDistribution(int totalBots)
    {
        int social = totalBots / 5;
        int walker = totalBots - social;
        return
        [
            (new WalkerBot(1), walker),
            (new SocialBot(2), social),
        ];
    }
}
