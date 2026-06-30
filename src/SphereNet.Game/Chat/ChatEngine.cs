using SphereNet.Core.Types;

namespace SphereNet.Game.Chat;

/// <summary>A chat conference channel: its members and moderation state
/// (owner, moderators, voiced members, password, default-voice).</summary>
public sealed class ChatChannel
{
    public required string Name { get; set; }
    /// <summary>Static channels survive emptying; ad-hoc ones are removed
    /// when the last member leaves.</summary>
    public bool IsStatic { get; init; }
    /// <summary>The member who created the channel — always a moderator.</summary>
    public Serial Owner { get; set; } = Serial.Invalid;
    /// <summary>Channel password (null/empty = open). Joining requires a match.</summary>
    public string? Password { get; set; }
    /// <summary>When true (default) every member may talk; when false only
    /// moderators and explicitly voiced members may.</summary>
    public bool DefaultVoice { get; set; } = true;

    private readonly List<Serial> _members = [];
    private readonly HashSet<Serial> _moderators = [];
    private readonly HashSet<Serial> _voiced = [];

    public IReadOnlyList<Serial> Members => _members;
    public bool HasPassword => !string.IsNullOrEmpty(Password);

    public bool IsModerator(Serial uid) => uid == Owner || _moderators.Contains(uid);
    public bool IsVoiced(Serial uid) => _voiced.Contains(uid);

    /// <summary>Whether a member may talk in this channel: moderators always; any
    /// member when default-voice is on; otherwise only explicitly voiced members.</summary>
    public bool CanSpeak(Serial uid) => IsModerator(uid) || DefaultVoice || _voiced.Contains(uid);

    /// <summary>0xB2 AddUser userType for a member: 1 = moderator, 2 = voiced
    /// (when the channel is moderated), 0 = ordinary speaker.</summary>
    public ushort UserType(Serial uid)
    {
        if (IsModerator(uid)) return 1;
        if (!DefaultVoice && _voiced.Contains(uid)) return 2;
        return 0;
    }

    internal bool Add(Serial uid)
    {
        if (_members.Contains(uid))
            return false;
        _members.Add(uid);
        return true;
    }

    internal bool Remove(Serial uid)
    {
        _moderators.Remove(uid);
        _voiced.Remove(uid);
        return _members.Remove(uid);
    }

    internal void SetModerator(Serial uid, bool on)
    {
        if (on) _moderators.Add(uid); else _moderators.Remove(uid);
    }

    internal void SetVoice(Serial uid, bool on)
    {
        if (on) _voiced.Add(uid); else _voiced.Remove(uid);
    }
}

/// <summary>
/// UO chat (conference) system state: channels, membership, moderation and talk
/// routing. Maps to the OSI chat system the client drives with 0xB3/0xB5 and
/// renders from 0xB2. Packet work stays in GameClient; this class is pure state.
/// </summary>
public sealed class ChatEngine
{
    private readonly Dictionary<string, ChatChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Serial, ChatChannel> _memberChannel = [];
    /// <summary>Per-character chat display name ("chat handle").</summary>
    private readonly Dictionary<Serial, string> _chatNames = [];
    /// <summary>Per-character ignore set: characters whose private messages (and
    /// channel lines, at the client) the owner has chosen not to receive.</summary>
    private readonly Dictionary<Serial, HashSet<Serial>> _ignored = [];

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

    /// <summary>Resolve a chat handle back to its character uid (for whispers).</summary>
    public Serial FindByChatName(string name)
    {
        foreach (var kv in _chatNames)
            if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        return Serial.Invalid;
    }

    // ---- Ignore list (per character, channel-independent) ----

    /// <summary>Whether <paramref name="owner"/> is ignoring <paramref name="other"/>'s
    /// messages.</summary>
    public bool IsIgnoring(Serial owner, Serial other) =>
        _ignored.TryGetValue(owner, out var set) && set.Contains(other);

    /// <summary>Add/remove a character to the owner's ignore set. Returns true when the
    /// set actually changed. A character cannot ignore itself.</summary>
    public bool SetIgnored(Serial owner, Serial other, bool on)
    {
        if (!other.IsValid || other == owner)
            return false;
        if (on)
        {
            if (!_ignored.TryGetValue(owner, out var set))
                _ignored[owner] = set = [];
            return set.Add(other);
        }
        return _ignored.TryGetValue(owner, out var existing) && existing.Remove(other);
    }

    /// <summary>Flip the ignore state for <paramref name="other"/>. Returns the new
    /// state (true = now ignored).</summary>
    public bool ToggleIgnored(Serial owner, Serial other)
    {
        bool now = !IsIgnoring(owner, other);
        SetIgnored(owner, other, now);
        return now;
    }

    /// <summary>Join (creating an ad-hoc channel when missing). A member can be in
    /// one channel at a time — joining leaves the previous one. The creator becomes
    /// the channel owner; an existing password-protected channel rejects a join with
    /// a wrong (or missing) password. Returns the channel, or null on empty name /
    /// password mismatch.</summary>
    public ChatChannel? Join(Serial uid, string channelName, string? password = null)
    {
        channelName = channelName.Trim();
        if (channelName.Length == 0)
            return null;

        if (_channels.TryGetValue(channelName, out var existing))
        {
            if (existing.HasPassword && !string.Equals(existing.Password, password, StringComparison.Ordinal))
                return null; // wrong/missing password
        }

        Leave(uid);
        if (!_channels.TryGetValue(channelName, out var channel))
        {
            channel = new ChatChannel
            {
                Name = channelName,
                Owner = uid,
                Password = string.IsNullOrEmpty(password) ? null : password,
            };
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

    // ---- Moderation (the actor must be a moderator of the channel they are in) ----

    private ChatChannel? ModeratedChannel(Serial mod) =>
        GetMemberChannel(mod) is { } c && c.IsModerator(mod) ? c : null;

    /// <summary>Kick a member out of the actor's channel. Returns the kicked
    /// member's channel (so the caller can notify), or null if not permitted.</summary>
    public ChatChannel? Kick(Serial mod, Serial target)
    {
        var channel = ModeratedChannel(mod);
        if (channel == null || target == mod || !channel.Members.Contains(target))
            return null;
        // Leave operates on the target's own membership entry.
        Leave(target);
        return channel;
    }

    public bool SetModerator(Serial mod, Serial target, bool on)
    {
        var channel = ModeratedChannel(mod);
        if (channel == null || !channel.Members.Contains(target)) return false;
        channel.SetModerator(target, on);
        return true;
    }

    public bool SetVoice(Serial mod, Serial target, bool on)
    {
        var channel = ModeratedChannel(mod);
        if (channel == null || !channel.Members.Contains(target)) return false;
        channel.SetVoice(target, on);
        return true;
    }

    public bool SetDefaultVoice(Serial mod, bool on)
    {
        var channel = ModeratedChannel(mod);
        if (channel == null) return false;
        channel.DefaultVoice = on;
        return true;
    }

    public bool SetPassword(Serial mod, string? password)
    {
        var channel = ModeratedChannel(mod);
        if (channel == null) return false;
        channel.Password = string.IsNullOrEmpty(password) ? null : password;
        return true;
    }

    /// <summary>Rename the actor's channel. Returns the channel on success (re-keyed),
    /// null if not permitted or the new name is taken/empty.</summary>
    public ChatChannel? Rename(Serial mod, string newName)
    {
        newName = newName.Trim();
        var channel = ModeratedChannel(mod);
        if (channel == null || newName.Length == 0 || channel.IsStatic ||
            _channels.ContainsKey(newName))
            return null;
        _channels.Remove(channel.Name);
        channel.Name = newName;
        _channels[newName] = channel;
        return channel;
    }
}
