using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Clients;

/// <summary>Readonly visibility delta produced by the (parallelizable) build
/// phase and consumed by the single-threaded apply phase.
///
/// Ownership: <see cref="ClientViewUpdater.BuildViewDelta"/> hands out the
/// client's own scratch delta, whose collections keep their capacity from one
/// refresh to the next (multicore audit M09: a fresh delta per build was ~64-86 KB
/// for a client in a 600-object crowd). The scratch is RENTED from the build until
/// <see cref="ClientViewUpdater.ApplyViewDelta"/> consumes it, or until the caller
/// abandons it with <see cref="Release"/>. A build that finds the scratch still
/// rented - a delta built but not yet applied - gets a freshly allocated delta
/// instead, so a pending delta is never overwritten. A delta's contents are valid
/// until the owning client's next build.</summary>
public sealed class ClientViewDelta
{
    public HashSet<uint> CurrentChars { get; } = [];
    public HashSet<uint> CurrentItems { get; } = [];
    public List<(Character Character, bool HiddenAsAllShow)> NewChars { get; } = [];
    public List<Character> UpdatedChars { get; } = [];
    public List<(Item Item, bool HiddenAsAllShow)> NewItems { get; } = [];
    public List<Item> UpdatedItems { get; } = [];

    /// <summary>Build-side scratch: ground items counted per tile for the 80-item cap.</summary>
    internal Dictionary<Point3D, int> ItemTileCounts { get; } = [];
    /// <summary>Apply-side scratch: known uids that left the view this refresh.</summary>
    internal List<uint> StaleUids { get; } = [];

    /// <summary>True while this delta is a client's scratch between build and apply.</summary>
    internal bool IsRented { get; set; }

    /// <summary>Give up a delta that will not be applied (an abandoned tick), so the
    /// owning client's next build may reuse its collections. Idempotent, and a no-op
    /// on a delta that is not a client's scratch.</summary>
    public void Release() => IsRented = false;

    internal void Reset()
    {
        CurrentChars.Clear();
        CurrentItems.Clear();
        NewChars.Clear();
        UpdatedChars.Clear();
        NewItems.Clear();
        UpdatedItems.Clear();
        ItemTileCounts.Clear();
        StaleUids.Clear();
    }
}

/// <summary>
/// View-update handler extracted from the GameClient.ViewUpdate partial
/// (decomposition phase 3 — see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Owns the view-delta build/apply pipeline and the known-object
/// notifications; the per-client state lives in ClientViewCache
/// (GameClient.View). GameClient keeps thin delegating members, so every
/// call site is unchanged — the logic moved verbatim, with field accesses
/// routed through the GameClient context (Character/NetState/World/View and
/// the now-internal Send* packet helpers).
/// </summary>
public sealed class ClientViewUpdater
{
    private const int MaxItemsPerViewTile = 80;

    private readonly IClientContext _client;

    // The client's reusable delta (see ClientViewDelta's ownership notes) and the
    // visitor delegates bound once per client, with the per-build state they read.
    // A client is built by one worker at a time, so these are never shared.
    private readonly ClientViewDelta _scratch = new();
    private readonly Action<Character> _visitChar;
    private readonly Action<Item> _visitItem;
    private Character? _buildMe;
    private ClientViewDelta? _buildDelta;
    private bool _buildCanSeeInvisItems;

    internal ClientViewUpdater(IClientContext client)
    {
        _client = client;
        _visitChar = VisitBuildChar;
        _visitItem = VisitBuildItem;
    }

    private ClientViewCache View => _client.View;
    private GameWorld WorldRef => _client.World;

    /// <summary>
    /// Source-X CClient::addObjMessage loop. Sends newly visible objects and
    /// removes objects that went out of range. Called each server tick.
    /// </summary>
    public void UpdateClientView()
    {
        var delta = BuildViewDelta();
        if (delta != null)
        {
            ApplyViewDelta(delta);
            SyncOpenMapStaticDoors();
        }
    }

    /// <summary>
    /// Build a readonly visibility delta. Safe for parallel build phase.
    /// Only runs for clients with ViewNeedsRefresh — idle clients skip entirely.
    /// </summary>
    public ClientViewDelta? BuildViewDelta()
    {
        var me = _client.Character;
        if (me == null || !_client.IsPlaying) return null;
        if (me.IsReplaySpectator) return null;

        int range = _client.NetState.ViewRange;
        var center = me.Position;

        // Reuse the client's scratch unless a delta built from it is still waiting
        // to be applied; overwriting that one would change what its apply sends.
        ClientViewDelta delta;
        if (_scratch.IsRented)
        {
            delta = new ClientViewDelta();
        }
        else
        {
            delta = _scratch;
            delta.Reset();
            delta.IsRented = true;
        }

        // Saved and restored so a build nested inside a visibility check (none is
        // known today) cannot leave the outer build reading the inner one's state.
        var prevMe = _buildMe;
        var prevDelta = _buildDelta;
        bool prevCanSee = _buildCanSeeInvisItems;
        _buildMe = me;
        _buildDelta = delta;
        // Invisible items (spawn worldgems, triggers, etc.) render for AllShow;
        // GM+ staff also see them dimmed without toggling AllShow, so a GM can
        // audit spawners on sight.
        _buildCanSeeInvisItems = me.AllShow || me.PrivLevel >= Core.Enums.PrivLevel.GM;
        try
        {
            WorldRef.VisitInRange(center, range, _visitChar, _visitItem);
        }
        catch
        {
            // The half-built delta is never returned; free the scratch.
            if (ReferenceEquals(delta, _scratch))
                delta.IsRented = false;
            throw;
        }
        finally
        {
            _buildMe = prevMe;
            _buildDelta = prevDelta;
            _buildCanSeeInvisItems = prevCanSee;
        }

        return delta;
    }

    private void VisitBuildChar(Character ch)
    {
        var me = _buildMe!;
        var delta = _buildDelta!;
        if (ch == me || !IsCharVisible(me, ch)) return;

        uint uid = ch.Uid.Value;
        delta.CurrentChars.Add(uid);

        bool hiddenAsAllShow = DrawsGreyed(ch);
        if (!View.KnownChars.Contains(uid))
            delta.NewChars.Add((ch, hiddenAsAllShow));
        else
            delta.UpdatedChars.Add(ch);
    }

    private void VisitBuildItem(Item item)
    {
        var delta = _buildDelta!;
        if (item.IsDeleted || item.IsEquipped || !item.IsOnGround) return;
        bool isInvis = item.IsAttr(Core.Enums.ObjAttributes.Invis);
        bool canSeeInvisItems = _buildCanSeeInvisItems;
        if (isInvis && !canSeeInvisItems)
            return;

        var itemTileCounts = delta.ItemTileCounts;
        var tile = new Point3D(item.X, item.Y, item.Z, item.MapIndex);
        int tileCount = itemTileCounts.GetValueOrDefault(tile);
        if (tileCount >= MaxItemsPerViewTile)
            return;
        itemTileCounts[tile] = tileCount + 1;

        uint uid = item.Uid.Value;
        delta.CurrentItems.Add(uid);
        if (!View.KnownItems.Contains(uid))
            delta.NewItems.Add((item, isInvis && canSeeInvisItems));
        else
            delta.UpdatedItems.Add(item);
    }

    /// <summary>
    /// Apply previously built delta and perform packet I/O + known-set mutation.
    /// Must run on single-thread apply phase.
    /// </summary>
    public void ApplyViewDelta(ClientViewDelta delta)
    {
        try
        {
            ApplyViewDeltaCore(delta);
        }
        finally
        {
            // Consumed (or failed - the next refresh rebuilds it either way): the
            // client's scratch is free for its next build.
            if (ReferenceEquals(delta, _scratch))
                delta.IsRented = false;
        }
    }

    private void ApplyViewDeltaCore(ClientViewDelta delta)
    {
        var me = _client.Character;
        if (me == null || !_client.IsPlaying) return;

        foreach (var (ch, hiddenAsAllShow) in delta.NewChars)
        {
            if (hiddenAsAllShow)
                _client.SendDrawObjectHidden(ch);
            else
                _client.SendDrawObject(ch);

            uint uid = ch.Uid.Value;
            View.KnownChars.Add(uid);
            View.LastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue, ComputeVisKey(ch), _client.GetNotoriety(ch));
            // A bonded pet's ghost is announced as bonded to whoever can see it
            // (Source-X addChar, CClientMsg.cpp:1196-1202). Without this the
            // client has no way to tell that ghost from any other corpse-less
            // body — and bonded pets DO stay in the world as ghosts here.
            if (!ch.IsPlayer && ch.IsBonded && ch.IsDead)
                _client.SendBondedStatus(ch, isGhost: true);
            _client.SendAosTooltip(ch, requested: false);
        }

        foreach (var ch in delta.UpdatedChars)
        {
            uint uid = ch.Uid.Value;
            bool posChanged = false;
            bool bodyChanged = false;
            bool visChanged = false;
            ushort curVis = ComputeVisKey(ch);
            // Per VIEWER, not per character: a guild war, a party join or an attack
            // changes the colour this client must draw without the target itself
            // changing at all, so the target's own state cannot detect it.
            byte curNoto = _client.GetNotoriety(ch);
            if (View.LastKnownPos.TryGetValue(uid, out var last))
            {
                posChanged = last.X != ch.X || last.Y != ch.Y || last.Z != ch.Z || last.Dir != (byte)ch.Direction;
                bodyChanged = last.Body != ch.BodyId || last.Hue != ch.Hue;
                visChanged = last.Vis != curVis || last.Noto != curNoto;
            }
            else
            {
                posChanged = true;
            }

            bool manifestGhost = ch.IsDead && ch.IsInWarMode &&
                !me.AllShow &&
                me.PrivLevel < Core.Enums.PrivLevel.Counsel &&
                !me.IsDead;
            // Already on screen, so the shared visibility rule let it through; the
            // greyed draw follows from its state alone.
            bool hiddenAsAllShow = DrawsGreyed(ch);

            if (bodyChanged || visChanged)
            {
                if (hiddenAsAllShow)
                    _client.SendDrawObjectHidden(ch);
                else if (manifestGhost)
                    _client.SendDrawObjectWithHue(ch, 0x4001);
                else
                    _client.SendDrawObject(ch);
            }
            else if (posChanged)
            {
                if (hiddenAsAllShow)
                    _client.SendUpdateMobileHidden(ch);
                else if (manifestGhost)
                    _client.SendUpdateMobileWithHue(ch, 0x4001);
                else
                    _client.SendUpdateMobile(ch);
            }

            if (posChanged || bodyChanged || visChanged)
                View.LastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue, curVis, curNoto);
        }

        foreach (var (item, hiddenAsAllShow) in delta.NewItems)
        {
            if (hiddenAsAllShow)
                _client.SendWorldItemAllShow(item);
            else
                _client.SendWorldItem(item);
            uint nuid = item.Uid.Value;
            View.KnownItems.Add(nuid);
            View.LastKnownItemState[nuid] = (item.X, item.Y, item.Z, item.DispIdFull, item.Hue, item.Amount, item.Direction);
            _client.SendAosTooltip(item, requested: false);
        }

        foreach (var item in delta.UpdatedItems)
        {
            uint uid = item.Uid.Value;
            if (View.LastKnownItemState.TryGetValue(uid, out var prev))
            {
                bool changed = prev.X != item.X || prev.Y != item.Y || prev.Z != item.Z ||
                               prev.DispId != item.DispIdFull || prev.Hue != item.Hue ||
                               prev.Amount != item.Amount || prev.Direction != item.Direction;
                if (changed)
                {
                    _client.SendWorldItem(item);
                    View.LastKnownItemState[uid] = (item.X, item.Y, item.Z, item.DispIdFull, item.Hue, item.Amount, item.Direction);
                }
            }
            else
            {
                // Known, but with no recorded state: we do not actually know what
                // the client is showing for it. Recording the current state and
                // staying silent made the item permanently invisible — every later
                // tick then compared equal and sent nothing, so it only came back
                // after a resync cleared KnownItems (walk to another sector and
                // return). Re-send instead of assuming.
                _client.SendWorldItem(item);
                View.LastKnownItemState[uid] = (item.X, item.Y, item.Z, item.DispIdFull, item.Hue, item.Amount, item.Direction);
            }
        }

        var staleChars = delta.StaleUids;
        staleChars.Clear();
        foreach (uint uid in View.KnownChars)
        {
            if (!delta.CurrentChars.Contains(uid))
            {
                _client.NetState.Send(new PacketDeleteObject(uid));
                staleChars.Add(uid);
            }
        }
        foreach (uint uid in staleChars)
        {
            View.KnownChars.Remove(uid);
            View.LastKnownPos.Remove(uid);
            // The tooltip cache survives view-exit deliberately (V2): the built
            // OPL lives on the object with a pure TTL (Source-X model).
        }

        var staleItems = delta.StaleUids;
        staleItems.Clear();
        foreach (uint uid in View.KnownItems)
        {
            if (delta.CurrentItems.Contains(uid))
                continue;

            // An item that dropped out of the ground view because it was
            // equipped onto a mobile has already been re-homed client-side by
            // the 0x2E worn-item packet. A 0x1D here would delete the now-worn
            // item from the client — the classic "recolour/equip a worn item
            // and it vanishes until a resync (teleport)" bug. Forget it from the
            // ground-known set without deleting; the wearer's draw owns it now.
            var existing = WorldRef.FindItem(new Serial(uid));
            if (existing is { IsDeleted: false, IsEquipped: true })
            {
                staleItems.Add(uid);
                continue;
            }

            _client.NetState.Send(new PacketDeleteObject(uid));
            staleItems.Add(uid);
        }
        foreach (uint uid in staleItems)
        {
            View.KnownItems.Remove(uid);
            View.LastKnownItemState.Remove(uid);
            // Tooltip caches kept across view-exit — see the stale-chars loop.
        }
        staleItems.Clear();
    }

    /// <summary>Run after every accepted step. The client drops a ground object the
    /// moment it is farther than its view range from where the client thinks it is
    /// (ClassicUO World.Update, item.Distance &gt; ClientViewRange) and tells no one.
    /// The view delta runs once a tick, so a step out of range and straight back
    /// between two ticks left the object known here and gone there - never sent again
    /// until the player walked far enough for the delta to see it leave. Source-X
    /// decides per step, from the point the step left (addPlayerSee(ptOld),
    /// CClientMsg.cpp:1949): whatever was out of view from there and is in view now is
    /// sent. Forgetting what fell out of range at this step gives the same result:
    /// the next delta finds it new again once it is back in range.</summary>
    internal static void ForgetBeyondRange(ClientViewCache view, GameWorld world, Character me, int range)
    {
        List<uint>? gone = null;
        foreach (var (uid, st) in view.LastKnownItemState)
        {
            if (Math.Max(Math.Abs(st.X - me.X), Math.Abs(st.Y - me.Y)) <= range)
                continue;
            // A multi is dropped by the client by its footprint, not its centre.
            if (world.FindItem(new Serial(uid)) is { } item && Item.IsMultiItemType(item.ItemType))
                continue;
            (gone ??= []).Add(uid);
        }
        if (gone != null)
        {
            foreach (uint uid in gone)
            {
                view.KnownItems.Remove(uid);
                view.LastKnownItemState.Remove(uid);
            }
            gone.Clear();
        }

        foreach (var (uid, st) in view.LastKnownPos)
        {
            if (Math.Max(Math.Abs(st.X - me.X), Math.Abs(st.Y - me.Y)) > range)
                (gone ??= []).Add(uid);
        }
        if (gone != null)
        {
            foreach (uint uid in gone)
            {
                view.KnownChars.Remove(uid);
                view.LastKnownPos.Remove(uid);
            }
        }
    }

    public void SyncOpenMapStaticDoors()
    {
        var me = _client.Character;
        if (me == null || WorldRef.MapData == null) return;

        int range = _client.NetState.ViewRange;
        byte mapId = me.MapIndex;
        short cx = me.X, cy = me.Y;

        var activeDoors = new HashSet<uint>();

        foreach (var (map, x, y, z) in WorldRef.OpenMapStaticDoors)
        {
            if (map != mapId) continue;
            if (Math.Abs(x - cx) > range || Math.Abs(y - cy) > range) continue;

            uint serial = (uint)(Serial.ItemFlag |
                (uint)((x & 0x7FFF) << 16) |
                (uint)((y & 0x3FFF) << 3) |
                (uint)(z & 0x07));
            activeDoors.Add(serial);

            if (View.KnownDoorOverrides.Add(serial))
            {
                ushort openTile = 0;
                ushort hue = 0;
                foreach (var s in WorldRef.MapData.GetStatics(mapId, x, y))
                {
                    if (s.Z == z && DoorHelper.IsDoorGraphic(WorldRef.MapData, s.TileId))
                    {
                        openTile = (ushort)(s.TileId + 1);
                        hue = s.Hue;
                        break;
                    }
                }
                if (openTile != 0)
                    _client.NetState.Send(new PacketWorldItem(serial, openTile, 1, x, y, z, hue));
            }
        }

        var staleDoors = new List<uint>();
        foreach (uint serial in View.KnownDoorOverrides)
        {
            if (!activeDoors.Contains(serial))
            {
                _client.NetState.Send(new PacketDeleteObject(serial));
                staleDoors.Add(serial);
            }
        }
        foreach (uint s in staleDoors)
            View.KnownDoorOverrides.Remove(s);
    }

    /// <summary>The whole visual-state word for a mobile, as this client sees it.
    ///
    /// The low byte IS the mobile flags byte the viewer receives, carried verbatim:
    /// frozen, female, poisoned/flying, yellow bar, staff, war, greyed. Picking out
    /// individual bits by hand is what made a STATIONARY character's state changes
    /// invisible - the criminal and murderer bits had to be added for exactly that
    /// reason once already, and freeze, invulnerability, flight and sleep were still
    /// missing. Taking the byte whole means the next flag added to it is covered
    /// without anyone having to remember to come back here.
    ///
    /// The high byte carries what the flags byte does not say and the client still
    /// needs re-drawing for: dead, criminal, murderer.</summary>
    internal ushort ComputeVisKey(Character ch)
    {
        ushort vis = _client.BuildMobileFlags(ch);
        if (ch.IsDead) vis |= 0x0100;
        if (ch.IsCriminal) vis |= 0x0200;
        if (ch.IsMurderer) vis |= 0x0400;
        if ((Definitions.CharDefHelper.GetCanFlags(ch) & Core.Enums.CanFlags.C_Statue) != 0) vis |= 0x0800;
        return vis;
    }

    /// <summary>
    /// Update this client's View.LastKnownPos for a character that was just broadcast via 0x77.
    /// Prevents the view delta from sending a duplicate 0x77 for the same position.
    /// </summary>
    public void UpdateKnownCharPosition(Character ch)
    {
        uint uid = ch.Uid.Value;
        if (View.KnownChars.Contains(uid))
            View.LastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue, ComputeVisKey(ch), _client.GetNotoriety(ch));
    }

    /// <summary>Returns true if this client already tracks the given mobile (has sent 0x78 spawn).</summary>
    public bool HasKnownChar(uint uid) => View.KnownChars.Contains(uid);

    /// <summary>Update the known-character cache to reflect a body/hue change
    /// broadcast out-of-band (death ghost transition / resurrect restore) so
    /// the next BuildViewDelta does not re-emit a duplicate 0x78. No-op when
    /// the UID is not currently known.</summary>
    public void UpdateKnownCharRender(uint uid, ushort newBody, ushort newHue, byte direction, short x, short y, sbyte z, ushort visKey = 0)
    {
        if (!View.KnownChars.Contains(uid))
            return;
        byte noto = WorldRef.FindChar(new Serial(uid)) is { } known
            ? _client.GetNotoriety(known)
            : (byte)0;
        View.LastKnownPos[uid] = (x, y, z, direction, newBody, newHue, visKey, noto);
    }

    /// <summary>Drop the character from the known set; optionally emit 0x1D.
    /// sendDelete=false is for the death dispatch where 0xAF already re-keyed
    /// the mobile client-side. Idempotent.</summary>
    public void RemoveKnownChar(uint uid, bool sendDelete = true)
    {
        if (View.KnownChars.Remove(uid))
        {
            View.LastKnownPos.Remove(uid);
            if (sendDelete)
                _client.NetState.Send(new PacketDeleteObject(uid));
        }
    }

    /// <summary>
    /// Called by BroadcastCharacterAppear to immediately show a character on this client.
    /// Each client renders from its own perspective (notoriety, AllShow, etc.).
    /// </summary>
    public void NotifyCharacterAppear(Character ch)
    {
        var me = _client.Character;
        if (me == null || !_client.IsPlaying) return;
        if (ch == me) return;
        if (ch.Position.Map != me.Position.Map) return;
        if (!InRange(me.Position, ch.Position, _client.NetState.ViewRange)) return;

        // The same permission rule the view delta applies (Source-X CChar::CanSee):
        // whatever is sent here must be what the next delta keeps, or it is drawn
        // now and deleted a tick later - or held back now and drawn a tick later.
        if (!IsCharVisible(me, ch))
            return;

        bool isStaffViewer = me.AllShow ||
            me.PrivLevel >= Core.Enums.PrivLevel.Counsel;
        bool ghostManifested = ch.IsDead && ch.IsInWarMode;

        uint uid = ch.Uid.Value;
        // Draw it the way the delta would: greyed when hidden/offline; a
        // manifested ghost translucent grey (hue 0x4001) for plain observers,
        // while staff see ghosts in their normal hue (HUE_DEFAULT).
        if (DrawsGreyed(ch))
            _client.SendDrawObjectHidden(ch);
        else if (ghostManifested && !isStaffViewer && !me.IsDead)
            _client.SendDrawObjectWithHue(ch, 0x4001);
        else
            _client.SendDrawObject(ch);

        View.KnownChars.Add(uid);
        View.LastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue, ComputeVisKey(ch), _client.GetNotoriety(ch));
    }

    /// <summary>
    /// Object-centric move notification for NPC movement. Handles enter-range (0x78),
    /// leave-range (0x1D), and position-update (0x77).
    /// </summary>
    public void NotifyCharMoved(Character ch, Point3D oldPos)
    {
        var me = _client.Character;
        if (me == null || !_client.IsPlaying) return;
        if (ch == me) return;
        if (ch.IsDeleted) return;
        if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden)) return;

        int range = _client.NetState.ViewRange;
        bool wasInRange = InRange(me.Position, oldPos, range) && oldPos.Map == me.Position.Map;
        bool nowInRange = InRange(me.Position, ch.Position, range);

        uint uid = ch.Uid.Value;

        if (!wasInRange && nowInRange)
        {
            NotifyCharacterAppear(ch);
        }
        else if (wasInRange && !nowInRange)
        {
            RemoveKnownChar(uid, sendDelete: true);
        }
        else if (wasInRange && nowInRange && View.KnownChars.Contains(uid))
        {
            if (!IsCharVisible(me, ch))
            {
                RemoveKnownChar(uid, sendDelete: true);
                return;
            }

            if (View.LastKnownPos.TryGetValue(uid, out var last))
            {
                bool posChanged = last.X != ch.X || last.Y != ch.Y || last.Z != ch.Z || last.Dir != (byte)ch.Direction;
                if (!posChanged) return;
            }
            if (DrawsGreyed(ch))
                _client.SendUpdateMobileHidden(ch);
            else
                _client.SendUpdateMobile(ch);
            View.LastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue, ComputeVisKey(ch), _client.GetNotoriety(ch));
        }
        else if (nowInRange)
        {
            // In range on both ends but not yet tracked — e.g. the NPC
            // unhid after a scout-hide, or its initial appear was filtered
            // at spawn time. Without this branch the mobile would move
            // invisibly until the observer's next full view refresh.
            // NotifyCharacterAppear re-applies the hidden/ghost filters.
            NotifyCharacterAppear(ch);
        }
    }

    /// <summary>
    /// Player enter/leave range notification. Only handles enter-range (0x78) and
    /// leave-range (0x1D). Still-in-range 0x77 is handled by BroadcastMoveNearby.
    /// </summary>
    public void NotifyCharEnterLeave(Character ch, Point3D oldPos)
    {
        var me = _client.Character;
        if (me == null || !_client.IsPlaying) return;
        if (ch == me) return;
        if (ch.IsDeleted) return;
        if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden)) return;

        int range = _client.NetState.ViewRange;
        bool wasInRange = InRange(me.Position, oldPos, range) && oldPos.Map == me.Position.Map;
        bool nowInRange = InRange(me.Position, ch.Position, range);

        uint uid = ch.Uid.Value;

        if (!wasInRange && nowInRange)
            NotifyCharacterAppear(ch);
        else if (wasInRange && !nowInRange)
            RemoveKnownChar(uid, sendDelete: true);
        else if (nowInRange && !View.KnownChars.Contains(uid))
            NotifyCharacterAppear(ch);
    }

    /// <summary>The one viewer/mobile visibility rule shared by the view delta, the
    /// appear notification and the move notifications. Range is the caller's. A
    /// mount is drawn as part of its rider, so it is kept out of everyone's view -
    /// except a viewer in DEBUG, which is what upstream's !IsPriv(PRIV_DEBUG) guard on
    /// the same filter is for (CClient.cpp:421). The rest is Source-X CChar::CanSee
    /// (CCharStatus.cpp:1167-1257), see <see cref="Character.CanSeeCharacter"/>.</summary>
    internal static bool IsCharVisible(Character me, Character ch)
    {
        if (ch.IsDeleted) return false;
        if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden) && !me.DebugView) return false;
        // A disconnected character sits in the sector's m_Chars_Disconnect list, which
        // the view's CWorldSearch walks only with ALLSHOW (CWorldSearch.cpp:271, set
        // from PRIV_ALLSHOW in CClientMsg.cpp:331). CanSee's GM-mode rule (the CANSEE
        // read) never reaches it in the view without ALLSHOW.
        if (ch.IsLoggedOut && !me.AllShow) return false;
        return me.CanSeeCharacter(ch);
    }

    /// <summary>A mobile the viewer is allowed to see but which is hidden, invisible
    /// or logged out is drawn greyed (the 0x80 mobile flag).</summary>
    private static bool DrawsGreyed(Character ch) =>
        ch.IsLoggedOut || ch.IsInvisible || ch.IsStatFlag(Core.Enums.StatFlag.Hidden);

    private static bool InRange(Point3D a, Point3D b, int range)
    {
        if (a.Map != b.Map) return false;
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        return dx <= range && dy <= range;
    }
}
