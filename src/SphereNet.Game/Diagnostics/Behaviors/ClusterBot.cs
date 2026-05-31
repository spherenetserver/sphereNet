namespace SphereNet.Game.Diagnostics.Behaviors;

/// <summary>
/// Worst-case broadcast-amplification load generator. Paired with the engine's
/// cluster spawn (the whole fleet packs around one city centre, in mutual view),
/// each bot speaks frequently. Speech fans out to every other bot in range, so
/// N bots talking is ~N^2 outgoing packets per round — the per-recipient packet
/// build + serial flush path that spread-out or idling bots never exercise.
///
/// Speech is used rather than movement on purpose: a dense crowd cannot actually
/// keep moving (mobiles block each other, so most steps are rejected and the
/// movement-broadcast storm throttles itself), whereas speech is not collision
/// limited and keeps the broadcast path saturated. A light jitter step is mixed
/// in so some movement broadcasts happen too.
/// </summary>
public sealed class ClusterBot : IBotBehavior
{
    public string Name => "Cluster";

    private readonly Random _rng;

    private static readonly string[] Chatter =
    {
        "Hail!", "Well met!", "Anyone selling?", "WTS regs", "Guard!",
        "*laughs*", "incoming", "rez plz", "on me", "fall back",
    };

    public ClusterBot(int seed) => _rng = new Random(seed);

    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && bot.State == BotState.Playing)
        {
            // Speak — broadcasts to every bot in view (the amplification path).
            await actions.Say(Chatter[_rng.Next(Chatter.Length)], ct);

            // Occasional jitter step so movement broadcasts fire too (mostly
            // rejected in the crush, which is itself realistic).
            if (_rng.Next(100) < 25)
                await actions.MoveDirection((byte)_rng.Next(0, 8), ct);

            await Task.Delay(_rng.Next(300, 600), ct);
        }
    }
}
