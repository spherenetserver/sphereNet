using System.Text;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Builds UO protocol packets for bot clients.
/// These packets mimic what a real UO client would send.
/// </summary>
public static class BotPacketBuilder
{
    /// <summary>Build 0xEF Login Seed packet (21 bytes).</summary>
    public static byte[] BuildLoginSeed(uint seed, uint clientMajor = 7, uint clientMinor = 0, uint clientRevision = 0, uint clientPatch = 0)
    {
        var packet = new byte[21];
        packet[0] = 0xEF;
        WriteUInt32BE(packet, 1, seed);
        WriteUInt32BE(packet, 5, clientMajor);
        WriteUInt32BE(packet, 9, clientMinor);
        WriteUInt32BE(packet, 13, clientRevision);
        WriteUInt32BE(packet, 17, clientPatch);
        return packet;
    }

    /// <summary>Build 0x80 Account Login packet (62 bytes).</summary>
    public static byte[] BuildAccountLogin(string account, string password)
    {
        var packet = new byte[62];
        packet[0] = 0x80;
        WriteAsciiFixed(packet, 1, account, 30);
        WriteAsciiFixed(packet, 31, password, 30);
        packet[61] = 0x00; // next login key
        return packet;
    }

    /// <summary>Build 0xA0 Server Select packet (3 bytes).</summary>
    public static byte[] BuildServerSelect(ushort serverIndex = 0)
    {
        var packet = new byte[3];
        packet[0] = 0xA0;
        WriteUInt16BE(packet, 1, serverIndex);
        return packet;
    }

    /// <summary>Build 0x91 Game Server Login packet (65 bytes).</summary>
    public static byte[] BuildGameLogin(string account, string password, uint authId)
    {
        var packet = new byte[65];
        packet[0] = 0x91;
        WriteUInt32BE(packet, 1, authId);
        WriteAsciiFixed(packet, 5, account, 30);
        WriteAsciiFixed(packet, 35, password, 30);
        return packet;
    }

    /// <summary>Build 0x5D Character Select packet (73 bytes).</summary>
    public static byte[] BuildCharSelect(int slotIndex, string charName)
    {
        var packet = new byte[73];
        packet[0] = 0x5D;
        WriteUInt32BE(packet, 1, 0xEDEDEDED); // pattern1
        WriteAsciiFixed(packet, 5, charName, 30);
        WriteUInt16BE(packet, 35, 0); // unknown
        WriteUInt32BE(packet, 37, 0x1F); // client flags (T2A+)
        WriteUInt32BE(packet, 41, 0xEDEDEDED); // pattern2
        WriteUInt32BE(packet, 45, 1); // login count
        // 16 bytes padding at 49
        WriteInt32BE(packet, 65, slotIndex);
        // 4 bytes clientIP at 69
        return packet;
    }

    /// <summary>Build 0x02 Move Request packet (7 bytes).</summary>
    public static byte[] BuildMoveRequest(byte direction, byte sequence, uint fastWalkKey = 0)
    {
        var packet = new byte[7];
        packet[0] = 0x02;
        packet[1] = direction;
        packet[2] = sequence;
        WriteUInt32BE(packet, 3, fastWalkKey);
        return packet;
    }

    /// <summary>Build 0x05 Attack Request packet (5 bytes).</summary>
    public static byte[] BuildAttackRequest(uint targetUid)
    {
        var packet = new byte[5];
        packet[0] = 0x05;
        WriteUInt32BE(packet, 1, targetUid);
        return packet;
    }

    /// <summary>Build 0x06 Double Click packet (5 bytes).</summary>
    public static byte[] BuildDoubleClick(uint targetUid)
    {
        var packet = new byte[5];
        packet[0] = 0x06;
        WriteUInt32BE(packet, 1, targetUid);
        return packet;
    }

    /// <summary>Build 0x72 War Mode packet (5 bytes).</summary>
    public static byte[] BuildWarMode(bool warMode)
    {
        var packet = new byte[5];
        packet[0] = 0x72;
        packet[1] = warMode ? (byte)1 : (byte)0;
        // 3 bytes unknown
        return packet;
    }

    /// <summary>Build 0x73 Ping packet (2 bytes).</summary>
    public static byte[] BuildPing(byte sequence)
    {
        return [0x73, sequence];
    }

    /// <summary>Build 0xBF subcommand 0x05 - Screen size (sent after game login).</summary>
    public static byte[] BuildScreenSize(ushort width = 800, ushort height = 600)
    {
        var packet = new byte[13];
        packet[0] = 0xBF;
        WriteUInt16BE(packet, 1, 13); // length
        WriteUInt16BE(packet, 3, 0x05); // subcommand: screen size
        WriteUInt32BE(packet, 5, 0); // unknown
        WriteUInt16BE(packet, 9, width);
        WriteUInt16BE(packet, 11, height);
        return packet;
    }

    /// <summary>Build 0xBF subcommand 0x0B - Client language (sent after game login).</summary>
    public static byte[] BuildClientLanguage(string lang = "ENU")
    {
        int len = 6 + lang.Length + 1;
        var packet = new byte[len];
        packet[0] = 0xBF;
        WriteUInt16BE(packet, 1, (ushort)len);
        WriteUInt16BE(packet, 3, 0x0B); // subcommand: language
        Encoding.ASCII.GetBytes(lang, 0, lang.Length, packet, 5);
        packet[len - 1] = 0;
        return packet;
    }

    /// <summary>Build 0x12 Skill Use packet (variable length).</summary>
    public static byte[] BuildSkillUse(ushort skillId)
    {
        string cmd = $" {skillId} 0";
        int len = 4 + cmd.Length + 1;
        var packet = new byte[len];
        packet[0] = 0x12;
        WriteUInt16BE(packet, 1, (ushort)len);
        packet[3] = 0x24; // skill use command type
        Encoding.ASCII.GetBytes(cmd, 0, cmd.Length, packet, 4);
        packet[len - 1] = 0; // null terminator
        return packet;
    }

    /// <summary>Build 0x03 Speech packet (variable length).</summary>
    public static byte[] BuildSpeech(string text, byte type = 0, ushort hue = 0x03B2, ushort font = 3)
    {
        int len = 8 + text.Length + 1;
        var packet = new byte[len];
        packet[0] = 0x03;
        WriteUInt16BE(packet, 1, (ushort)len);
        packet[3] = type;
        WriteUInt16BE(packet, 4, hue);
        WriteUInt16BE(packet, 6, font);
        Encoding.ASCII.GetBytes(text, 0, text.Length, packet, 8);
        packet[len - 1] = 0;
        return packet;
    }

    /// <summary>Build 0x07 Pick Up Item packet (7 bytes).</summary>
    public static byte[] BuildPickUp(uint serial, ushort amount)
    {
        var packet = new byte[7];
        packet[0] = 0x07;
        WriteUInt32BE(packet, 1, serial);
        WriteUInt16BE(packet, 5, amount);
        return packet;
    }

    /// <summary>Build 0x08 Drop Item packet (14 bytes, 6.0.1.7+).</summary>
    public static byte[] BuildDropToContainer(uint itemSerial, uint containerSerial,
        short x = -1, short y = -1, sbyte z = 0)
    {
        var packet = new byte[15];
        packet[0] = 0x08;
        WriteUInt32BE(packet, 1, itemSerial);
        WriteUInt16BE(packet, 5, (ushort)x);
        WriteUInt16BE(packet, 7, (ushort)y);
        packet[9] = (byte)z;
        packet[10] = 0; // grid index
        WriteUInt32BE(packet, 11, containerSerial);
        return packet;
    }

    /// <summary>Build 0x08 Drop Item to World packet (15 bytes, 6.0.1.7+).</summary>
    public static byte[] BuildDropToWorld(uint itemSerial, short x, short y, sbyte z)
    {
        return BuildDropToContainer(itemSerial, 0xFFFFFFFF, x, y, z);
    }

    /// <summary>Build 0x6C Target Response packet (19 bytes).</summary>
    public static byte[] BuildTargetObject(uint cursorId, uint targetSerial)
    {
        var packet = new byte[19];
        packet[0] = 0x6C;
        packet[1] = 0x00; // object target
        WriteUInt32BE(packet, 2, cursorId);
        packet[6] = 0x00; // flags
        WriteUInt32BE(packet, 7, targetSerial);
        return packet;
    }

    /// <summary>Build 0x6C Target Location Response packet (19 bytes).</summary>
    public static byte[] BuildTargetLocation(uint cursorId, short x, short y, sbyte z, ushort graphic = 0)
    {
        var packet = new byte[19];
        packet[0] = 0x6C;
        packet[1] = 0x01; // ground target
        WriteUInt32BE(packet, 2, cursorId);
        packet[6] = 0x00;
        WriteUInt16BE(packet, 11, (ushort)x);
        WriteUInt16BE(packet, 13, (ushort)y);
        packet[15] = (byte)z;
        packet[16] = 0;
        WriteUInt16BE(packet, 17, graphic);
        return packet;
    }

    /// <summary>Build 0x3B Buy Items packet (variable).</summary>
    public static byte[] BuildBuyItems(uint vendorSerial, (ushort layer, uint serial, ushort amount)[] items)
    {
        int len = 8 + items.Length * 7;
        var packet = new byte[len];
        packet[0] = 0x3B;
        WriteUInt16BE(packet, 1, (ushort)len);
        WriteUInt32BE(packet, 3, vendorSerial);
        packet[7] = 0x02; // flag: buy
        int offset = 8;
        foreach (var (layer, serial, amount) in items)
        {
            packet[offset++] = (byte)layer;
            WriteUInt32BE(packet, offset, serial); offset += 4;
            WriteUInt16BE(packet, offset, amount); offset += 2;
        }
        return packet;
    }

    /// <summary>Build 0x9F Sell Items packet (variable).</summary>
    public static byte[] BuildSellItems(uint vendorSerial, (uint serial, ushort amount)[] items)
    {
        int len = 9 + items.Length * 6;
        var packet = new byte[len];
        packet[0] = 0x9F;
        WriteUInt16BE(packet, 1, (ushort)len);
        WriteUInt32BE(packet, 3, vendorSerial);
        WriteUInt16BE(packet, 7, (ushort)items.Length);
        int offset = 9;
        foreach (var (serial, amount) in items)
        {
            WriteUInt32BE(packet, offset, serial); offset += 4;
            WriteUInt16BE(packet, offset, amount); offset += 2;
        }
        return packet;
    }

    /// <summary>Build 0xB1 Gump Response packet (variable).</summary>
    public static byte[] BuildGumpResponse(uint serial, uint gumpId, int buttonId, int[]? switches = null)
    {
        int switchCount = switches?.Length ?? 0;
        int len = 23 + switchCount * 4;
        var packet = new byte[len];
        packet[0] = 0xB1;
        WriteUInt16BE(packet, 1, (ushort)len);
        WriteUInt32BE(packet, 3, serial);
        WriteUInt32BE(packet, 7, gumpId);
        WriteUInt32BE(packet, 11, (uint)buttonId);
        WriteUInt32BE(packet, 15, (uint)switchCount);
        int offset = 19;
        if (switches != null)
            foreach (int sw in switches)
            {
                WriteUInt32BE(packet, offset, (uint)sw);
                offset += 4;
            }
        WriteUInt32BE(packet, offset, 0); // text entry count
        return packet;
    }

    /// <summary>Build 0x12 Cast Spell packet (variable).</summary>
    public static byte[] BuildCastSpell(int spellId)
    {
        string cmd = $" {spellId}";
        int len = 4 + cmd.Length + 1;
        var packet = new byte[len];
        packet[0] = 0x12;
        WriteUInt16BE(packet, 1, (ushort)len);
        packet[3] = 0x56; // spell cast command type
        Encoding.ASCII.GetBytes(cmd, 0, cmd.Length, packet, 4);
        packet[len - 1] = 0;
        return packet;
    }

    private static void WriteUInt32BE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteInt32BE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteUInt16BE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static void WriteAsciiFixed(byte[] buf, int offset, string text, int length)
    {
        int copyLen = Math.Min(text.Length, length);
        Encoding.ASCII.GetBytes(text, 0, copyLen, buf, offset);
    }
}
