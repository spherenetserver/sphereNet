using SphereNet.Core.Types;

namespace SphereNet.Game.Chat;

/// <summary>A chat conference channel and its current members.</summary>
public sealed class ChatChannel
{
    public required string Name { get; init; }
    /// <summary>Static channels survive emptying; ad-hoc ones are removed
    /// when the last member leaves.</summary>
    public bool IsStatic { get; init; }
    private readonly List<Serial> _members = [];
    public IReadOnlyList<Serial> Members => _members;

    internal bool Add(Serial uid)
    {
        if (_members.Contains(uid))
            return false;
        _members.Add(uid);
        return true;
    }

    internal bool Remove(Serial uid) => _members.Remove(uid);
}

/// <summary>
/// UO chat (conference) system state: channels, membership, talk routing.
/// Maps to the OSI chat system the client drives with 0xB3/0xB5 and renders
/// from 0xB2. Packet work stays in GameClient; this class is pure state.
/// </summary>
public sealed class ChatEngine
{
    private readonly Dictionary<string, ChatChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Serial, ChatChannel> _memberChannel = [];
    /// <summary>Per-character chat display name ("chat handle").</summary>
    private readonly Dictionary<Serial, string> _chatNames = [];

    public ChatEngine(params string[] staticChannels)
    {
        foreach (var name in staticChannels.Length > 0 ? staticChannels : ["General"])
            _channels[name] = new ChatChannel { Name = name, IsStatic = true };
    }

    public IReadOnlyCollection<ChatChannel> Channels => _channels.Values;

    public ChatChannel? GetChannel(string name) => _channels.GetValueOrDefault(name);

    public ChatChannel? GetMemberChannel(Serial uid) => _memberChannel.GetValueOrDefault(uid);

    public string GetChatName(Serial uid) => _chatNames.GetValueOrDefault(uid, "");

    public void SetChatName(Serial uid, string name) => _chatNames[uid] = name;

    /// <summary>Join (creating an ad-hoc channel when missing). A member can
    /// be in one channel at a time — joining leaves the previous one.
    /// Returns the channel, or null when the name is empty.</summary>
    public ChatChannel? Join(Serial uid, string channelName)
    {
        channelName = channelName.Trim();
        if (channelName.Length == 0)
            return null;

        Leave(uid);
        if (!_channels.TryGetValue(channelName, out var channel))
        {
            channel = new ChatChannel { Name = channelName };
            _channels[channelName] = channel;
        }
        channel.Add(uid);
        _memberChannel[uid] = channel;
        return channel;
    }

    /// <summary>Leave the current channel. Empty ad-hoc channels are removed.
    /// Returns the channel that was left, or null.</summary>
    public ChatChannel? Leave(Serial uid)
    {
        if (!_memberChannel.TryGetValue(uid, out var channel))
            return null;
        _memberChannel.Remove(uid);
        channel.Remove(uid);
        if (!channel.IsStatic && channel.Members.Count == 0)
            _channels.Remove(channel.Name);
        return channel;
    }
}
