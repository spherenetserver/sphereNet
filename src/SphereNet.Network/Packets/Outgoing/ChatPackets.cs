namespace SphereNet.Network.Packets.Outgoing;

/// <summary>
/// 0xB2 — chat system message (server → client).
/// Layout (verified against the ClassicUO parser): [0xB2][len:2][cmd:2]
/// [4 reserved bytes] then the per-command payload; strings are
/// null-terminated big-endian unicode.
/// </summary>
public sealed class PacketChatSystem : PacketWriter
{
    public const ushort CreateChannel = 0x03E8;   // name + U16 ('1' = has password)
    public const ushort RemoveChannel = 0x03E9;   // name
    public const ushort UsernameAccepted = 0x03ED; // username (client then auto-joins "General")
    public const ushort AddUser = 0x03EE;          // U16 userType + username
    public const ushort RemoveUser = 0x03EF;       // username
    public const ushort ClearUsers = 0x03F0;       // (empty)
    public const ushort JoinedChannel = 0x03F1;    // channel name
    public const ushort LeftChannel = 0x03F4;      // channel name
    public const ushort ChannelMessage = 0x0025;   // U16 msgType + username + text
    public const ushort EmoteMessage = 0x0027;     // U16 msgType + username + text

    private readonly ushort _cmd;
    private readonly ushort? _code;     // leading U16 (user type / message type)
    private readonly string? _text1;    // first unicode string
    private readonly string? _text2;    // second unicode string
    private readonly ushort? _trailer;  // trailing U16 (CreateChannel password flag)

    private PacketChatSystem(ushort cmd, ushort? code, string? text1, string? text2, ushort? trailer)
        : base(0xB2)
    {
        _cmd = cmd;
        _code = code;
        _text1 = text1;
        _text2 = text2;
        _trailer = trailer;
    }

    public static PacketChatSystem MakeCreateChannel(string name, bool hasPassword = false) =>
        new(CreateChannel, null, name, null, hasPassword ? (ushort)'1' : (ushort)'0');

    public static PacketChatSystem MakeRemoveChannel(string name) =>
        new(RemoveChannel, null, name, null, null);

    public static PacketChatSystem MakeUsernameAccepted(string username) =>
        new(UsernameAccepted, null, username, null, null);

    public static PacketChatSystem MakeAddUser(string username, ushort userType = 0) =>
        new(AddUser, userType, username, null, null);

    public static PacketChatSystem MakeRemoveUser(string username) =>
        new(RemoveUser, null, username, null, null);

    public static PacketChatSystem MakeClearUsers() =>
        new(ClearUsers, null, null, null, null);

    public static PacketChatSystem MakeJoinedChannel(string name) =>
        new(JoinedChannel, null, name, null, null);

    public static PacketChatSystem MakeLeftChannel(string name) =>
        new(LeftChannel, null, name, null, null);

    public static PacketChatSystem MakeChannelMessage(string username, string text, bool emote = false) =>
        new(emote ? EmoteMessage : ChannelMessage, 0, username, text, null);

    public override PacketBuffer Build()
    {
        int size = 16 + ((_text1?.Length ?? 0) + (_text2?.Length ?? 0) + 2) * 2;
        var buf = CreateVariable(size);
        buf.WriteUInt16(_cmd);
        buf.WriteBytes(new byte[4]); // reserved/language — client skips
        if (_code != null)
            buf.WriteUInt16(_code.Value);
        if (_text1 != null)
            buf.WriteUnicodeNullBE(_text1);
        if (_text2 != null)
            buf.WriteUnicodeNullBE(_text2);
        if (_trailer != null)
            buf.WriteUInt16(_trailer.Value);
        buf.WriteLengthAt(1);
        return buf;
    }
}
