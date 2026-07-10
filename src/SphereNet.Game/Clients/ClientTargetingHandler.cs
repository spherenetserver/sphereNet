using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Game.Objects;
using SphereNet.Game.Gumps;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;
using SphereNet.Game.Messages;
using ScriptDbAdapter = SphereNet.Scripting.Execution.ScriptDbAdapter;

namespace SphereNet.Game.Clients;

/// <summary>
/// Targeting handler extracted from the GameClient.Targeting partial
/// (decomposition phase 3 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Target-response router (GM cursor flows, script TARGETF, spell targets),
/// gump response routing and gump/target-cursor sending. Method bodies moved
/// verbatim; the private context shims below enumerate exactly what this
/// handler needs from GameClient.
/// </summary>
public sealed class ClientTargetingHandler
{
    private readonly IClientContext _client;

    internal ClientTargetingHandler(IClientContext client)
    {
        _client = client;
    }

    // --- context shims (the GameClient surface this handler depends on) ---
    private Character? _character => _client.Character;
    private GameWorld _world => _client.World;
    private NetState _netState => _client.NetState;
    private TriggerDispatcher? _triggerDispatcher => _client.Triggers;
    private Mounts.MountEngine? _mountEngine => _client.MountE;
    private CommandHandler? _commands => _client.Cmds;
    private ILogger _logger => _client.Log;
    private ClientTargetState Targets => _client.Targets;
    private ClientGumpRegistry Gumps => _client.Gumps;
    private Action<Character>? OnResurrectOther => _client.OnResurrectOther;
    private Action<Character, Character>? OnKillTarget => _client.OnKillTarget;
    private string? _pendingDialogCloseFunction
    {
        get => _client.PendingDialogCloseFunction;
        set => _client.PendingDialogCloseFunction = value;
    }
    private string _pendingDialogArgs
    {
        get => _client.PendingDialogArgs;
        set => _client.PendingDialogArgs = value;
    }
    private void SysMessage(string text) => _client.SysMessage(text);
    private void Resync() => _client.Resync();
    private void BroadcastDrawObject(Character ch) => _client.BroadcastDrawObject(ch);
    private bool TryAddAtTarget(string token, Point3D targetPos, uint targetSerial = 0) => _client.TryAddAtTarget(token, targetPos, targetSerial);
    private bool RemoveTargetedObject(uint uid) => _client.RemoveTargetedObject(uid);
    private void OpenInspectPropDialog(ObjBase obj, int requestedPage) => _client.OpenInspectPropDialog(obj, requestedPage);
    private Item? DuplicateItem(Item src) => _client.DuplicateItem(src);
    private void OpenForeignBank(Character victim) => _client.OpenForeignBank(victim);
    private void SpawnCageAround(Point3D centre) => _client.SpawnCageAround(centre);
    private int ExecuteAreaVerb(string verb, Point3D centre, int range) => _client.ExecuteAreaVerb(verb, centre, range);
    private void HandleCastSpell(SpellType spell, uint targetUid) => _client.HandleCastSpell(spell, targetUid);
    private bool TryMountCharacter(Character mount) => _client.TryMountCharacter(mount);
    private bool TryResolveScriptVariable(string varName, IScriptObj target, ITriggerArgs? triggerArgs, out string value) => _client.TryResolveScriptVariable(varName, target, triggerArgs, out value);
    private void ClearPendingTargetState() => _client.ClearPendingTargetState();
    private Character? ResolvePickedChar(uint uid) => _client.ResolvePickedChar(uid);


    public void HandleTargetResponse(byte type, uint targetId, uint serial, short x, short y, sbyte z, ushort graphic)
    {
        if (_character == null) return;
        Targets.CursorActive = false;
        if (_character.IsDead)
        {
            int cancelledSkill = Targets.SkillCancelId;
            ClearPendingTargetState();
            if (cancelledSkill >= 0)
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillTargetCancel,
                    new TriggerArgs { CharSrc = _character, N1 = cancelledSkill });
            return;
        }
        bool targetCancelled = IsTargetCancelled(serial, x, y, z, graphic);
        if (targetCancelled)
        {
            // Source-X @Targon_Cancel — the player dismissed the target cursor.
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Targon_Cancel,
                new TriggerArgs { CharSrc = _character, ScriptConsole = _client });

            var pendingItemUid = Targets.ItemUid;
            // Hard-cancel all pending target flows to avoid any stale state from triggering
            // a resync/teleport path on the next target packet.
            Targets.Tele = false;
            Targets.AddToken = null;
            Targets.Remove = false;
            Targets.XVerb = null;
            Targets.XVerbArgs = "";
            Targets.AreaVerb = null;
            Targets.AreaRange = 0;
            Targets.Control = false;
            Targets.Dupe = false;
            Targets.Heal = false;
            Targets.Kill = false;
            Targets.Bank = false;
            Targets.SummonTo = false;
            Targets.Mount = false;
            Targets.SummonCage = false;
            Targets.Function = null;
            Targets.FunctionArgs = "";
            Targets.AllowGround = false;
            Targets.ItemUid = Serial.Invalid;
            Targets.ScriptNewItem = null;
            Targets.LastScriptPoint = null;
            Targets.Callback = null;
            int pendingSkillTargetCancelId = Targets.SkillCancelId;
            Targets.SkillCancelId = -1;

            if (_character.TryGetTag("CAST_SPELL", out string? cancelledSpellStr))
            {
                if (Enum.TryParse<SpellType>(cancelledSpellStr, out var cancelledSpell))
                {
                    _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellTargetCancel,
                        new TriggerArgs { CharSrc = _character, N1 = (int)cancelledSpell });
                }
                _character.RemoveTag("CAST_SPELL");
                // The cast never started — drop the wand/scroll source tags so they
                // don't leak into and get consumed by the next cast.
                _character.RemoveTag("WAND_UID");
                _character.RemoveTag("SCROLL_UID");
            }
            _character.RemoveTag("TARGP");
            _character.RemoveTag("TARG.X");
            _character.RemoveTag("TARG.Y");
            _character.RemoveTag("TARG.Z");
            _character.RemoveTag("TARG.MAP");
            _character.RemoveTag("TARG.UID");

            FirePendingItemTargetTrigger(pendingItemUid, ItemTrigger.TargOnCancel, Serial.Invalid, x, y, z, graphic);
            if (pendingSkillTargetCancelId >= 0)
            {
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillTargetCancel,
                    new TriggerArgs { CharSrc = _character, N1 = pendingSkillTargetCancelId });
            }

            SysMessage(ServerMessages.Get("target_cancel_1"));
            return;
        }

        // Callback-based target (housing, etc.)
        if (Targets.Callback != null)
        {
            var cb = Targets.Callback;
            Targets.Callback = null;
            cb(serial, x, y, z, graphic);
            return;
        }

        if (Targets.Tele)
        {
            Targets.Tele = false;

            Point3D? destination = null;
            if (serial != 0 && serial != 0xFFFFFFFF)
            {
                var obj = _world.FindObject(new Serial(serial));
                if (obj is Character targetChar)
                {
                    destination = targetChar.Position;
                }
                else if (obj is Item targetItem)
                {
                    destination = targetItem.Position;
                }
            }

            destination ??= new Point3D(x, y, z, _character.MapIndex);

            // Snap Z to the nearest walkable surface. Clients pick the Z of
            // whatever tile the mouse overlaps — frequently a rooftop or a
            // static plane. Landing there strands the player: every subsequent
            // step gets rejected by climb/cliff checks (~150 MoveReject spam
            // on `.mtele 1493,1639,40` observed in logs).
            var mdata = _world.MapData;
            if (mdata != null)
            {
                var d = destination.Value;
                sbyte walkZ = mdata.GetEffectiveZ(_character.MapIndex, d.X, d.Y, (sbyte)d.Z);
                if (walkZ != d.Z)
                    destination = new Point3D(d.X, d.Y, walkZ, _character.MapIndex);
            }

            _world.MoveCharacter(_character, destination.Value);
            Resync();
            _mountEngine?.EnsureMountedState(_character);
            // Broadcast full appearance (including mount) to nearby clients at new location.
            BroadcastDrawObject(_character);
            SysMessage(ServerMessages.GetFormatted("gm_teleported_dest", destination.Value));
            return;
        }

        if (!string.IsNullOrWhiteSpace(Targets.AddToken))
        {
            string addToken = Targets.AddToken;
            Targets.AddToken = null;

            Point3D targetPos = new Point3D(x, y, z, _character.MapIndex);
            uint targetSerial = serial;
            if (serial != 0 && serial != 0xFFFFFFFF)
            {
                var obj = _world.FindObject(new Serial(serial));
                if (obj != null)
                    targetPos = obj.Position;
            }

            if (!TryAddAtTarget(addToken, targetPos, targetSerial))
                SysMessage(ServerMessages.GetFormatted("gm_unknown_add", addToken));
            return;
        }

        if (Targets.Remove)
        {
            Targets.Remove = false;

            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            if (RemoveTargetedObject(serial))
                SysMessage(ServerMessages.GetFormatted("gm_removed", $"{serial:X8}"));
            else
                SysMessage(ServerMessages.Get("target_cant_remove"));
            return;
        }

        if (Targets.Resurrect)
        {
            Targets.Resurrect = false;

            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            // Try the picked serial as a character first; if it's a corpse,
            // fall back to the OWNER_UID tag the DeathEngine wrote on it.
            var victim = _world.FindChar(new Serial(serial));
            if (victim == null)
            {
                var corpse = _world.FindItem(new Serial(serial));
                if (corpse != null && corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
                    uint.TryParse(ownerStr, out uint ownerUid))
                {
                    victim = _world.FindChar(new Serial(ownerUid));
                }
            }

            if (victim == null)
            {
                SysMessage("Resurrect: cannot identify a character from that target.");
                return;
            }
            if (!victim.IsDead)
            {
                SysMessage($"'{victim.Name}' is not dead.");
                return;
            }

            OnResurrectOther?.Invoke(victim);
            SysMessage($"Resurrected '{victim.Name}'.");
            return;
        }

        if (Targets.Inspect)
        {
            Targets.Inspect = false;
            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }
            var infoObj = _world.FindObject(new Serial(serial));
            if (infoObj != null)
                OpenInspectPropDialog(infoObj, 0);
            else
                SysMessage(ServerMessages.GetFormatted("gm_object_serial", $"{serial:X8}"));
            return;
        }

        if (!string.IsNullOrWhiteSpace(Targets.ShowArgs))
        {
            string showArgs = Targets.ShowArgs;
            Targets.ShowArgs = null;

            if (_commands == null || serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            _commands.ExecuteShowForTarget(_character, showArgs, serial);
            return;
        }

        if (Targets.EditArgs != null)
        {
            string editArgs = Targets.EditArgs;
            Targets.EditArgs = null;

            if (_commands == null || serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            _commands.ExecuteEditForTarget(_character, editArgs, serial);
            return;
        }

        // ---- Phase C: NUKE / NUKECHAR / NUDGE area handlers ----
        if (!string.IsNullOrEmpty(Targets.AreaVerb))
        {
            string areaVerb = Targets.AreaVerb!;
            int areaRange = Targets.AreaRange;
            Targets.AreaVerb = null;
            Targets.AreaRange = 0;

            // Resolve the centre. If the GM clicked on an object use its
            // position so NUDGE/NUKE applied to a chest also covers the
            // surrounding tiles, mirroring Source-X's box centre behaviour.
            Point3D centre;
            if (serial != 0 && serial != 0xFFFFFFFF)
            {
                var picked = _world.FindObject(new Serial(serial));
                centre = picked?.Position ?? new Point3D(x, y, z, _character.MapIndex);
            }
            else
            {
                centre = new Point3D(x, y, z, _character.MapIndex);
            }

            int affected = ExecuteAreaVerb(areaVerb, centre, areaRange);
            switch (areaVerb)
            {
                case "NUKE":
                    SysMessage(ServerMessages.GetFormatted("gm_nuke_done", affected));
                    break;
                case "NUKECHAR":
                    SysMessage(ServerMessages.GetFormatted("gm_nukechar_done", affected));
                    break;
                case "NUDGE":
                    SysMessage(ServerMessages.GetFormatted("gm_nudge_done", affected));
                    break;
            }
            return;
        }

        if (Targets.Control)
        {
            Targets.Control = false;
            var npc = ResolvePickedChar(serial);
            if (npc == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            npc.TryAssignOwnership(_character, _character, summoned: false, enforceFollowerCap: false);
            SysMessage(ServerMessages.GetFormatted("gm_control_done", npc.Name));
            return;
        }

        if (Targets.Dupe)
        {
            Targets.Dupe = false;
            var pickedItem = serial != 0 && serial != 0xFFFFFFFF
                ? _world.FindItem(new Serial(serial))
                : null;
            if (pickedItem == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            var dup = DuplicateItem(pickedItem);
            if (dup != null)
                SysMessage(ServerMessages.GetFormatted("gm_dupe_done",
                    pickedItem.Name ?? "item", dup.Uid.Value.ToString("X8")));
            else
                SysMessage(ServerMessages.Get("target_cant_remove"));
            return;
        }

        if (Targets.Heal)
        {
            Targets.Heal = false;
            var victim = ResolvePickedChar(serial);
            if (victim == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            if (victim.IsDead) OnResurrectOther?.Invoke(victim);
            victim.Hits = victim.MaxHits;
            victim.Mana = victim.MaxMana;
            victim.Stam = victim.MaxStam;
            SysMessage(ServerMessages.GetFormatted("gm_heal_done", victim.Name));
            return;
        }

        if (Targets.Kill)
        {
            Targets.Kill = false;
            var victim = ResolvePickedChar(serial);
            if (victim == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            OnKillTarget?.Invoke(_character!, victim);
            return;
        }

        if (Targets.Bank)
        {
            Targets.Bank = false;
            var picked = ResolvePickedChar(serial);
            if (picked == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            // We open the picked char's bank on *our* client. Source-X
            // Eq's the BankBox onto the picked char then sends the owner
            // GM a 0x24 OpenContainer; the bank items are then drawn
            // from that container's content.
            OpenForeignBank(picked);
            return;
        }

        if (Targets.SummonTo)
        {
            Targets.SummonTo = false;
            var picked = ResolvePickedChar(serial);
            if (picked == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            _world.MoveCharacter(picked, _character.Position);
            BroadcastDrawObject(picked);
            SysMessage(ServerMessages.GetFormatted("gm_summonto_done", picked.Name));
            return;
        }

        if (Targets.Mount)
        {
            Targets.Mount = false;
            var npc = ResolvePickedChar(serial);
            if (npc == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            if (!TryMountCharacter(npc))
            {
                SysMessage(ServerMessages.Get("gm_mount_failed"));
                return;
            }
            BroadcastDrawObject(_character);
            SysMessage(ServerMessages.GetFormatted("gm_mount_done", npc.Name));
            return;
        }

        if (Targets.SummonCage)
        {
            Targets.SummonCage = false;
            var picked = ResolvePickedChar(serial);
            if (picked == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            // Source-X CV_SUMMONCAGE: teleport the victim to the GM and
            // ring them with iron-bar items. We use BaseId 0x0008/0x0009
            // for vertical/horizontal bars (DEFNAMES i_bars_*).
            _world.MoveCharacter(picked, _character.Position);
            BroadcastDrawObject(picked);
            SpawnCageAround(picked.Position);
            SysMessage(ServerMessages.GetFormatted("gm_summoncage_done", picked.Name));
            return;
        }

        // Source-X CClient.cpp:921 — generic X-prefix verb fallback:
        // resolve the picked object and apply the inner verb to it via
        // SpeechEngine.ExecuteVerbForTarget. Mirrors C++ addTargetVerb.
        if (!string.IsNullOrEmpty(Targets.XVerb))
        {
            string verb = Targets.XVerb!;
            string xargs = Targets.XVerbArgs;
            Targets.XVerb = null;
            Targets.XVerbArgs = "";

            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            IScriptObj? obj = (IScriptObj?)_world.FindChar(new Serial(serial))
                ?? _world.FindItem(new Serial(serial));
            if (obj == null)
            {
                SysMessage(ServerMessages.GetFormatted("gm_object_serial", $"{serial:X8}"));
                return;
            }

            // Snapshot the relevant fields we may need to broadcast on
            // change (position / appearance) before mutating the target.
            Point3D? posBefore = (obj as Character)?.Position;
            ushort bodyBefore = (obj as Character)?.BodyId ?? 0;
            ushort hueBefore = (obj as Character)?.Hue.Value ?? 0;

            bool ok = _commands?.ExecuteVerbForTarget(_character, verb, xargs, obj) ?? false;

            if (ok)
            {
                SysMessage(ServerMessages.GetFormatted("gm_xverb_applied", verb, obj.GetName()));

                if (obj is Character ch)
                {
                    bool moved = posBefore.HasValue && !ch.Position.Equals(posBefore.Value);
                    bool appearance = ch.BodyId != bodyBefore || ch.Hue.Value != hueBefore;
                    if (moved)
                    {
                        _world.MoveCharacter(ch, ch.Position);
                        if (ch == _character) Resync();
                        BroadcastDrawObject(ch);
                    }
                    else if (appearance)
                    {
                        BroadcastDrawObject(ch);
                    }
                }
                else if (obj is Item it && !it.IsDeleted)
                {
                    // Refresh the item for every observer. Only the COLOR property
                    // fires the OnVisualUpdate hook from inside TrySetProperty;
                    // other appearance edits (.xid / DISPID, etc.) would otherwise
                    // not reach clients until a full resync, since worn and
                    // contained items are skipped by the per-tick view delta.
                    Item.OnVisualUpdate?.Invoke(it);
                }
            }
            else
            {
                SysMessage(ServerMessages.GetFormatted("gm_xverb_failed", verb, obj.GetName()));
            }
            return;
        }

        if (!string.IsNullOrEmpty(Targets.Function) && _triggerDispatcher?.Runner != null)
        {
            string func = Targets.Function;
            Targets.Function = null;
            bool allowGround = Targets.AllowGround;
            Targets.AllowGround = false;
            var pendingItemUid = Targets.ItemUid;
            Targets.ItemUid = Serial.Invalid;
            Targets.LastScriptPoint = new Point3D(x, y, z, _character.MapIndex);
            _character.SetTag("TARGP", $"{x},{y},{z},{_character.MapIndex}");
            _character.SetTag("TARG.X", x.ToString());
            _character.SetTag("TARG.Y", y.ToString());
            _character.SetTag("TARG.Z", z.ToString());
            _character.SetTag("TARG.MAP", _character.MapIndex.ToString());
            _character.SetTag("TARG.UID", $"0{serial:X}");

            IScriptObj? argo = null;
            if (serial != 0 && serial != 0xFFFFFFFF)
                argo = _world.FindObject(new Serial(serial));
            if (argo == null && !allowGround)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            if (FirePendingItemTargetTrigger(pendingItemUid, ResolveItemTargetTrigger(serial, argo), new Serial(serial), x, y, z, graphic) == TriggerResult.True)
            {
                Targets.FunctionArgs = "";
                return;
            }

            var trigArgs = new ExecTriggerArgs(_character, 0, 0, Targets.FunctionArgs)
            {
                Object1 = argo,
                Object2 = pendingItemUid.IsValid
                    ? ((IScriptObj?)_world.FindItem(pendingItemUid) ?? _character)
                    : _character
            };
            Targets.FunctionArgs = "";

            // Snapshot position before running the script function so we can
            // detect if it moved the character (e.g. SRC.GO <TARGP>).
            // We cannot rely on Targets.LastScriptPoint because the script may
            // chain another TARGETF which calls ClearPendingTargetState and
            // clears Targets.LastScriptPoint before we get back here.
            var posBefore = _character.Position;
            _triggerDispatcher.Runner.RunFunction(func, _character, _client, trigArgs);
            if (_character != null && !_character.Position.Equals(posBefore))
            {
                _world.MoveCharacter(_character, _character.Position);
                Resync();
                _mountEngine?.EnsureMountedState(_character);
                BroadcastDrawObject(_character);
            }
            return;
        }

        if (_character.TryGetTag("CAST_SPELL", out string? spellStr) &&
            Enum.TryParse<SpellType>(spellStr, out var spell))
        {
            _character.RemoveTag("CAST_SPELL");
            HandleCastSpell(spell, serial);
        }
    }

    private ItemTrigger ResolveItemTargetTrigger(uint serial, IScriptObj? target)
    {
        if (target is Character) return ItemTrigger.TargOnChar;
        if (target is Item) return ItemTrigger.TargOnItem;
        if (serial == 0 || serial == 0xFFFFFFFF) return ItemTrigger.TargOnGround;
        return ItemTrigger.TargOnGround;
    }

    private TriggerResult FirePendingItemTargetTrigger(Serial sourceItemUid, ItemTrigger trigger, Serial targetUid,
        short x, short y, sbyte z, ushort graphic)
    {
        if (!sourceItemUid.IsValid || _triggerDispatcher == null)
            return TriggerResult.Default;

        var sourceItem = _world.FindItem(sourceItemUid);
        if (sourceItem == null)
            return TriggerResult.Default;

        IScriptObj? targetObj = targetUid.IsValid ? _world.FindObject(targetUid) : null;
        return _triggerDispatcher.FireItemTrigger(sourceItem, trigger, new TriggerArgs
        {
            CharSrc = _character,
            ItemSrc = sourceItem,
            O1 = targetObj,
            N1 = x,
            N2 = y,
            N3 = z,
            S1 = graphic.ToString()
        });
    }

    private static bool IsTargetCancelled(uint serial, short x, short y, sbyte z, ushort graphic)
    {
        // Classic cancel payload variant (seen in some clients): serial=0, x=y=0xFFFF.
        if (serial == 0 && (ushort)x == 0xFFFF && (ushort)y == 0xFFFF)
            return true;

        // Client cancel is most commonly serial=0xFFFFFFFF.
        if (serial == 0xFFFFFFFF)
            return true;

        // Some clients send a fully-zero target payload on ESC.
        if (serial == 0 && x == 0 && y == 0 && z == 0 && graphic == 0)
            return true;

        // Legacy cancel payload variant with -1 coordinates.
        if (serial == 0 && x == -1 && y == -1 && z == -1)
            return true;

        // Additional client cancel variant: serial=0 with out-of-world x/y.
        // Note: z < 0 is valid (caves/dungeons), only reject impossible x/y.
        if (serial == 0 && (x < 0 || y < 0))
            return true;

        // Another observed cancel form: invalid serial + no model + max coords.
        if (serial == 0 && graphic == 0 && ((ushort)x == 0xFFFF || (ushort)y == 0xFFFF))
            return true;

        return false;
    }

    // ==================== Gump Response ====================

    public void HandleGumpResponse(uint serial, uint gumpId, uint buttonId,
        uint[] switches, (ushort Id, string Text)[] textEntries)
    {
        if (_character == null) return;

        if (!Gumps.ActiveGumps.Remove(gumpId))
        {
            _logger.LogWarning("Rejected forged/stale gump response from {Char}: serial=0x{S:X}, gumpId=0x{G:X}, button={B}",
                _character.Name, serial, gumpId, buttonId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_pendingDialogCloseFunction))
        {
            string closeFn = _pendingDialogCloseFunction;
            _pendingDialogCloseFunction = null;
            var trigArgs = new ExecTriggerArgs(_character, (int)buttonId, (int)gumpId, _pendingDialogArgs)
            {
                Object1 = _character,
                Object2 = _character
            };

            // Allow script-provided close function tokens like CTAG0.HELP_TYPE.
            if (TryResolveScriptVariable(closeFn, _character, trigArgs, out string resolvedCloseFn) &&
                !string.IsNullOrWhiteSpace(resolvedCloseFn))
            {
                closeFn = resolvedCloseFn;
            }

            closeFn = closeFn.Trim().Trim(',', ';');
            if (closeFn.Equals("DIALOGCLOSE", StringComparison.OrdinalIgnoreCase) ||
                closeFn.Equals("DIALOGCLOSE()", StringComparison.OrdinalIgnoreCase))
            {
                closeFn = $"f_dialogclose_{_pendingDialogArgs}";
            }
            if (string.IsNullOrWhiteSpace(closeFn))
                closeFn = $"f_dialogclose_{_pendingDialogArgs}";

            _pendingDialogArgs = "";
            if (_triggerDispatcher?.Runner != null)
            {
                // Script-first fallback chain:
                // 1) explicit/variable-resolved close function
                // 2) default f_dialogclose_<dialogId>
                if (!_triggerDispatcher.Runner.TryRunFunction(closeFn, _character, _client, trigArgs, out _))
                {
                    string defaultCloseFn = $"f_dialogclose_{trigArgs.ArgString.Trim().Trim(',', ';')}";
                    _triggerDispatcher.Runner.TryRunFunction(defaultCloseFn, _character, _client, trigArgs, out _);
                }
            }
        }

        // Route to registered callback if present
        if (Gumps.Callbacks.TryGetValue(gumpId, out var callback))
        {
            Gumps.Callbacks.Remove(gumpId);
            callback(buttonId, switches, textEntries);
            return;
        }

        _logger.LogDebug("GumpResponse: serial=0x{S:X}, gumpId=0x{G:X}, button={B}",
            serial, gumpId, buttonId);
    }

    // ==================== Gump Sending ====================

    /// <summary>Send a gump dialog to the client. Optionally register a response callback.</summary>
    public void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null)
    {
        if (_character == null) return;

        if (callback != null)
            Gumps.Callbacks[gump.GumpId] = callback;
        Gumps.ActiveGumps.Add(gump.GumpId);

        string layout = gump.BuildLayoutString();
        int gx = gump.ExplicitX ?? (gump.Width > 0 ? (800 - gump.Width) / 2 : 50);
        int gy = gump.ExplicitY ?? (gump.Height > 0 ? (600 - gump.Height) / 2 : 50);
        _netState.Send(new PacketGumpDialog(
            gump.Serial, gump.GumpId, gx, gy, layout, gump.Texts));
    }

    /// <summary>Set a callback-based target cursor. Used by housing, pets, etc.</summary>
    internal void SetPendingTarget(Action<uint, short, short, sbyte, ushort> callback, byte cursorType = 1)
    {
        if (Targets.CursorActive)
        {
            int replacedSkill = Targets.SkillCancelId;
            _netState.Send(new PacketTarget(0x00, 0x00000000, flags: 3));
            if (replacedSkill >= 0 && _character != null)
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillTargetCancel,
                    new TriggerArgs { CharSrc = _character, N1 = replacedSkill });
        }

        ClearPendingTargetState();
        Targets.Callback = callback;
        Targets.CursorActive = true;
        _netState.Send(new PacketTarget(cursorType, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    // ==================== Information Skills ====================
}
