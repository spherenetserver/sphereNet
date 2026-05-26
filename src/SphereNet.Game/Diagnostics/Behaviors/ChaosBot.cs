namespace SphereNet.Game.Diagnostics.Behaviors;

public sealed class ChaosBot : IBotBehavior
{
    public string Name => "Chaos";

    private readonly Random _rng;

    public ChaosBot(int seed) => _rng = new Random(seed);

    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && bot.State == BotState.Playing)
        {
            int action = _rng.Next(100);

            if (action < 30)
            {
                byte dir = (byte)_rng.Next(0, 8);
                if (_rng.Next(2) == 0) dir |= 0x80;
                await actions.MoveDirection(dir, ct);
            }
            else if (action < 45)
            {
                uint serial = (uint)(0x40000001 + _rng.Next(0, 10000));
                await actions.DoubleClick(serial, 1000, ct);
            }
            else if (action < 55)
            {
                var target = bot.World.FindNearest(_ => true, 18);
                if (target != null)
                    await actions.Attack(target.Serial, ct);
            }
            else if (action < 65)
            {
                int skillId = _rng.Next(0, 55);
                await actions.UseSkill(skillId, 2000, ct);
            }
            else if (action < 75)
            {
                bool war = !bot.World.IsWarMode;
                await actions.SetWarMode(war, ct);
            }
            else if (action < 85)
            {
                string text = $"chaos_{_rng.Next(1000)}";
                await actions.Say(text, ct);
            }
            else if (action < 92)
            {
                uint serial = (uint)(0x40000001 + _rng.Next(0, 5000));
                await actions.PickUp(serial, 1, 1000, ct);
            }
            else
            {
                if (bot.World.HasPendingTarget)
                    await actions.TargetLocation(
                        bot.World.X + _rng.Next(-5, 6),
                        bot.World.Y + _rng.Next(-5, 6),
                        bot.World.Z, ct);
            }

            await Task.Delay(_rng.Next(200, 800), ct);
        }
    }
}
