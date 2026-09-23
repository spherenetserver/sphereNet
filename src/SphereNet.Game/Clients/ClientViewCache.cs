namespace SphereNet.Game.Clients;

public sealed record TooltipCacheEntry(
    uint Hash,
    uint Revision,
    long BuiltAt,
    (uint ClilocId, string Args)[] Properties);

/// <summary>
/// Per-client view bookkeeping extracted from GameClient (decomposition
/// phase 2 — see docs/GAMECLIENT_DECOMPOSITION_TR.md): which objects the
/// client has been told about and the last state sent for each, used by the
/// view-delta builder to suppress duplicate packets. Pure state relocation —
/// the call sites operate on these collections exactly as they did on the
/// former fields.
/// </summary>
public sealed class ClientViewCache
{
    public HashSet<uint> KnownChars { get; } = [];
    public HashSet<uint> KnownItems { get; } = [];
    public HashSet<uint> KnownDoorOverrides { get; } = [];
    /// <summary>What this client was last told about each mobile it knows.
    ///
    /// <c>Vis</c> is the whole visual-state word, not a hand-picked subset: the
    /// mobile flags byte the viewer receives plus the view-only bits (dead,
    /// criminal, murderer). Picking individual bits is what left a stationary
    /// character's state changes invisible until it moved, twice over.
    ///
    /// <c>Noto</c> is the notoriety byte, which is computed PER VIEWER - a guild
    /// war, a party join or an attack changes the colour this client must draw
    /// without the target itself changing at all.</summary>
    public Dictionary<uint, (short X, short Y, sbyte Z, byte Dir, ushort Body, ushort Hue, ushort Vis, byte Noto)> LastKnownPos { get; } = [];
    public Dictionary<uint, (short X, short Y, sbyte Z, ushort DispId, ushort Hue, ushort Amount, byte Direction)> LastKnownItemState { get; } = [];
    /// <summary>The last container add (0x25) sent for each contained item, and when.
    /// Read by the dirty-drain resend so the add an engine has just sent explicitly
    /// is not sent a second time on the same tick.</summary>
    public Dictionary<uint, ((ushort ItemId, ushort Amount, short X, short Y, uint Container, ushort Hue) Key, long SentAt)> LastSentContainerItem { get; } = [];
    // Tooltip state lives on the object (ObjBase.TooltipCache, Source-X
    // SetPropertyList model) — no per-client tooltip bookkeeping remains: pushes
    // are version-only (0xDC) and the client pulls full data on a cache miss.
}
