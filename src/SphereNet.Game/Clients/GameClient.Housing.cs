using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient : IHouseDesignClient
{
    /// <summary>
    /// Enter house-customization mode on this client: start a design session
    /// from the committed design, flip the client's design UI on (0xBF 0x20)
    /// and stream the current design (0xD8).
    /// </summary>
    public void BeginHouseCustomization(Item multi)
    {
        if (_character == null || _customHousing == null)
            return;
        if (multi.ItemType != ItemType.MultiCustom)
        {
            SysMessage("This house is not customizable.");
            return;
        }
        if (_character.PrivLevel < PrivLevel.GM && !_customHousing.CanCustomize(_character, multi))
        {
            SysMessage("Only the house owner may customize this house.");
            return;
        }

        // The whole lifecycle - @HouseDesignBegin and its N1/N2/N3, ending a
        // previous designer, moving and hiding this one - lives in the engine
        // (Source-X CItemMultiCustom::BeginCustomize).
        _customHousing.Begin(_character, multi, this);
    }

    // ---- IHouseDesignClient: the engine's handle on this designer ----

    void IHouseDesignClient.SendHouseCustomizationMode(Item multi, bool begin) =>
        Send(new PacketHouseCustomizationMode(multi.Uid.Value, begin));

    void IHouseDesignClient.SendWorkingHouseDesign(Item multi, HouseDesign design) =>
        SendHouseDesign(multi, design);

    void IHouseDesignClient.SendCommittedHouseDesign(Item multi) => SendCommittedDesign(multi);

    void IHouseDesignClient.SendDesignerRemoveObject(Item item) =>
        _netState.Send(new PacketDeleteObject(item.Uid.Value));

    void IHouseDesignClient.SendDesignerMoved()
    {
        if (_character != null)
            SendSelfRedraw();
    }

    HouseDesignBeginResult? IHouseDesignClient.FireHouseDesignBegin(Item multi, long n1, long n2, long n3)
    {
        // Source-X runs the trigger - and acts on its N1/N2/N3 - only when a
        // script hooks it (IsTrigUsed(TRIGGER_HOUSEDESIGNBEGIN), :118).
        if (_character == null || _triggerDispatcher == null ||
            !_triggerDispatcher.IsTriggerNameUsed("HouseDesignBegin"))
            return null;
        var args = new TriggerArgs
        {
            CharSrc = _character, O1 = multi, N1 = n1, N2 = n2, N3 = n3,
            ScriptConsole = this
        };
        bool cancel = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.HouseDesignBegin, args)
            == TriggerResult.True;
        return new HouseDesignBeginResult(cancel, args.N1, args.N2, args.N3);
    }

    bool IHouseDesignClient.FireHouseDesignExit(Item multi, bool forced) =>
        _character != null && _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.HouseDesignExit,
            new TriggerArgs
            {
                CharSrc = _character, O1 = multi, N1 = forced ? 1 : 0, ScriptConsole = this
            }) == TriggerResult.True;

    bool IHouseDesignClient.FireHouseDesignCommit(Item multi, CustomHousingEngine.CommitPreview pv)
    {
        if (_character == null || _triggerDispatcher == null)
            return false;
        // The reference pack prices the build from ARGN1/ARGN2 - (new - old) * 500
        // gold - and RETURNs 1 when the owner cannot afford it (Scripts-X
        // house_typedefs.scp:603-615). The counts come off the working design after
        // the per-piece filter (CommitChanges:272-327).
        var locals = new SphereNet.Scripting.Variables.VarMap();
        locals.SetInt("FIXTURES.OLD", pv.OldFixtures);
        locals.SetInt("FIXTURES.NEW", pv.NewFixtures);
        locals.SetInt("MAXZ", pv.MaxZ);
        var commitArgs = new TriggerArgs
        {
            CharSrc = _character,
            O1 = multi,
            N1 = pv.OldTiles,
            N2 = pv.NewTiles,
            N3 = pv.Revision,
            Locals = locals,
            ScriptConsole = this,
        };
        return _triggerDispatcher.FireCharTrigger(_character, CharTrigger.HouseDesignCommit, commitArgs)
            == TriggerResult.True;
    }

    /// <summary>0xBF sub 0x1E — client requests the design of a house whose
    /// revision it doesn't have cached.</summary>
    internal void HandleQueryDesignDetails(byte[] data)
    {
        if (data.Length < 4)
            return;
        uint serial = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        var multi = _world.FindItem(new Serial(serial));
        if (multi == null)
            return;
        // SendStructureTo: the architect gets the working design, everyone else
        // the committed one (CItemMultiCustom.cpp:871).
        if (_character != null && _customHousing?.GetSession(_character.Uid) is { } session &&
            session.HouseUid == multi.Uid)
        {
            SendHouseDesign(multi, session.Working);
            return;
        }
        SendCommittedDesign(multi);
    }

    /// <summary>Working-design 0xD8 — sent to the designer, fixture tiles
    /// included (Source-X sends invisible components only for the working
    /// design, so the architect sees the doors being placed).</summary>
    private void SendHouseDesign(Item multi, HouseDesign design)
    {
        Send(new PacketHouseDesignDetailed(multi.Uid.Value, design.Revision, design.Tiles));
    }

    /// <summary>Committed-design 0xD8 — fixture tiles are excluded because
    /// commit materialized them as real items the client already draws;
    /// keeping them in the stream doubled every door.</summary>
    private void SendCommittedDesign(Item multi)
    {
        // No engine wired (no fixture materialization either) → raw tags.
        var design = _customHousing != null
            ? _customHousing.GetCommittedDesign(multi)
            : HouseDesign.LoadFromTags(multi);
        IReadOnlyList<HouseDesignTile> tiles = design.Tiles;
        if (tiles.Any(t => !t.Visible))
            tiles = tiles.Where(t => t.Visible).ToList();
        Send(new PacketHouseDesignDetailed(multi.Uid.Value, design.Revision, tiles));
    }

    /// <summary>
    /// 0xD7 encoded design commands. Each payload value is "encoded": one
    /// prefix byte followed by a 4-byte BE integer (ClassicUO sends 0x00 +
    /// value, terminated by 0x0A). Commands arriving without an active design
    /// session are ignored — the client only sends them in design mode, so
    /// stray packets are noise or spoofing.
    /// </summary>
    public void HandleEncodedCommand(ushort subCmd, uint serial, PacketBuffer payload)
    {
        if (_character == null)
            return;

        // 0xD7 sub 0x19 — combat ability request (client Send_UseCombatAbility:
        // [serial][0x19][0:4][abilityIdx:1][0x0A]). Not a house-design command;
        // handled before the design-session gate. N1 = the ability index.
        if (subCmd == 0x19)
        {
            if (payload.Remaining >= 4)
                payload.ReadUInt32();
            int ability = payload.Remaining >= 1 ? payload.ReadByte() : 0;
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserSpecialMove,
                new TriggerArgs { CharSrc = _character, N1 = ability, ScriptConsole = this });
            return;
        }

        // 0xD7 0x28 / 0x32 — guild and quest button presses. Source-X routes
        // these through the EXTAOS (0xD7) space (PacketGuildButton /
        // PacketQuestButton, receive.cpp:4161/4194); a real client never sends
        // them on 0xBF. Each just fires its @User*Button trigger.
        if (subCmd == 0x28)
        {
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserGuildButton,
                new TriggerArgs { CharSrc = _character, N1 = 0x28, ScriptConsole = this });
            return;
        }
        if (subCmd == 0x32)
        {
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserQuestButton,
                new TriggerArgs { CharSrc = _character, N1 = 0x32, ScriptConsole = this });
            return;
        }

        // 0xD7 0x1E — EquipLastWeapon client macro (Source-X PacketEquipLastWeapon,
        // receive.cpp:4120): re-wield the last weapon held. No item is created —
        // HandleItemEquip re-checks "still in my possession", so a dropped/traded
        // weapon can't be summoned back.
        if (subCmd == EncodedCommandRegistry.EquipLastWeapon)
        {
            var lastUid = _character.LastWeaponUid;
            if (!lastUid.IsValid)
                return;
            if (_world.FindObject(lastUid) is not Item weapon || weapon.IsDeleted)
                return;
            // Already wielding it → no-op (mirrors Source-X's m_uidWeapon check).
            if (ReferenceEquals(_character.GetEquippedItem(Layer.OneHanded), weapon) ||
                ReferenceEquals(_character.GetEquippedItem(Layer.TwoHanded), weapon))
                return;
            // Mirror Source-X ItemPickup: pull the weapon out of the player's own
            // pack into hand so the equip's possession check passes. A weapon that
            // isn't sitting in the player's pack (dropped/traded/nested) is left alone.
            if (weapon.ContainedIn != _character.Uid)
            {
                if (_world.FindObject(weapon.ContainedIn) is not Item pack ||
                    pack.ContainedIn != _character.Uid)
                    return;
                pack.RemoveItem(weapon);
                weapon.ContainedIn = _character.Uid;
            }
            byte layer = (byte)(weapon.IsTwoHanded ? Layer.TwoHanded : Layer.OneHanded);
            HandleItemEquip(lastUid.Value, layer, _character.Uid.Value);
            return;
        }

        if (_customHousing == null)
            return;
        if (!_customHousing.IsSessionAuthorized(_character))
            return;
        var session = _customHousing.GetSession(_character.Uid);
        if (session == null)
            return;
        var multi = _customHousing.GetSessionMulti(_character.Uid);
        if (multi == null || multi.Uid.Value != serial)
            return;

        switch (subCmd)
        {
            case EncodedCommandRegistry.Build:
            {
                ushort tile = (ushort)ReadEncodedValue(payload);
                int x = ReadEncodedValue(payload);
                int y = ReadEncodedValue(payload);
                _customHousing.Build(_character, tile, x, y);
                break;
            }
            case EncodedCommandRegistry.Delete:
            case EncodedCommandRegistry.RoofDelete:
            {
                ushort tile = (ushort)ReadEncodedValue(payload);
                int x = ReadEncodedValue(payload);
                int y = ReadEncodedValue(payload);
                int z = ReadEncodedValue(payload);
                _customHousing.Erase(_character, tile, x, y, z);
                break;
            }
            case EncodedCommandRegistry.Stairs:
            {
                ushort tile = (ushort)ReadEncodedValue(payload);
                int x = ReadEncodedValue(payload);
                int y = ReadEncodedValue(payload);
                _customHousing.Stairs(_character, tile, x, y);
                break;
            }
            case EncodedCommandRegistry.Roof:
            {
                ushort tile = (ushort)ReadEncodedValue(payload);
                int x = ReadEncodedValue(payload);
                int y = ReadEncodedValue(payload);
                int z = ReadEncodedValue(payload);
                _customHousing.Roof(_character, tile, x, y, z);
                break;
            }
            case EncodedCommandRegistry.Level:
            {
                _customHousing.SetLevel(_character, ReadEncodedValue(payload));
                break;
            }
            case EncodedCommandRegistry.Clear:
                _customHousing.Clear(_character);
                SendHouseDesign(multi, _customHousing.GetSession(_character.Uid)!.Working);
                break;
            case EncodedCommandRegistry.Backup:
                _customHousing.BackupDesign(_character);
                break;
            case EncodedCommandRegistry.Restore:
                _customHousing.RestoreDesign(_character);
                SendHouseDesign(multi, _customHousing.GetSession(_character.Uid)!.Working);
                break;
            case EncodedCommandRegistry.Sync:
                SendHouseDesign(multi, session.Working);
                break;
            case EncodedCommandRegistry.Revert:
                _customHousing.Revert(_character);
                SendHouseDesign(multi, _customHousing.GetSession(_character.Uid)!.Working);
                break;
            case EncodedCommandRegistry.Commit:
            {
                // Source-X CommitChanges: the per-piece @HouseDesignCommitItem filter,
                // then @HouseDesignCommit (which may refuse) on what is left, then the
                // commit itself. Design mode stays on (the reference does not end it);
                // observers are told the new revision through the engine's
                // DesignCommitted hook.
                _customHousing.Commit(_character, this);
                break;
            }
            case EncodedCommandRegistry.Close:
            case EncodedCommandRegistry.Action:
            case EncodedCommandRegistry.Action2:
                // Source-X EndCustomize(false): @HouseDesignExit RETURN 1 keeps the
                // designer in design mode.
                _customHousing.End(_character, forced: false);
                break;
        }
    }

    /// <summary>Read one "encoded" 0xD7 payload value: prefix byte + BE int32.</summary>
    private static int ReadEncodedValue(PacketBuffer payload)
    {
        if (payload.Remaining < 5)
            return 0;
        payload.ReadByte();
        return (int)payload.ReadUInt32();
    }
}
