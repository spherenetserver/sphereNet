using SphereNet.Game.Chat;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>
    /// 0xB5 — the player opened the chat window. The character's own name is
    /// accepted as the chat handle (no separate username prompt) and the
    /// channel list is sent; the client auto-joins "General" on acceptance.
    /// </summary>
    public void HandleChatOpen()
    {
        if (_character == null || _chatEngine == null)
            return;
        string chatName = _character.Name ?? "Player";
        _chatEngine.SetChatName(_character.Uid, chatName);
        Send(PacketChatSystem.MakeUsernameAccepted(chatName));
        foreach (var channel in _chatEngine.Channels)
            Send(PacketChatSystem.MakeCreateChannel(channel.Name));
    }

    /// <summary>0xB3 chat actions: 0x61 talk, 0x62 join, 0x63 create, 0x43 leave.</summary>
    public void HandleChatAction(ushort cmd, string text)
    {
        if (_character == null || _chatEngine == null)
            return;

        switch (cmd)
        {
            case 0x62: // join — channel name arrives wrapped in '"' quotes
            case 0x63: // create channel (joins it too)
                ChatJoinChannel(CleanChannelName(text));
                break;
            case 0x43: // leave current channel
                ChatLeaveChannel();
                break;
            case 0x61: // talk to the current channel
                ChatTalk(text);
                break;
        }
    }

    /// <summary>Drop chat membership on disconnect so channels don't hold
    /// stale entries.</summary>
    public void ChatOnDisconnect()
    {
        if (_character != null)
            ChatLeaveChannel();
    }

    private void ChatJoinChannel(string channelName)
    {
        if (_character == null || _chatEngine == null || channelName.Length == 0)
            return;

        ChatLeaveChannel(); // notify the old channel before switching

        var channel = _chatEngine.Join(_character.Uid, channelName);
        if (channel == null)
            return;
        string myName = _chatEngine.GetChatName(_character.Uid);

        Send(PacketChatSystem.MakeJoinedChannel(channel.Name));
        Send(PacketChatSystem.MakeClearUsers());
        foreach (var memberUid in channel.Members)
        {
            string memberName = _chatEngine.GetChatName(memberUid);
            if (memberName.Length == 0)
                continue;
            Send(PacketChatSystem.MakeAddUser(memberName));
            if (memberUid != _character.Uid)
                SendToChar?.Invoke(memberUid, PacketChatSystem.MakeAddUser(myName));
        }
    }

    private void ChatLeaveChannel()
    {
        if (_character == null || _chatEngine == null)
            return;
        var left = _chatEngine.Leave(_character.Uid);
        if (left == null)
            return;
        string myName = _chatEngine.GetChatName(_character.Uid);
        Send(PacketChatSystem.MakeLeftChannel(left.Name));
        foreach (var memberUid in left.Members)
            SendToChar?.Invoke(memberUid, PacketChatSystem.MakeRemoveUser(myName));
    }

    private void ChatTalk(string text)
    {
        if (_character == null || _chatEngine == null || string.IsNullOrWhiteSpace(text))
            return;
        var channel = _chatEngine.GetMemberChannel(_character.Uid);
        if (channel == null)
            return;
        string myName = _chatEngine.GetChatName(_character.Uid);
        if (text.Length > 256)
            text = text[..256];

        foreach (var memberUid in channel.Members)
        {
            var pkt = PacketChatSystem.MakeChannelMessage(myName, text);
            if (memberUid == _character.Uid)
                Send(pkt);
            else
                SendToChar?.Invoke(memberUid, pkt);
        }
    }

    /// <summary>The 0x62 join payload wraps the channel name in '"' quote
    /// characters and may append a password — strip both.</summary>
    private static string CleanChannelName(string raw)
    {
        string name = raw.Trim();
        int firstQuote = name.IndexOf('"');
        if (firstQuote >= 0)
        {
            int secondQuote = name.IndexOf('"', firstQuote + 1);
            name = secondQuote > firstQuote
                ? name[(firstQuote + 1)..secondQuote]
                : name[(firstQuote + 1)..];
        }
        int brace = name.IndexOf('{');
        if (brace >= 0)
            name = name[..brace];
        return name.Trim();
    }
}
