namespace SphereNet.Game.Diagnostics.Behaviors;

public sealed class CombatBot : IBotBehavior
{
    public string Name => "Combat";

    private readonly Random _rng;
    private bool _warMode;

    public CombatBot(int seed) => _rng = new Random(seed);

    /// <summary>One decision per call, then return. The runner's behavior loop
    /// drains incoming packets between calls, so the bot's world model stays
    /// fresh and it actually reacts to visible monsters — a behavior that loops
    /// internally never lets the socket be read and so fights blind.</summary>
    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        var world = bot.World;

        if (world.IsDead)
        {
            if (_warMode) { await actions.SetWarMode(false, ct); _warMode = false; }
            var healer = world.FindNearest(m => m.IsLikelyHealer);
            if (healer != null) await actions.MoveTo(healer.X, healer.Y, 4000, ct);
            else await actions.MoveDirection((byte)_rng.Next(0, 8), ct);
            await Task.Delay(1500, ct);
            return;
        }

        if (!_warMode) { await actions.SetWarMode(true, ct); _warMode = true; }

        if (world.Hits < world.MaxHits / 4)
        {
            // Flee: a running step away.
            await actions.MoveDirection((byte)(_rng.Next(0, 8) | 0x80), ct);
            await Task.Delay(250, ct);
            return;
        }

        var target = world.FindNearest(m => m.IsMonster, 12);
        if (target != null)
        {
            if (world.DistanceTo(target.X, target.Y) > 1)
                await actions.MoveTo(target.X, target.Y, 2000, ct);
            await actions.Attack(target.Serial, ct);
            await Task.Delay(_rng.Next(600, 1200), ct);
        }
        else
        {
            await actions.MoveDirection((byte)_rng.Next(0, 8), ct);
            await Task.Delay(_rng.Next(300, 600), ct);
        }
    }
}
