using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>View-update handler (decomposition phase 3) — the members
    /// below delegate so every call site stays unchanged. The logic lives in
    /// <see cref="ClientViewUpdater"/>; the per-client state in
    /// <see cref="ClientViewCache"/> (View).</summary>
    internal ClientViewUpdater ViewUpdater => _viewUpdater ??= new ClientViewUpdater(this);
    private ClientViewUpdater? _viewUpdater;

    /// <summary>Source-X CClient::addObjMessage loop. Sends newly visible
    /// objects and removes objects that went out of range.</summary>
    public void UpdateClientView() => ViewUpdater.UpdateClientView();

    /// <summary>Build a readonly visibility delta. Safe for parallel build phase.</summary>
    public ClientViewDelta? BuildViewDelta() => ViewUpdater.BuildViewDelta();

    /// <summary>Apply a previously built delta (single-thread apply phase).</summary>
    public void ApplyViewDelta(ClientViewDelta delta) => ViewUpdater.ApplyViewDelta(delta);

    public void SyncOpenMapStaticDoors() => ViewUpdater.SyncOpenMapStaticDoors();

    /// <summary>Update View.LastKnownPos for a character that was just
    /// broadcast via 0x77, preventing a duplicate from the view delta.</summary>
    public void UpdateKnownCharPosition(Character ch) => ViewUpdater.UpdateKnownCharPosition(ch);

    /// <summary>True if this client already tracks the given mobile (0x78 sent).</summary>
    public bool HasKnownChar(uint uid) => ViewUpdater.HasKnownChar(uid);

    /// <summary>Sync the known-char cache after an out-of-band body/hue
    /// broadcast (death/resurrect) so the next delta is not a duplicate.</summary>
    public void UpdateKnownCharRender(uint uid, ushort newBody, ushort newHue, byte direction, short x, short y, sbyte z, byte visKey = 0) =>
        ViewUpdater.UpdateKnownCharRender(uid, newBody, newHue, direction, x, y, z, visKey);

    /// <summary>Drop a character from the known set; optionally emit 0x1D.</summary>
    public void RemoveKnownChar(uint uid, bool sendDelete = true) => ViewUpdater.RemoveKnownChar(uid, sendDelete);

    /// <summary>Immediately show a character on this client (login/teleport).</summary>
    public void NotifyCharacterAppear(Character ch) => ViewUpdater.NotifyCharacterAppear(ch);

    /// <summary>NPC move notification: enter (0x78) / leave (0x1D) / update (0x77).</summary>
    public void NotifyCharMoved(Character ch, Point3D oldPos) => ViewUpdater.NotifyCharMoved(ch, oldPos);

    /// <summary>Player enter/leave range notification (0x78/0x1D only).</summary>
    public void NotifyCharEnterLeave(Character ch, Point3D oldPos) => ViewUpdater.NotifyCharEnterLeave(ch, oldPos);
}
