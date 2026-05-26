namespace SphereNet.Game.Diagnostics.Behaviors;

public sealed class WalkerBot : IBotBehavior
{
    public string Name => "Walker";

    private readonly Random _rng;

    public WalkerBot(int seed) => _rng = new Random(seed);

    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && bot.State == BotState.Playing)
        {
            byte dir = (byte)_rng.Next(0, 8);
            bool run = _rng.Next(100) < 30;
            if (run) dir |= 0x80;

            await actions.MoveDirection(dir, ct);

            int delay = run ? _rng.Next(100, 250) : _rng.Next(200, 450);
            await Task.Delay(delay, ct);

            if (_rng.Next(100) < 5)
                await actions.Say(PickChat(), ct);
        }
    }

    private string PickChat()
    {
        var lines = new[] { "Hail!", "Well met!", "Good day!", "*looks around*", "*stretches*" };
        return lines[_rng.Next(lines.Length)];
    }
}
