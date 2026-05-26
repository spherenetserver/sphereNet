namespace SphereNet.Game.Diagnostics.Behaviors;

public sealed class CombatBot : IBotBehavior
{
    public string Name => "Combat";

    private readonly Random _rng;

    public CombatBot(int seed) => _rng = new Random(seed);

    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        await actions.SetWarMode(true, ct);

        while (!ct.IsCancellationRequested && bot.State == BotState.Playing)
        {
            var world = bot.World;

            if (world.IsDead)
            {
                await actions.SetWarMode(false, ct);
                var healer = world.FindNearest(m => m.IsLikelyHealer);
                if (healer != null)
                {
                    await actions.MoveTo(healer.X, healer.Y, 8000, ct);
                    await Task.Delay(2000, ct);
                }
                else
                {
                    await WanderAsync(actions, ct);
                }
                continue;
            }

            if (world.Hits < world.MaxHits / 4)
            {
                await FleeAsync(actions, ct);
                continue;
            }

            var target = world.FindNearest(m => m.IsMonster, 12);
            if (target != null)
            {
                int dist = world.DistanceTo(target.X, target.Y);
                if (dist > 1)
                    await actions.MoveTo(target.X, target.Y, 3000, ct);

                await actions.Attack(target.Serial, ct);
                await Task.Delay(_rng.Next(800, 1500), ct);
            }
            else
            {
                await WanderAsync(actions, ct);
            }
        }
    }

    private async Task WanderAsync(BotActionApi actions, CancellationToken ct)
    {
        byte dir = (byte)_rng.Next(0, 8);
        await actions.MoveDirection(dir, ct);
        await Task.Delay(_rng.Next(300, 600), ct);
    }

    private async Task FleeAsync(BotActionApi actions, CancellationToken ct)
    {
        for (int i = 0; i < 5 && !ct.IsCancellationRequested; i++)
        {
            byte dir = (byte)(_rng.Next(0, 8) | 0x80);
            await actions.MoveDirection(dir, ct);
            await Task.Delay(150, ct);
        }
        await Task.Delay(2000, ct);
    }
}
