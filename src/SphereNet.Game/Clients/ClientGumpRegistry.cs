namespace SphereNet.Game.Clients;

/// <summary>
/// Open gump/dialog bookkeeping extracted from GameClient (decomposition
/// phase 1 — see docs/GAMECLIENT_DECOMPOSITION_TR.md): the active gump set,
/// per-gump response callbacks and the open script-dialog name registry
/// behind DIALOGCLOSE / ISDIALOGOPEN. Pure state relocation — the call
/// sites operate on these collections exactly as they did on the fields.
/// </summary>
public sealed class ClientGumpRegistry
{
    public HashSet<uint> ActiveGumps { get; } = [];

    public Dictionary<uint, Action<uint, uint[], (ushort, string)[]>> Callbacks { get; } = [];

    /// <summary>Open script dialogs (name → gump id). Entries drop when the
    /// client answers the gump or DIALOGCLOSE force-closes it.</summary>
    public Dictionary<string, uint> OpenScriptDialogs { get; } = new(StringComparer.OrdinalIgnoreCase);
}
