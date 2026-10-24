namespace SphereNet.Game.PlayerBots;

/// <summary>
/// A line a bot thinks over before it answers a player: who the bot is and what it is
/// doing (<see cref="Persona"/>), the last words it had with that player, and the line
/// it answers. The engine fills it on the main thread and hands it to the
/// <see cref="IBotBrain"/>; the brain works on its own thread and calls
/// <see cref="Finish"/>. The engine reads <see cref="Done"/> each tick: the bot says the
/// reply if it came in time, and one of its own lines if it did not
/// (<see cref="Abandon"/> then tells the brain not to bother).
/// </summary>
public sealed class BotThought
{
    /// <summary>Who the bot is, where it is and what it is doing, and how to talk.</summary>
    public required string Persona { get; init; }
    /// <summary>The last exchanges with this player, oldest first: what they said, what the bot said.</summary>
    public required IReadOnlyList<(string Player, string Bot)> History { get; init; }
    /// <summary>The player's line.</summary>
    public required string Line { get; init; }
    /// <summary>The language the reply is to be in ("en", "tr").</summary>
    public required string Language { get; init; }

    private volatile bool _done;
    private volatile bool _abandoned;
    private string? _reply;

    /// <summary>The brain's reply, once <see cref="Done"/>; null when it had none.</summary>
    public string? Reply => _done ? _reply : null;
    public bool Done => _done;
    /// <summary>The engine stopped waiting: the brain skips it, or stops where it is.</summary>
    public bool Abandoned => _abandoned;
    /// <summary>How long the brain took, for the telemetry.</summary>
    public long ThinkMs { get; set; }

    /// <summary>Called by the brain, on its thread, once.</summary>
    public void Finish(string? reply)
    {
        _reply = reply;
        _done = true;
    }

    public void Abandon() => _abandoned = true;
}

/// <summary>
/// Something that writes a bot's reply to a player's line: a small language model run
/// in the server process (SphereNet.BotBrain), or a test's stand-in. It must never hold
/// up the caller: <see cref="TryThink"/> only queues the thought, and says no when the
/// brain is busy, in which case the bot answers from its own lines.
/// </summary>
public interface IBotBrain
{
    bool TryThink(BotThought thought);
}
