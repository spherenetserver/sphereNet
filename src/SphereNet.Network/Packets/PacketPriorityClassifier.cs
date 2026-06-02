namespace SphereNet.Network.Packets;

/// <summary>
/// Maps an outgoing packet's opcode to its send <see cref="PacketPriority"/>,
/// mirroring the Source-X priority model. Unlisted opcodes default to
/// <see cref="PacketPriority.Normal"/> (visible-world / gameplay state).
///
/// Classification is by top-level opcode only; multiplexed opcodes (0xBF, 0xD7)
/// keep the default unless a specific subcommand ever needs promoting.
/// </summary>
public static class PacketPriorityClassifier
{
    private static readonly PacketPriority[] s_table = BuildTable();

    public static PacketPriority Classify(byte opcode) => s_table[opcode];

    private static PacketPriority[] BuildTable()
    {
        var t = new PacketPriority[256];
        for (int i = 0; i < t.Length; i++)
            t[i] = PacketPriority.Normal; // default: visible-world / gameplay state

        // --- Highest: latency-critical movement + auth control ---
        t[0x21] = PacketPriority.Highest; // movement reject
        t[0x22] = PacketPriority.Highest; // movement ack
        t[0x82] = PacketPriority.Highest; // login denied

        // --- High: login/stream control + movement-affecting control ---
        t[0x1B] = PacketPriority.High;    // login confirm / player start
        t[0x8C] = PacketPriority.High;    // relay server
        t[0xA8] = PacketPriority.High;    // server list
        t[0xA9] = PacketPriority.High;    // character list
        t[0x27] = PacketPriority.High;    // pickup/drag cancel
        t[0xE3] = PacketPriority.High;    // KR encryption
        t[0xF2] = PacketPriority.High;    // time sync response

        // --- Normal: explicit (already the default, listed for clarity) ---
        // 0x1A/0xF3 world item, 0x1D remove, 0x20/0x77/0x78 mobile, 0x25/0x3C/0xF7
        // container, 0x6F secure trade, 0x1C/0xAE/0x54/0x70/0xC0/0xC7 speech/fx,
        // 0x0B damage, 0x2C death — all Normal by default.

        // --- Low: status, UI, animations, less latency-sensitive ---
        t[0x11] = PacketPriority.Low;     // status full
        t[0x16] = PacketPriority.Low;     // health bar (new)
        t[0x17] = PacketPriority.Low;     // health bar update
        t[0x3A] = PacketPriority.Low;     // skills
        t[0xB0] = PacketPriority.Low;     // gump
        t[0xDD] = PacketPriority.Low;     // compressed gump
        t[0x7C] = PacketPriority.Low;     // open menu
        t[0xD4] = PacketPriority.Low;     // book (new)
        t[0x66] = PacketPriority.Low;     // book page
        t[0x90] = PacketPriority.Low;     // map
        t[0xF5] = PacketPriority.Low;     // new map
        t[0x88] = PacketPriority.Low;     // open paperdoll
        t[0x74] = PacketPriority.Low;     // vendor buy list
        t[0x9E] = PacketPriority.Low;     // vendor sell list
        t[0x6C] = PacketPriority.Low;     // target cursor
        t[0x99] = PacketPriority.Low;     // target cursor (multi)
        t[0x6E] = PacketPriority.Low;     // animation
        t[0xE2] = PacketPriority.Low;     // new animation
        t[0x2F] = PacketPriority.Low;     // combat swing/fight occurring
        t[0xDF] = PacketPriority.Low;     // buff/debuff
        t[0x72] = PacketPriority.Low;     // war mode
        t[0xE5] = PacketPriority.Low;     // display waypoint
        t[0xE6] = PacketPriority.Low;     // remove waypoint
        t[0xF9] = PacketPriority.Low;     // global chat

        // --- Idle: ambience / delay-tolerant ---
        t[0x5B] = PacketPriority.Idle;    // game time
        t[0x65] = PacketPriority.Idle;    // weather
        t[0x6D] = PacketPriority.Idle;    // play music
        t[0xD6] = PacketPriority.Idle;    // mega-cliloc / tooltip property list
        t[0xD8] = PacketPriority.Idle;    // custom house design

        return t;
    }
}
