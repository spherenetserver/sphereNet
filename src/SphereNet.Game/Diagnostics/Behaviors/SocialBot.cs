namespace SphereNet.Game.Diagnostics.Behaviors;

public sealed class SocialBot : IBotBehavior
{
    public string Name => "Social";

    private readonly Random _rng;

    private static readonly string[] Chats =
    [
        "Hail, traveler!", "Well met!", "Good day!", "Anyone need a hand?",
        "Where is the nearest bank?", "How much for that?", "*waves*",
        "*bows*", "Vendor buy!", "Guards!", "Recdu!", "Recsu!",
        "Nice weather today", "Any news from the front?",
    ];

    public SocialBot(int seed) => _rng = new Random(seed);

    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && bot.State == BotState.Playing)
        {
            int action = _rng.Next(100);

            if (action < 40)
            {
                string text = Chats[_rng.Next(Chats.Length)];
                int mode = _rng.Next(3);
                if (mode == 0) await actions.Say(text, ct);
                else if (mode == 1) await actions.Yell(text, ct);
                else await actions.Whisper(text, ct);
            }
            else if (action < 70)
            {
                var target = bot.World.FindNearest(m => m.IsHumanBody, 18);
                if (target != null)
                    await actions.DoubleClick(target.Serial, 2000, ct);
            }
            else
            {
                byte dir = (byte)_rng.Next(0, 8);
                await actions.MoveDirection(dir, ct);
            }

            await Task.Delay(_rng.Next(1000, 3000), ct);
        }
    }
}
