namespace SphereNet.Network.Packets;

/// <summary>
/// The send priority of an outgoing packet, as Source-X assigns it.
///
/// Source-X gives most packets their priority only while USEPACKETPRIORITY is on
/// (<c>g_Cfg.m_fUsePacketPriorities ? PRI_x : PRI_NORMAL</c>, send.cpp); with it off,
/// they all share the Normal queue and leave in the order they were sent. A few carry
/// a fixed priority either way: login denied 0x82 and kick 0x26 (Highest), time sync
/// 0xF2 (High) and the property list 0xD6 (Idle).
///
/// The table follows send.cpp packet by packet. Packets that travel together share a
/// queue there - the vendor list 0x74/0x9E with the container open 0x24, a map 0x90/0xF5
/// with its 0x56 pins, a book 0xD4 with 0x24 - so a sequence keeps its order.
///
/// A shard may move any opcode to another priority with a [PACKETPRIORITY] section in
/// sphere.ini (<see cref="Configure"/>); unlisted opcodes keep the table.
/// </summary>
public static class PacketPriorityClassifier
{
    private static readonly PacketPriority[] s_default = BuildTable();
    private static readonly Dictionary<byte, PacketPriority> s_defaultExtended = BuildExtendedTable();

    private static PacketPriority[] s_table = (PacketPriority[])s_default.Clone();
    private static Dictionary<byte, PacketPriority> s_extended = new(s_defaultExtended);

    /// <summary>USEPACKETPRIORITY. Off (the Source-X default) sends everything but the
    /// fixed-priority packets through the Normal queue, in order.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>MAXQUEUESIZE: packets a priority queue holds before a new one is moved
    /// down a level (CNetworkOutput::QueuePacketTransaction). 0 = no limit.</summary>
    public static int MaxQueueSize { get; private set; } = 75;

    /// <summary>The opcode's priority while USEPACKETPRIORITY is on.</summary>
    public static PacketPriority Classify(byte opcode) => s_table[opcode];

    /// <summary>The queue a built packet goes to.</summary>
    public static PacketPriority Resolve(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0)
            return PacketPriority.Normal;
        byte opcode = packet[0];
        if (TryFixed(opcode, out var fixedPriority))
            return fixedPriority;
        if (!Enabled)
            return PacketPriority.Normal;
        // 0xBF: [0xBF][len16][sub16]
        if (opcode == 0xBF && packet.Length >= 5 && packet[3] == 0 &&
            s_extended.TryGetValue(packet[4], out var sub))
            return sub;
        return s_table[opcode];
    }

    /// <summary>A priority a caller asked for explicitly (movement ack/reject). Source-X
    /// grants it only while USEPACKETPRIORITY is on (send.cpp:757, :781).</summary>
    public static PacketPriority Effective(PacketPriority requested) =>
        Enabled ? requested : PacketPriority.Normal;

    private static bool TryFixed(byte opcode, out PacketPriority priority)
    {
        switch (opcode)
        {
            case 0x82: priority = PacketPriority.Highest; return true; // PacketLoginError (send.cpp:2581)
            case 0x26: priority = PacketPriority.Highest; return true; // PacketKick (:996)
            case 0xF2: priority = PacketPriority.High; return true;    // PacketTimeSyncResponse (:5223)
            case 0xD6: priority = PacketPriority.Idle; return true;    // PacketPropertyList (:4726)
            default: priority = PacketPriority.Normal; return false;
        }
    }

    /// <summary>Packets sent in sequence with another, where moving one to a different
    /// queue than its partners reorders what the client sees. An override of one is
    /// accepted but warned about.</summary>
    public static readonly IReadOnlyDictionary<byte, string> SequenceMembers = new Dictionary<byte, string>
    {
        [0x3C] = "container contents, before the vendor list 0x74/0x9E",
        [0x74] = "vendor buy list, sent with 0x3C and the container open 0x24",
        [0x9E] = "vendor sell list, sent with the container open 0x24",
        [0x24] = "container open, after the vendor list 0x74 or book content 0xD4",
        [0xD4] = "book content, before the container open 0x24",
        [0x90] = "map display, before its 0x56 pins",
        [0xF5] = "map display, before its 0x56 pins",
        [0x56] = "map pin, after the map display 0x90/0xF5",
    };

    /// <summary>Apply USEPACKETPRIORITY, MAXQUEUESIZE and the [PACKETPRIORITY]
    /// overrides. A key is an opcode (<c>0x54</c>) or an 0xBF subcommand
    /// (<c>0xBF.0x26</c>); a value is a priority name or its number (0 = Idle ..
    /// 4 = Highest). Bad lines are reported through <paramref name="warn"/>.</summary>
    public static void Configure(bool enabled, int maxQueueSize,
        IEnumerable<KeyValuePair<string, string>>? overrides, Action<string>? warn = null)
    {
        var table = (PacketPriority[])s_default.Clone();
        var extended = new Dictionary<byte, PacketPriority>(s_defaultExtended);
        foreach (var (key, value) in overrides ?? [])
        {
            if (!TryParsePriority(value, out var priority))
            {
                warn?.Invoke($"[PACKETPRIORITY] {key}={value}: unknown priority (Idle, Low, Normal, High, Highest or 0-4)");
                continue;
            }
            string k = key.Trim();
            if (k.StartsWith("0xBF.", StringComparison.OrdinalIgnoreCase) &&
                TryParseByte(k[5..], out byte sub))
            {
                extended[sub] = priority;
                continue;
            }
            if (!TryParseByte(k, out byte opcode))
            {
                warn?.Invoke($"[PACKETPRIORITY] {key}: not an opcode (0x54 or 0xBF.0x26)");
                continue;
            }
            if (TryFixed(opcode, out _))
                warn?.Invoke($"[PACKETPRIORITY] 0x{opcode:X2} has a fixed priority and is not affected");
            else if (SequenceMembers.TryGetValue(opcode, out string? why))
                warn?.Invoke($"[PACKETPRIORITY] 0x{opcode:X2} is part of a packet sequence ({why}); " +
                             "a different queue than its partners reorders it on the client");
            table[opcode] = priority;
        }
        s_table = table;
        s_extended = extended;
        Enabled = enabled;
        MaxQueueSize = Math.Max(0, maxQueueSize);
    }

    /// <summary>Back to the Source-X defaults (tests).</summary>
    public static void Reset() => Configure(false, 75, null);

    private static bool TryParsePriority(string value, out PacketPriority priority)
    {
        string v = value.Trim();
        if (int.TryParse(v, out int n) && n >= 0 && n < (int)PacketPriority.Count)
        {
            priority = (PacketPriority)n;
            return true;
        }
        return Enum.TryParse(v, ignoreCase: true, out priority) &&
               priority != PacketPriority.Count && Enum.IsDefined(priority);
    }

    private static bool TryParseByte(string text, out byte value)
    {
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return byte.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return byte.TryParse(t, out value);
    }

    /// <summary>Source-X send.cpp priorities while USEPACKETPRIORITY is on.</summary>
    private static PacketPriority[] BuildTable()
    {
        var t = new PacketPriority[256];
        for (int i = 0; i < t.Length; i++)
            t[i] = PacketPriority.Normal;

        // Highest
        t[0x21] = PacketPriority.Highest; // PacketMovementRej
        t[0x22] = PacketPriority.Highest; // PacketMovementAck

        // High
        t[0x1B] = PacketPriority.High;    // PacketPlayerStart
        t[0x27] = PacketPriority.High;    // PacketDragCancel
        t[0xE3] = PacketPriority.High;    // PacketKREncryption

        // Low
        foreach (byte op in new byte[]
                 {
                     0x11, // PacketObjectStatus
                     0x16, // PacketHealthBarUpdateNew
                     0x17, // PacketHealthBarUpdate
                     0x23, // PacketDragAnimation
                     0x24, // PacketContainerOpen
                     0x2F, // PacketSwing
                     0x3A, // PacketSkills
                     0x56, // PacketMapPlot
                     0x66, // PacketBookPageContent
                     0x6C, // PacketAddTarget
                     0x6E, // PacketAction
                     0x71, // PacketBulletinBoard
                     0x72, // PacketWarMode
                     0x74, // PacketVendorBuyList
                     0x76, // PacketZoneChange
                     0x7C, // PacketDisplayMenu
                     0x81, // PacketChangeCharacter
                     0x85, // PacketDeleteError
                     0x86, // PacketCharacterListUpdate
                     0x88, // PacketPaperdoll
                     0x8B, // PacketSignGump
                     0x90, // PacketDisplayMap
                     0x93, // PacketDisplayBook
                     0x95, // PacketShowDyeWindow
                     0x99, // PacketAddTarget (multi)
                     0x9A, // PacketAddPrompt
                     0x9E, // PacketVendorSellList
                     0xA1, // PacketHealthUpdate
                     0xA2, // PacketManaUpdate
                     0xA3, // PacketStaminaUpdate
                     0xA5, // PacketWebPage
                     0xA6, // PacketOpenScroll
                     0xA8, // PacketServerList
                     0xA9, // PacketCharacterList
                     0xAB, // PacketGumpValueInput
                     0xB0, // PacketGumpDialog
                     0xB2, // PacketChatMessage
                     0xB8, // PacketProfile
                     0xC2, // PacketAddPrompt (unicode)
                     0xD1, // PacketLogoutAck
                     0xD4, // PacketDisplayBookNew
                     0xDC, // PacketPropertyListVersion
                     0xDD, // PacketGumpDialog (compressed)
                     0xDF, // PacketBuff
                     0xE2, // PacketActionBasic
                     0xE5, // PacketWaypointAdd
                     0xE6, // PacketWaypointRemove
                     0xF5, // PacketDisplayMapNew
                     0xF9, // PacketGlobalChat
                 })
            t[op] = PacketPriority.Low;

        // Idle
        t[0x5B] = PacketPriority.Idle;    // PacketGameTime
        t[0x65] = PacketPriority.Idle;    // PacketWeather
        t[0x6D] = PacketPriority.Idle;    // PacketPlayMusic
        t[0x73] = PacketPriority.Idle;    // PacketPingAck
        t[0x8C] = PacketPriority.Idle;    // PacketServerRelay
        t[0x98] = PacketPriority.Idle;    // PacketAllNamesResponse
        t[0xB7] = PacketPriority.Idle;    // PacketTooltip
        t[0xD8] = PacketPriority.Idle;    // PacketHouseDesign

        return t;
    }

    /// <summary>0xBF subcommands with their own priority (PacketExtended, send.cpp).</summary>
    private static Dictionary<byte, PacketPriority> BuildExtendedTable() => new()
    {
        [0x04] = PacketPriority.Low,  // PacketGumpChange
        [0x10] = PacketPriority.Low,  // PacketPropertyListVersionOld
        [0x14] = PacketPriority.Low,  // PacketDisplayPopup
        [0x17] = PacketPriority.Low,  // PacketCodexOfWisdom
        [0x1D] = PacketPriority.Low,  // PacketHouseDesignVersion
        [0x26] = PacketPriority.High, // PacketSpeedMode
    };
}
