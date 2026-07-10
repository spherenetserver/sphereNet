using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
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

        var session = _customHousing.Begin(_character, multi);
        Send(new PacketHouseCustomizationMode(multi.Uid.Value, begin: true));
        SendHouseDesign(multi, session.Working);
    }

    /// <summary>0xBF sub 0x1E — client requests the design of a house whose
    /// revision it doesn't have cached. Always answered from the committed
    /// design (DESIGN_n tags), never from someone's working session.</summary>
    internal void HandleQueryDesignDetails(byte[] data)
    {
        if (data.Length < 4)
            return;
        uint serial = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        var multi = _world.FindItem(new Serial(serial));
        if (multi == null)
            return;
        SendHouseDesign(multi, HouseDesign.LoadFromTags(multi));
    }

    private void SendHouseDesign(Item multi, HouseDesign design)
    {
        Send(new PacketHouseDesignDetailed(multi.Uid.Value, design.Revision, design.Tiles));
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
                uint? revision = _customHousing.Commit(_character);
                if (revision == null)
                    break;
                Send(new PacketHouseCustomizationMode(multi.Uid.Value, begin: false));
                SendHouseDesign(multi, HouseDesign.LoadFromTags(multi));
                // Observers re-query (0xBF 0x1E) on revision mismatch and get
                // the committed design through HandleQueryDesignDetails.
                BroadcastNearby?.Invoke(multi.Position, 24,
                    new PacketHouseDesignVersion(multi.Uid.Value, revision.Value), _character.Uid.Value);
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.HouseDesignCommit,
                    new TriggerArgs { CharSrc = _character, O1 = multi, N1 = (int)revision.Value });
                break;
            }
            case EncodedCommandRegistry.Close:
            case EncodedCommandRegistry.Action:
            case EncodedCommandRegistry.Action2:
            {
                _customHousing.End(_character);
                Send(new PacketHouseCustomizationMode(multi.Uid.Value, begin: false));
                SendHouseDesign(multi, HouseDesign.LoadFromTags(multi));
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.HouseDesignExit,
                    new TriggerArgs { CharSrc = _character, O1 = multi });
                break;
            }
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
