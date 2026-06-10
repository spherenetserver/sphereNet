using SphereNet.Game.Gumps;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>Targeting handler (decomposition phase 3) — the members below
    /// delegate so every call site stays unchanged. The logic lives in
    /// <see cref="ClientTargetingHandler"/>.</summary>
    internal ClientTargetingHandler Targeting => _targeting ??= new ClientTargetingHandler(this);
    private ClientTargetingHandler? _targeting;

    public void HandleTargetResponse(byte type, uint targetId, uint serial, short x, short y, sbyte z, ushort graphic) =>
        Targeting.HandleTargetResponse(type, targetId, serial, x, y, z, graphic);

    public void HandleGumpResponse(uint serial, uint gumpId, uint buttonId,
        uint[] switches, (ushort Id, string Text)[] textEntries) =>
        Targeting.HandleGumpResponse(serial, gumpId, buttonId, switches, textEntries);

    /// <summary>Send a gump dialog to the client. Optionally register a response callback.</summary>
    public void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null) =>
        Targeting.SendGump(gump, callback);

    /// <summary>Set a callback-based target cursor. Used by housing, pets, etc.</summary>
    internal void SetPendingTarget(Action<uint, short, short, sbyte, ushort> callback, byte cursorType = 1) =>
        Targeting.SetPendingTarget(callback, cursorType);
}
