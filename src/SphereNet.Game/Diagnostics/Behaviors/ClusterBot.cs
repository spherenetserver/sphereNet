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

    // Stay within this many tiles of the spawn anchor. Cluster spawn already
    // packs every bot near one city centre, so anchoring each bot to its own
    // spawn keeps the whole fleet inside a single screen (mutual view) instead
    // of letting random jitter disperse it — which is what makes each speech
    // actually fan out to ~everyone.
    private const int Radius = 6;
    private int _anchorX = -1, _anchorY;

    /// <summary>One iteration of behavior, then return. The runner's behavior
    /// loop (RunRoleBehaviorAsync) drains incoming packets between calls, so
    /// RunAsync must NOT loop forever — otherwise a speak-only bot never reads
    /// its socket, its inbound backs up, and the server sheds everything bound
    /// for it (the connection looks hopelessly behind). Returning each iteration
    /// lets the bot actually consume the broadcast flood it is generating.</summary>
    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        var w = bot.World;
        if (_anchorX < 0 && w.X > 0) { _anchorX = w.X; _anchorY = w.Y; }

        if (_anchorX >= 0 && (Math.Abs(w.X - _anchorX) > Radius || Math.Abs(w.Y - _anchorY) > Radius))
        {
            // Drifted out of the cluster — walk back toward the anchor.
            await actions.MoveTo(_anchorX, _anchorY, 3000, ct);
        }
        else
        {
            // Inside the cluster — speak (broadcasts to everyone in view), with
            // an occasional jitter step.
            await actions.Say(Chatter[_rng.Next(Chatter.Length)], ct);
            if (_rng.Next(100) < 20)
                await actions.MoveDirection((byte)_rng.Next(0, 8), ct);
        }

        await Task.Delay(_rng.Next(250, 450), ct);
    }
}
