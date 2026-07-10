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
    public Dictionary<uint, (short X, short Y, sbyte Z, byte Dir, ushort Body, ushort Hue, byte Vis)> LastKnownPos { get; } = [];
    public Dictionary<uint, (short X, short Y, sbyte Z, ushort DispId, ushort Hue, ushort Amount, byte Direction)> LastKnownItemState { get; } = [];
    /// <summary>serial → last sent tooltip hash.</summary>
    public Dictionary<uint, uint> TooltipHashCache { get; } = [];
    public Dictionary<uint, TooltipCacheEntry> TooltipDataCache { get; } = [];
}
