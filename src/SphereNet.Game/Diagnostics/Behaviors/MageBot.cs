namespace SphereNet.Game.Diagnostics.Behaviors;

/// <summary>
/// Reactive spellcaster: finds the nearest monster and casts an offensive spell
/// at it via the cast→target flow. Relies on the per-bot receive loop keeping the
/// world model current (so it sees real targets) and on the bot combat/magic buff
/// (Magery/Int/Mana). One decision per call so the receive loop drains between
/// casts.
/// </summary>
public sealed class MageBot : IBotBehavior
{
    public string Name => "Mage";

    private readonly Random _rng;

    // UO spell numbers (the 0x12/0x56 cast command takes these): Magic Arrow (5),
    // Harm (17), Fireball (30), Lightning (42) — low/mid-circle offensive spells.
    private static readonly int[] Spells = { 5, 17, 30, 42 };

    public MageBot(int seed) => _rng = new Random(seed);

    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        var world = bot.World;
        var target = world.FindNearest(m => m.IsMonster, 10);

        if (target != null)
        {
            int spell = Spells[_rng.Next(Spells.Length)];
            await actions.CastSpell(spell, target.Serial, 3000, ct);
            await Task.Delay(_rng.Next(900, 1600), ct);
        }
        else
        {
            await actions.MoveDirection((byte)_rng.Next(0, 8), ct);
            await Task.Delay(_rng.Next(300, 600), ct);
        }
    }
}
