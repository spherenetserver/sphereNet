namespace SphereNet.Game.PlayerBots;

/// <summary>Something that really happened, worth talking about: <c>death</c> (a
/// player killed by a monster: <see cref="Actor"/> the dead, <see cref="Other"/> what
/// killed them), <c>pk</c> (a player killed by a player: actor the victim, other the
/// killer), <c>kill</c> (a big monster brought down: actor the player, other the
/// monster), <c>red</c> (a murderer seen: actor who saw, other the murderer).</summary>
public sealed record BotGossipEvent(string Kind, string Actor, uint ActorUid, string Other, uint OtherUid,
    string Place, long AtMs);

/// <summary>
/// The last things that happened, for bots to tell each other and the players
/// around them. Only real events go in, so a story a bot tells can be checked; old
/// ones drop out. Kept in memory only: yesterday's news is not worth a save.
/// </summary>
public sealed class BotGossip
{
    public const int Capacity = 48;
    public const long MaxAgeMs = 45 * 60_000;

    private readonly List<BotGossipEvent> _events = [];
    private readonly Dictionary<uint, int> _pkCounts = [];

    public IReadOnlyList<BotGossipEvent> Events => _events;

    public void Add(BotGossipEvent ev)
    {
        // The same story twice in a row is one story.
        if (_events.Count > 0 && _events[^1] is var last && last.Kind == ev.Kind &&
            last.ActorUid == ev.ActorUid && last.OtherUid == ev.OtherUid && ev.AtMs - last.AtMs < 60_000)
            return;
        if (ev.Kind == "pk") _pkCounts[ev.OtherUid] = _pkCounts.GetValueOrDefault(ev.OtherUid) + 1;
        _events.Add(ev);
        if (_events.Count > Capacity) _events.RemoveAt(0);
    }

    /// <summary>How many murders this session's news has put on <paramref name="killer"/>.</summary>
    public int PkCount(uint killer) => _pkCounts.GetValueOrDefault(killer);

    /// <summary>A fresh enough story of one of <paramref name="kinds"/>, newest more
    /// likely; null when there is none.</summary>
    public BotGossipEvent? Pick(Random rng, long now, IReadOnlyCollection<string> kinds)
    {
        _events.RemoveAll(e => now - e.AtMs > MaxAgeMs);
        BotGossipEvent? pick = null;
        int seen = 0;
        for (int i = _events.Count - 1; i >= 0; i--)
        {
            var e = _events[i];
            if (kinds.Count > 0 && !kinds.Contains(e.Kind)) continue;
            // Reservoir draw tilted toward the newest.
            seen++;
            if (pick == null || rng.Next(seen + 1) == 0) pick = e;
            if (seen >= 6) break;
        }
        return pick;
    }
}
