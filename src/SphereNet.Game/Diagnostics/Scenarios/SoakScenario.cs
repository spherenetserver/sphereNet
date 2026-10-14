using SphereNet.Game.Diagnostics.Behaviors;

namespace SphereNet.Game.Diagnostics.Scenarios;

/// <summary>A scenario long enough to show slow trends: the engine saves the world on
/// an interval, audits it after each save and judges memory growth at the end
/// (<see cref="SoakMonitor"/>).</summary>
public interface ISoakScenario : IBotScenario
{
    int SaveIntervalMinutes { get; }
    double MaxHeapGrowthMbPerHour { get; }
}

/// <summary>
/// Hours of mixed play - walking, melee, spells, vendors, looting, skills, chatter and
/// item trades between bots - with a save every 15 minutes and a consistency audit
/// after each. What a short benchmark cannot see: memory that creeps, state that drifts
/// out of step, saves that start failing under load.
/// </summary>
public sealed class SoakScenario : ISoakScenario
{
    public string Name => "Soak";
    public string Description => "Hours of mixed play with periodic saves, audits and a memory trend";
    public int DefaultBotCount => 100;
    public int DefaultDurationMinutes => 240;
    /// <summary>One city, so the traders meet each other and the load is a crowd.</summary>
    public BotSpawnCity SpawnCity => BotSpawnCity.Britain;
    public int SaveIntervalMinutes => 15;
    public double MaxHeapGrowthMbPerHour => 64;

    public IReadOnlyList<(IBotBehavior behavior, int count)> GetBotDistribution(int totalBots)
    {
        // Seven roles in about equal shares; walkers take the remainder.
        int share = totalBots / 7;
        int remainder = totalBots - share * 7;
        return
        [
            (new WalkerBot(11), share + remainder),
            (new CombatBot(12), share),
            (new MageBot(13), share),
            (new VendorBot(14), share),
            (new LootBot(15), share),
            (new SkillBot(16), share),
            (new TradeBot(17), share),
        ];
    }
}
