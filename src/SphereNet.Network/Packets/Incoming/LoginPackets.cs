using SphereNet.Core.Types;
using SphereNet.Network.Packets;
using SphereNet.Network.State;

namespace SphereNet.Network.Packets.Incoming;

/// <summary>0x80 — Login request (client → login server).</summary>
public sealed class PacketLoginRequest : PacketHandler
{
    public PacketLoginRequest() : base(0x80, 62) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        string account = buffer.ReadAsciiFixed(30);
        string password = buffer.ReadAsciiFixed(30);
        byte nextLoginKey = buffer.ReadByte();

        state.OnLoginRequest(account, password);
    }
}

/// <summary>0x91 — Game server login (client → game server after relay).</summary>
public sealed class PacketGameLogin : PacketHandler
{
    public PacketGameLogin() : base(0x91, 65) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint authId = buffer.ReadUInt32();
        string account = buffer.ReadAsciiFixed(30);
        string password = buffer.ReadAsciiFixed(30);

        state.OnGameLogin(account, password, authId);
    }
}

/// <summary>0xF8 — Create Character (HS, 7.0+ clients). 106 bytes.</summary>
public sealed class PacketCreateCharacterHS : PacketHandler
{
    public PacketCreateCharacterHS() : base(0xF8, 106) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        buffer.ReadUInt32(); // pattern1
        buffer.ReadUInt32(); // pattern2
        buffer.ReadByte();   // pattern3
        string charName = buffer.ReadAsciiFixed(30);

        buffer.ReadBytes(2); // unknown
        uint clientFlags = buffer.ReadUInt32();
        buffer.ReadBytes(8); // unknown
        byte profession = buffer.ReadByte();
        buffer.ReadBytes(15); // unknown

        byte genderRace = buffer.ReadByte();
        byte str = buffer.ReadByte();
        byte dex = buffer.ReadByte();
        byte intl = buffer.ReadByte();

        var skills = new (byte Id, byte Value)[4];
        for (int i = 0; i < 4; i++)
        {
            skills[i].Id = buffer.ReadByte();
            skills[i].Value = buffer.ReadByte();
        }

        ushort skinHue = buffer.ReadUInt16();
        ushort hairStyle = buffer.ReadUInt16();
        ushort hairHue = buffer.ReadUInt16();
        ushort beardStyle = buffer.ReadUInt16();
        ushort beardHue = buffer.ReadUInt16();

        buffer.ReadByte();            // shard index
        byte city = buffer.ReadByte(); // starting city index (into the 0xA9 list)

        buffer.ReadBytes(8); // slot (4) + client IP (4)
        ushort shirtHue = buffer.ReadUInt16();
        ushort pantsHue = buffer.ReadUInt16();

        bool female = (genderRace % 2) != 0;

        state.OnCharCreate(new CharCreateInfo
        {
            Name = charName,
            Female = female,
            ClientFlags = clientFlags,
            Profession = profession,
            // 0xF8 is a 7.0.16+ (Stygian Abyss) packet, so the byte uses the SA
            // race/sex encoding.
            Race = RaceFromGenderRace(genderRace, saEncoding: true),
            Str = str, Dex = dex, Int = intl,
            SkinHue = skinHue,
            HairStyle = hairStyle, HairHue = hairHue,
            BeardStyle = beardStyle, BeardHue = beardHue,
            ShirtHue = shirtHue, PantsHue = pantsHue,
            Skills = skills,
            City = city,
        });
    }

    /// <summary>Map the create packet's gender/race byte to a race id (1 human,
    /// 2 elf, 3 gargoyle). Source-X PacketCreate::onReceive: 7.0.0.0+ clients encode
    /// 2/3=human, 4/5=elf, 6/7=gargoyle; older clients encode 0/1=human, 2/3=elf.</summary>
    internal static byte RaceFromGenderRace(byte genderRace, bool saEncoding)
    {
        if (saEncoding)
            return genderRace switch { <= 3 => 1, <= 5 => 2, _ => 3 };
        return (byte)(genderRace >= 2 ? 2 : 1);
    }
}

/// <summary>0x8D — Create Character (KR / Stygian Abyss Enhanced Client). 146 bytes on
/// the wire including the length word, which the framing has already consumed.
/// Source-X PacketCreateNew::onReceive (receive.cpp:1511): the race byte is a
/// RACE_TYPE (KR sends it one lower than SA), and a chosen profession replaces the
/// stats and the four skills with a fixed table because the packet carries no
/// skills for it. Routed into the same creation path as 0x00 / 0xF8.</summary>
public sealed class PacketCreateCharacterEnhanced : PacketHandler
{
    public PacketCreateCharacterEnhanced() : base(0x8D, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        buffer.ReadUInt32(); // pattern1
        buffer.ReadUInt32(); // pattern2
        string charName = buffer.ReadAsciiFixed(30);
        buffer.ReadBytes(30); // unknown

        byte profession = buffer.ReadByte();
        byte city = buffer.ReadByte();
        byte sex = buffer.ReadByte();
        byte race = buffer.ReadByte();
        // Source-X: "SA client sends race packet one higher than KR".
        if (state.IsKingdomRebornClient && race > 0)
            race--;
        byte str = buffer.ReadByte();
        byte dex = buffer.ReadByte();
        byte intl = buffer.ReadByte();
        ushort skinHue = buffer.ReadUInt16();
        buffer.ReadBytes(8); // unknown

        var skills = new (byte Id, byte Value)[4];
        for (int i = 0; i < 4; i++)
        {
            skills[i].Id = buffer.ReadByte();
            skills[i].Value = buffer.ReadByte();
        }

        buffer.ReadBytes(26); // unknown
        ushort hairHue = buffer.ReadUInt16();
        ushort hairStyle = buffer.ReadUInt16();
        buffer.ReadBytes(6); // unknown
        ushort shirtHue = buffer.ReadUInt16();
        ushort shirtId = buffer.ReadUInt16();
        buffer.ReadByte(); // unknown
        ushort faceHue = buffer.ReadUInt16();
        ushort faceId = buffer.ReadUInt16();
        buffer.ReadByte(); // unknown
        ushort beardHue = buffer.ReadUInt16();
        ushort beardStyle = buffer.ReadUInt16();

        ApplyProfessionTemplate(profession, ref str, ref dex, ref intl, skills);

        state.OnCharCreate(new CharCreateInfo
        {
            Name = charName,
            Female = sex > 0,
            // Source-X passes UINT32_MAX as the client flags for this packet.
            ClientFlags = uint.MaxValue,
            Profession = profession,
            Race = race,
            Str = str, Dex = dex, Int = intl,
            SkinHue = skinHue,
            HairStyle = hairStyle, HairHue = hairHue,
            BeardStyle = beardStyle, BeardHue = beardHue,
            // Source-X hands the shirt hue to doCreate as both shirt and pants hue.
            ShirtHue = shirtHue, PantsHue = shirtHue,
            ShirtId = shirtId,
            FaceId = faceId, FaceHue = faceHue,
            Skills = skills,
            City = city,
        });
    }

    /// <summary>The PROFESSION_* table of PacketCreateNew::onReceive
    /// (receive.cpp:1557-1650). PROFESSION_ADVANCED (0) and unknown ids keep what the
    /// client sent.</summary>
    internal static void ApplyProfessionTemplate(byte profession, ref byte str, ref byte dex, ref byte intl,
        (byte Id, byte Value)[] skills)
    {
        (byte S, byte D, byte I, byte K1, byte K2, byte K3, byte K4)? t = profession switch
        {
            1 => (45, 35, 10, 40, 27, 17, 1),   // warrior: swordsmanship, tactics, healing, anatomy
            2 => (25, 20, 45, 25, 16, 46, 43),  // mage: magery, evalint, meditation, wrestling
            3 => (60, 10, 10, 7, 45, 37, 34),   // blacksmith: blacksmithing, mining, tinkering, tailoring
            4 => (25, 20, 45, 49, 32, 42, 46),  // necromancer: necromancy, spiritspeak, fencing, meditation
            5 => (45, 20, 25, 51, 40, 27, 50),  // paladin: chivalry, swordsmanship, tactics, focus
            6 => (40, 30, 10, 52, 40, 50, 5),   // samurai: bushido, swordsmanship, focus, parrying
            7 => (40, 30, 10, 53, 42, 21, 47),  // ninja: ninjitsu, fencing, hiding, stealth
            _ => null,
        };
        if (t is not { } p)
            return;
        str = p.S; dex = p.D; intl = p.I;
        skills[0] = (p.K1, 30);
        skills[1] = (p.K2, 30);
        skills[2] = (p.K3, 30);
        skills[3] = (p.K4, 30);
    }
}

/// <summary>0x00 — Create Character (old clients). 104 bytes.</summary>
public sealed class PacketCreateCharacter : PacketHandler
{
    public PacketCreateCharacter() : base(0x00, 104) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        buffer.ReadUInt32(); // pattern1
        buffer.ReadUInt32(); // pattern2
        buffer.ReadByte();   // pattern3
        string charName = buffer.ReadAsciiFixed(30);

        buffer.ReadBytes(2); // unknown
        uint clientFlags = buffer.ReadUInt32();
        buffer.ReadBytes(8); // unknown
        byte profession = buffer.ReadByte();
        buffer.ReadBytes(15); // unknown

        byte genderRace = buffer.ReadByte();
        byte str = buffer.ReadByte();
        byte dex = buffer.ReadByte();
        byte intl = buffer.ReadByte();

        var skills = new (byte Id, byte Value)[3];
        for (int i = 0; i < 3; i++)
        {
            skills[i].Id = buffer.ReadByte();
            skills[i].Value = buffer.ReadByte();
        }

        ushort skinHue = buffer.ReadUInt16();
        ushort hairStyle = buffer.ReadUInt16();
        ushort hairHue = buffer.ReadUInt16();
        ushort beardStyle = buffer.ReadUInt16();
        ushort beardHue = buffer.ReadUInt16();

        buffer.ReadByte();            // shard index
        byte city = buffer.ReadByte(); // starting city index (into the 0xA9 list)

        buffer.ReadBytes(8); // slot (4) + client IP (4)
        ushort shirtHue = buffer.ReadUInt16();
        ushort pantsHue = buffer.ReadUInt16();

        bool female = (genderRace % 2) != 0;

        state.OnCharCreate(new CharCreateInfo
        {
            Name = charName,
            Female = female,
            ClientFlags = clientFlags,
            Profession = profession,
            // The old 0x00 packet is pre-7.0.16; use the SA race encoding only if
            // the client explicitly reports a 7.0.0.0+ version, else the legacy one.
            Race = PacketCreateCharacterHS.RaceFromGenderRace(
                genderRace, saEncoding: state.ClientVersionNumber >= 70_000_000),
            Str = str, Dex = dex, Int = intl,
            SkinHue = skinHue,
            HairStyle = hairStyle, HairHue = hairHue,
            BeardStyle = beardStyle, BeardHue = beardHue,
            ShirtHue = shirtHue, PantsHue = pantsHue,
            Skills = skills,
            City = city,
        });
    }
}

/// <summary>0x5D — Character select.</summary>
public sealed class PacketCharSelect : PacketHandler
{
    public PacketCharSelect() : base(0x5D, 73) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        buffer.ReadUInt32(); // pattern1
        string charName = buffer.ReadAsciiFixed(30);
        buffer.ReadUInt16(); // unknown
        uint clientFlag = buffer.ReadUInt32();
        buffer.ReadUInt32(); // pattern2
        uint loginCount = buffer.ReadUInt32();
        buffer.ReadBytes(16); // padding
        int slotIndex = buffer.ReadInt32();
        buffer.ReadBytes(4); // clientIP

        state.OnCharSelect(slotIndex, charName);
    }
}

/// <summary>0x73 — Ping request.</summary>
public sealed class PacketPing : PacketHandler
{
    public PacketPing() : base(0x73, 2) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte seq = buffer.ReadByte();
        state.OnPingReceived(seq);
    }
}

/// <summary>0x02 — Move request.</summary>
public sealed class PacketMoveRequest : PacketHandler
{
    public PacketMoveRequest() : base(0x02, 7) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        state.LastMovementOpcode = 0x02;
        state.LastMovementBatchSize = 1;
        byte dir = buffer.ReadByte();
        byte seq = buffer.ReadByte();
        uint fastWalkKey = buffer.ReadUInt32();

        state.OnMoveRequest(dir, seq, fastWalkKey);
    }
}

/// <summary>
/// 0xF0 — New movement request (EC/batch) or third-party client extension subcommands.
/// Extension payloads (party/guild/razor) are ignored; movement payloads route to 0x02 handler.
/// Routing uses protocol version when available, falls back to payload-size heuristic.
/// </summary>
public sealed class PacketNewMovementRequest : PacketHandler
{
    public PacketNewMovementRequest() : base(0xF0, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        int remaining = buffer.Remaining;
        if (remaining <= 0)
            return;

        state.LastMovementOpcode = 0xF0;
        state.LastMovementBatchSize = 0;

        // Extension subcommands (≤3 bytes) — no server action required.
        if (remaining <= 3)
        {
            _ = buffer.ReadByte();
            return;
        }

        // Determine routing: batch movement (EC / ModernUO) vs single-step.
        // Prefer explicit version/type detection over payload-size heuristic.
        bool isBatchClient = state.HasProtocolChanges(Core.Enums.ProtocolChanges.StygianAbyss)
            || state.IsKingdomRebornClient || state.IsEnhancedClient;

        if (!isBatchClient)
        {
            // Single-step movement (dir + seq + fastwalk key = 6 bytes).
            // Also handles clients that embed a single move in 0xF0.
            if (remaining >= 6)
            {
                byte dir = buffer.ReadByte();
                byte seq = buffer.ReadByte();
                uint fastWalkKey = buffer.ReadUInt32();
                state.LastMovementBatchSize = 1;
                state.OnMoveRequest(dir, seq, fastWalkKey);
            }
            return;
        }

        // Batch movement: steps count + 34-byte per-step timing/direction block.
        byte steps = buffer.ReadByte();
        var movementSteps = new List<MovementStep>(steps);
        for (int i = 0; i < steps && buffer.Remaining >= 34; i++)
        {
            buffer.ReadUInt32();
            buffer.ReadUInt32();
            buffer.ReadUInt32();
            buffer.ReadUInt32();
            byte seq = buffer.ReadByte();
            byte dir = buffer.ReadByte();
            int mode = buffer.ReadInt32();
            buffer.ReadInt32();
            buffer.ReadInt32();
            buffer.ReadInt32();

            if (mode == 2)
                dir |= 0x80;

            movementSteps.Add(new MovementStep(dir, seq, 0, mode));
        }

        if (movementSteps.Count > 0)
        {
            state.LastMovementBatchSize = movementSteps.Count;
            state.OnMovementBatch(movementSteps);
        }
    }
}

/// <summary>0x03 — ASCII speech request.</summary>
public sealed class PacketSpeechRequest : PacketHandler
{
    public PacketSpeechRequest() : base(0x03, -1) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte type = buffer.ReadByte();
        ushort hue = buffer.ReadUInt16();
        ushort font = buffer.ReadUInt16();
        string text = buffer.ReadAsciiNull();

        state.OnSpeech(type, hue, font, text);
    }
}

/// <summary>0x05 — Attack request.</summary>
public sealed class PacketAttackRequest : PacketHandler
{
    public PacketAttackRequest() : base(0x05, 5) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint targetUid = buffer.ReadUInt32();
        state.OnAttackRequest(targetUid);
    }
}

/// <summary>0x72 — War mode toggle.</summary>
public sealed class PacketWarMode : PacketHandler
{
    public PacketWarMode() : base(0x72, 5) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte warMode = buffer.ReadByte();
        buffer.ReadBytes(3); // unknown
        state.OnWarMode(warMode != 0);
    }
}

/// <summary>0xA0 — Server select (client picks a server from the list).</summary>
public sealed class PacketServerSelect : PacketHandler
{
    public PacketServerSelect() : base(0xA0, 3) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        ushort serverIndex = buffer.ReadUInt16();
        state.OnServerSelect(serverIndex);
    }
}

/// <summary>0x12 — Text command (skill use, spell cast, etc.).</summary>
public sealed class PacketTextCommand : PacketHandler
{
    public PacketTextCommand() : base(0x12, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        if (!buffer.HasBytes(2)) return;
        byte type = buffer.ReadByte();
        string command = buffer.ReadAsciiNull();
        state.OnTextCommand(type, command);
    }
}

/// <summary>0x3A — Skill lock change.</summary>
public sealed class PacketSkillLock : PacketHandler
{
    public PacketSkillLock() : base(0x3A, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        if (!buffer.HasBytes(3)) return;
        ushort skillId = buffer.ReadUInt16();
        byte lockState = buffer.ReadByte();
        state.SkillLockHandler?.Invoke(state, skillId, lockState);
    }
}

/// <summary>0x9B — Help request (client → server). Client presses the help button.</summary>
public sealed class PacketHelpRequest : PacketHandler
{
    public PacketHelpRequest() : base(0x9B, 258) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        state.OnHelpRequest();
    }
}

/// <summary>0x22 — Resync request (client → server). Client sends this when desynced.</summary>
public sealed class PacketResyncRequest : PacketHandler
{
    public PacketResyncRequest() : base(0x22, 3) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        buffer.ReadByte(); // seq
        buffer.ReadByte(); // notoriety (ignored from client)
        state.OnResyncRequest();
    }
}

/// <summary>0xD1 — Logout request (client → server). Sent when the player clicks
/// "Return to character select" in the paperdoll. Server must reply with an
/// accept (0xD1 + 0x01) so the client actually leaves the world.</summary>
public sealed class PacketLogoutRequest : PacketHandler
{
    public PacketLogoutRequest() : base(0xD1, 2) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        buffer.ReadByte(); // 0x00 from client (request)
        state.OnLogoutRequest();
    }
}
