namespace SphereNet.Game.Diagnostics.Behaviors;

public sealed class SkillBot : IBotBehavior
{
    public string Name => "Skill";

    private readonly Random _rng;

    private static readonly int[] SafeSkills =
    [
        2,  // Alchemy
        7,  // Blacksmithy
        8,  // Bowcraft
        11, // Carpentry
        12, // Cartography
        23, // Inscription
        25, // Magery (cast)
        34, // Tailoring
        37, // Tinkering
        44, // Lumberjacking
        45, // Mining
    ];

    public SkillBot(int seed) => _rng = new Random(seed);

    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && bot.State == BotState.Playing)
        {
            int skillId = SafeSkills[_rng.Next(SafeSkills.Length)];
            await actions.UseSkill(skillId, 3000, ct);
            await Task.Delay(_rng.Next(1000, 3000), ct);

            if (bot.World.HasPendingTarget)
            {
                await actions.TargetLocation(bot.World.X, bot.World.Y, bot.World.Z, ct);
                await Task.Delay(500, ct);
            }

            if (_rng.Next(100) < 20)
            {
                byte dir = (byte)_rng.Next(0, 8);
                await actions.MoveDirection(dir, ct);
                await Task.Delay(300, ct);
            }
        }
    }
}
