using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Chat;
using SphereNet.Game.Definitions;
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
            Send(PacketChatSystem.MakeCreateChannel(channel.Name, channel.HasPassword));
    }

    /// <summary>0xB3 chat actions. Beyond talk/join/create/leave this handles the
    /// conference moderation set: rename, password, kick, moderator/voice grants,
    /// default-voice toggles and emote.</summary>
    public void HandleChatAction(ushort cmd, string text)
    {
        if (_character == null || _chatEngine == null)
            return;

        switch (cmd)
        {
            case 0x62: // join an existing channel: "Name" password
            {
                var (name, password) = ParseJoinCommand(text);
                ChatJoinChannel(name, password, create: false);
                break;
            }
            case 0x63: // create a new channel: Name{password}
            {
                var (name, password) = ParseCreateCommand(text);
                ChatJoinChannel(name, password, create: true);
                break;
            }
            case 0x43: // leave current channel
            case 0x58: // leave chat entirely (OSI alias)
                ChatLeaveChannel();
                break;
            case 0x61: // talk to the current channel
                ChatTalk(text, emote: false);
                break;
            case 0x7A: // emote to the current channel
                ChatTalk(text, emote: true);
                break;
            case 0x65: // private message: "<recipient> <text>"
                ChatPrivateMessage(text);
                break;
            case 0x66: // add to ignore list
                ChatSetIgnore(text, ChatIgnoreAction.Add);
                break;
            case 0x67: // remove from ignore list
                ChatSetIgnore(text, ChatIgnoreAction.Remove);
                break;
            case 0x68: // toggle ignore
                ChatSetIgnore(text, ChatIgnoreAction.Toggle);
                break;
            case 0x64: // rename the current channel (moderator)
                ChatRename(ParseCreateCommand(text).Name);
                break;
            case 0x41: // change channel password (moderator)
                _chatEngine.SetPassword(_character.Uid, ParseCreateCommand(text).Password ?? text.Trim());
                break;
            case 0x76: // kick a member (moderator)
                ChatModerateTarget(text, ChatModAction.Kick);
                break;
            case 0x6C: // add moderator
                ChatModerateTarget(text, ChatModAction.AddModerator);
                break;
            case 0x6D: // remove moderator
                ChatModerateTarget(text, ChatModAction.RemoveModerator);
                break;
            case 0x6E: // toggle moderator
                ChatModerateTarget(text, ChatModAction.ToggleModerator);
                break;
            case 0x69: // grant voice
                ChatModerateTarget(text, ChatModAction.AddVoice);
                break;
            case 0x6A: // revoke voice
                ChatModerateTarget(text, ChatModAction.RemoveVoice);
                break;
            case 0x6B: // toggle voice
                ChatModerateTarget(text, ChatModAction.ToggleVoice);
                break;
            case 0x77: // enable default voice (everyone may talk)
                _chatEngine.SetDefaultVoice(_character.Uid, true);
                break;
            case 0x78: // disable default voice (only moderators/voiced may talk)
                _chatEngine.SetDefaultVoice(_character.Uid, false);
                break;
            case 0x79: // toggle default voice
                ChatToggleDefaultVoice();
                break;

            // The chat window's own privacy buttons. Source-X keeps both switches
            // on the member and both default ON (CChatChanMember.cpp:8); the
            // client sends these as their own actions and we were dropping all
            // seven on the floor, so the buttons did nothing at all.
            case 0x6F: // +receive — accept private messages
                _chatEngine?.SetReceivingPrivate(_character!.Uid, true);
                break;
            case 0x70: // -receive — refuse private messages
                _chatEngine?.SetReceivingPrivate(_character!.Uid, false);
                break;
            case 0x71: // /receive — flip it
                _chatEngine?.ToggleReceivingPrivate(_character!.Uid);
                break;
            case 0x72: // +showname — let /whois give my name
                _chatEngine?.SetShowCharacterName(_character!.Uid, true);
                break;
            case 0x73: // -showname — stay anonymous
                _chatEngine?.SetShowCharacterName(_character!.Uid, false);
                break;
            case 0x74: // /showname — flip it
                _chatEngine?.ToggleShowCharacterName(_character!.Uid);
                break;
            case 0x75: // /whois <name>
                ChatWhoIs(text);
                break;
        }
    }

    /// <summary>0x75 — who is behind a chat handle. Source-X answers with the
    /// character name only when that member allows it, and says they are anonymous
    /// otherwise (CChatChannel::WhoIs, CChatChannel.cpp:43-61). Asking from outside
    /// a channel is not answered at all, as with every other channel command.</summary>
    private void ChatWhoIs(string targetName)
    {
        if (_character == null || _chatEngine == null)
            return;
        if (_chatEngine.GetMemberChannel(_character.Uid) == null)
            return;

        targetName = targetName.Trim();
        if (targetName.Length == 0)
            return;

        var target = _chatEngine.FindByChatName(targetName);
        if (!target.IsValid)
        {
            Send(PacketChatSystem.MakeChannelMessage("SYSTEM",
                $"There is no player named '{targetName}'."));
            return;
        }

        if (!_chatEngine.ShowsCharacterName(target))
        {
            Send(PacketChatSystem.MakeChannelMessage("SYSTEM",
                $"{targetName} is remaining anonymous."));
            return;
        }

        var targetChar = _world.FindChar(target);
        Send(PacketChatSystem.MakeChannelMessage("SYSTEM",
            $"{targetName} is known in the world as {targetChar?.Name ?? targetName}."));
    }

    /// <summary>Drop chat membership on disconnect so channels don't hold
    /// stale entries.</summary>
    public void ChatOnDisconnect()
    {
        if (_character != null)
        {
            ChatLeaveChannel();
            _chatEngine?.ForgetPrivacy(_character.Uid);
        }
    }

    private void ChatJoinChannel(string channelName, string? password, bool create)
    {
        if (_character == null || _chatEngine == null || channelName.Length == 0)
            return;

        // Source-X checks the destination FIRST and only then leaves the old channel
        // (JoinChannel, CChat.cpp:70). Announcing the departure up front meant a wrong
        // password left the speaker in no channel at all - and took a one-member
        // channel, with its password and moderators, down with it.
        var previous = _chatEngine.GetMemberChannel(_character.Uid);
        var channel = _chatEngine.Join(_character.Uid, channelName, password, create);
        if (channel == null)
            return; // no such channel, name taken, or wrong password

        if (previous != null && previous != channel)
        {
            string leavingName = _chatEngine.GetChatName(_character.Uid);
            Send(PacketChatSystem.MakeLeftChannel(previous.Name));
            foreach (var memberUid in previous.Members)
                SendToChar?.Invoke(memberUid, PacketChatSystem.MakeRemoveUser(leavingName));
        }

        // A channel that has just come into being, or one that has just gone, changes
        // the list every open chat window is showing (BroadcastAddChannel,
        // CChat.cpp:48).
        if (create)
            AnnounceToChat(PacketChatSystem.MakeCreateChannel(channel.Name, channel.HasPassword));
        if (previous != null && previous != channel && !_chatEngine.Exists(previous.Name))
            AnnounceToChat(PacketChatSystem.MakeRemoveChannel(previous.Name));

        string myName = _chatEngine.GetChatName(_character.Uid);

        Send(PacketChatSystem.MakeJoinedChannel(channel.Name));
        Send(PacketChatSystem.MakeClearUsers());
        foreach (var memberUid in channel.Members)
        {
            string memberName = _chatEngine.GetChatName(memberUid);
            if (memberName.Length == 0)
                continue;
            Send(PacketChatSystem.MakeAddUser(memberName, channel.UserType(memberUid)));
            if (memberUid != _character.Uid)
                SendToChar?.Invoke(memberUid, PacketChatSystem.MakeAddUser(myName, channel.UserType(_character.Uid)));
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

    private void ChatTalk(string text, bool emote)
    {
        if (_character == null || _chatEngine == null || string.IsNullOrWhiteSpace(text))
            return;
        var channel = _chatEngine.GetMemberChannel(_character.Uid);
        if (channel == null || !channel.CanSpeak(_character.Uid))
            return; // not in a channel, or no voice in a moderated channel
        string myName = _chatEngine.GetChatName(_character.Uid);
        if (text.Length > 256)
            text = text[..256];

        foreach (var memberUid in channel.Members)
        {
            if (memberUid == _character.Uid)
            {
                Send(PacketChatSystem.MakeChannelMessage(myName, text, emote));
            }
            else if (!_chatEngine.IsIgnoring(memberUid, _character.Uid))
            {
                SendToChar?.Invoke(memberUid, PacketChatSystem.MakeChannelMessage(myName, text, emote));
            }
        }
    }

    private enum ChatModAction { Kick, AddModerator, RemoveModerator, ToggleModerator, AddVoice, RemoveVoice, ToggleVoice }

    private void ChatModerateTarget(string targetName, ChatModAction action)
    {
        if (_character == null || _chatEngine == null)
            return;
        var target = _chatEngine.FindByChatName(ParseCreateCommand(targetName).Name);
        if (!target.IsValid)
            return;

        // The actor's own channel decides the current role state for the toggles.
        var actorChannel = _chatEngine.GetMemberChannel(_character.Uid);

        switch (action)
        {
            case ChatModAction.Kick:
            {
                var channel = _chatEngine.Kick(_character.Uid, target);
                if (channel == null) return;
                string kickedName = _chatEngine.GetChatName(target);
                // Tell the kicked client it left, and every remaining member it's gone.
                SendToChar?.Invoke(target, PacketChatSystem.MakeLeftChannel(channel.Name));
                foreach (var m in channel.Members)
                    SendToChar?.Invoke(m, PacketChatSystem.MakeRemoveUser(kickedName));
                break;
            }
            case ChatModAction.AddModerator:
            case ChatModAction.RemoveModerator:
            case ChatModAction.ToggleModerator:
            {
                bool on = action == ChatModAction.ToggleModerator
                    ? actorChannel == null || !actorChannel.IsModerator(target)
                    : action == ChatModAction.AddModerator;
                if (_chatEngine.SetModerator(_character.Uid, target, on))
                    ChatRefreshUserType(target);
                break;
            }
            case ChatModAction.AddVoice:
            case ChatModAction.RemoveVoice:
            case ChatModAction.ToggleVoice:
            {
                bool on = action == ChatModAction.ToggleVoice
                    ? actorChannel == null || !actorChannel.IsVoiced(target)
                    : action == ChatModAction.AddVoice;
                if (_chatEngine.SetVoice(_character.Uid, target, on))
                    ChatRefreshUserType(target);
                break;
            }
        }
    }

    private void ChatToggleDefaultVoice()
    {
        if (_character == null || _chatEngine == null)
            return;
        var channel = _chatEngine.GetMemberChannel(_character.Uid);
        if (channel != null)
            _chatEngine.SetDefaultVoice(_character.Uid, !channel.DefaultVoice);
    }

    private enum ChatIgnoreAction { Add, Remove, Toggle }

    /// <summary>0x66/0x67/0x68 — manage the player's ignore list by chat handle.</summary>
    private void ChatSetIgnore(string targetName, ChatIgnoreAction action)
    {
        if (_character == null || _chatEngine == null)
            return;
        var target = _chatEngine.FindByChatName(ParseCreateCommand(targetName).Name);
        if (!target.IsValid || target == _character.Uid)
            return;
        switch (action)
        {
            case ChatIgnoreAction.Add: _chatEngine.SetIgnored(_character.Uid, target, true); break;
            case ChatIgnoreAction.Remove: _chatEngine.SetIgnored(_character.Uid, target, false); break;
            case ChatIgnoreAction.Toggle: _chatEngine.ToggleIgnored(_character.Uid, target); break;
        }
    }

    /// <summary>0x65 — a private message. The payload is the recipient's chat handle,
    /// a space, then the text. Delivery is suppressed when the recipient is ignoring
    /// the sender; the sender sees their own copy regardless.</summary>
    private void ChatPrivateMessage(string raw)
    {
        if (_character == null || _chatEngine == null)
            return;
        string s = raw.Trim();
        int space = s.IndexOf(' ');
        if (space <= 0)
            return; // need both a recipient and a message
        string targetName = s[..space].Trim();
        string text = s[(space + 1)..].Trim();
        if (text.Length == 0)
            return;
        if (text.Length > 256)
            text = text[..256];

        var target = _chatEngine.FindByChatName(targetName);
        if (!target.IsValid || target == _character.Uid)
            return;

        // Source-X refuses the whole message when the recipient has switched
        // private messages off, and tells the sender so (CChatChannel.cpp:110) —
        // it is not merely suppressed at delivery like an ignore.
        if (!_chatEngine.IsReceivingPrivate(target))
        {
            Send(PacketChatSystem.MakeChannelMessage("SYSTEM",
                $"{targetName} is not receiving private messages."));
            return;
        }

        string myName = _chatEngine.GetChatName(_character.Uid);
        // The sender always sees what they sent.
        Send(PacketChatSystem.MakeChannelMessage(myName, text));
        // The recipient receives it unless they have ignored the sender.
        if (!_chatEngine.IsIgnoring(target, _character.Uid))
            SendToChar?.Invoke(target, PacketChatSystem.MakeChannelMessage(myName, text));
    }

    /// <summary>Re-send a member's AddUser to the channel so clients update the
    /// moderator/voice marker beside the name.</summary>
    private void ChatRefreshUserType(Serial target)
    {
        if (_chatEngine == null) return;
        var channel = _chatEngine.GetMemberChannel(target);
        if (channel == null) return;
        string name = _chatEngine.GetChatName(target);
        if (name.Length == 0) return;
        var pkt = PacketChatSystem.MakeAddUser(name, channel.UserType(target));
        foreach (var m in channel.Members)
            SendToChar?.Invoke(m, pkt);
    }

    private void ChatRename(string newName)
    {
        if (_character == null || _chatEngine == null || newName.Length == 0)
            return;
        var channel = _chatEngine.Rename(_character.Uid, newName, out string oldName);
        if (channel == null)
            return;
        // The client tracks channels by name: re-advertise as remove-then-create.
        foreach (var m in channel.Members)
        {
            SendToChar?.Invoke(m, PacketChatSystem.MakeJoinedChannel(channel.Name));
        }
        // And everyone ELSE has the old name in their channel list (RenameChannel does
        // a global remove/add, CChatChannel.cpp:138).
        AnnounceToChat(PacketChatSystem.MakeRemoveChannel(oldName));
        AnnounceToChat(PacketChatSystem.MakeCreateChannel(channel.Name, channel.HasPassword));
    }

    /// <summary>Tell every chat participant about a channel-list change. Source-X
    /// announces a create, a removal and a rename to all of them, not only to the
    /// members of the channel in question (CChat.cpp:48; RenameChannel,
    /// CChatChannel.cpp:138).</summary>
    private void AnnounceToChat(SphereNet.Network.Packets.Outgoing.PacketChatSystem packet)
    {
        if (_chatEngine == null || SendToChar == null)
            return;
        foreach (var participant in _chatEngine.Participants.ToArray())
            SendToChar(participant, packet);
    }

    /// <summary>The JOIN command's shape: <c>"Name" password</c> - the name inside the
    /// quotes, the password after them (Source-X CChat.cpp:169). The old parser threw
    /// the tail away and then looked for braces in what was left, so the standard client
    /// could never join a password-protected channel with the right password.</summary>
    private static (string Name, string? Password) ParseJoinCommand(string raw)
    {
        string s = raw.Trim();
        int firstQuote = s.IndexOf('"');
        if (firstQuote < 0)
            return ParseCreateCommand(s);   // an unquoted name, brace form and all

        int secondQuote = s.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0)
            return (s[(firstQuote + 1)..].Trim(), null);

        string name = s[(firstQuote + 1)..secondQuote].Trim();
        string tail = s[(secondQuote + 1)..].Trim();
        return (name, string.IsNullOrEmpty(tail) ? null : tail);
    }

    /// <summary>The CREATE command's shape: <c>Name{password}</c>.</summary>
    private static (string Name, string? Password) ParseCreateCommand(string raw)
    {
        string s = raw.Trim();
        int firstQuote = s.IndexOf('"');
        if (firstQuote >= 0)
        {
            int secondQuote = s.IndexOf('"', firstQuote + 1);
            s = secondQuote > firstQuote
                ? s[(firstQuote + 1)..secondQuote]
                : s[(firstQuote + 1)..];
        }
        string? password = null;
        int brace = s.IndexOf('{');
        if (brace >= 0)
        {
            int closeBrace = s.IndexOf('}', brace + 1);
            password = closeBrace > brace ? s[(brace + 1)..closeBrace] : s[(brace + 1)..];
            s = s[..brace];
        }
        return (s.Trim(), string.IsNullOrEmpty(password) ? null : password);
    }

    // ==================== Tips (0xA7 -> 0xA6) ====================

    /// <summary>Source-X CClient::Event_Tips (CClientEvent.cpp:140): tip 0 means tip 1;
    /// a missing [TIP n] falls back to [TIP 1], and nothing is sent when that is missing
    /// too (or n was 1 already). The section lines go out as a type-0 (TIPS) scroll whose
    /// context is n + 1 - the number the client sends back when paging.</summary>
    public void HandleTipRequest(ushort tip)
    {
        if (tip == 0)
            tip = 1;
        var resources = _commands?.Resources ?? DefinitionLoader.StaticResources;
        if (resources == null)
            return;

        var link = resources.GetResource(ResType.Tip, tip);
        if (link == null)
        {
            if (tip == 1)
                return;
            link = resources.GetResource(ResType.Tip, 1);
            if (link == null)
                return;
            tip = 1;
        }

        // PacketOpenScroll (send.cpp:3244) writes every section line followed by CR.
        var sb = new System.Text.StringBuilder();
        if (link.StoredKeys != null)
        {
            foreach (var key in link.StoredKeys)
                sb.Append(key.RawLine).Append('\r');
        }
        Send(new PacketOpenScroll(0, (uint)tip + 1, sb.ToString()));
    }

    // ==================== Global chat (0xF9) ====================

    /// <summary>Source-X CHATF_GLOBALCHAT: the global chat system is on.</summary>
    public const int ChatFlagGlobalChat = 0x10;

    /// <summary>Source-X MINCLIVER_GLOBALCHAT (7.0.62.2) in this server's version
    /// number format.</summary>
    public const uint MinClientVersionGlobalChat = 70_062_002;

    /// <summary>Source-X CGlobalChatChanMember: the Jabber id set at connect and the
    /// online/offline status the client toggles.</summary>
    internal string GlobalChatJid { get; private set; } = "";
    internal bool GlobalChatVisible { get; private set; }

    private bool CanSendGlobalChat =>
        _character != null && _netState.ClientVersionNumber >= MinClientVersionGlobalChat;

    /// <summary>0xF9 — Source-X PacketGlobalChatReq::onReceive (receive.cpp:4711).
    /// Upstream marks the system INCOMPLETE: sending a message and removing a friend
    /// do nothing, the friend-add target has no response handler
    /// (CClient::Event_Target has no CLIMODE_TARG_GLOBALCHAT_ADD case), and only the
    /// status toggle does real work.</summary>
    public void HandleGlobalChat(byte action, string xml)
    {
        if ((ServerChatFlags & ChatFlagGlobalChat) == 0)
        {
            SysMessage("Global Chat is currently unavailable.");
            return;
        }

        switch (action)
        {
            case PacketGlobalChatOut.ActionMessageSend:
            case PacketGlobalChatOut.ActionFriendRemove:
                return;
            case PacketGlobalChatOut.ActionFriendAddTarg:
                // addTarget(CLIMODE_TARG_GLOBALCHAT_ADD, prompt): the prompt as a system
                // message, then an object cursor whose answer upstream ignores.
                SysMessage("Target player to request as Global Chat friend.");
                SetPendingTarget(static (_, _, _, _, _) => { }, 0);
                return;
            case PacketGlobalChatOut.ActionStatusToggle:
                SendGlobalChatStatusToggle();
                return;
            default:
                _logger.LogDebug("Unknown global chat action 0x{Action:X2}", action);
                return;
        }
    }

    /// <summary>Source-X CClient::addGlobalChatConnect (CClientMsg.cpp:2606), sent at
    /// login when CHATF_GLOBALCHAT is on.</summary>
    internal void SendGlobalChatConnect()
    {
        if (!CanSendGlobalChat)
            return;
        // Jabber id: CharName_CharUID@ServerID ("%.6s_%.7u@%.2hhu").
        GlobalChatJid = FormatGlobalChatJid(_character!.Name ?? "", _character.Uid.Value);
        string xml = $"<iq to=\"{GlobalChatJid}\" id=\"iq_{GlobalChatStamp()}\" type=\"6\" version=\"1\" jid=\"{GlobalChatJid}\" />";
        GlobalChatVisible = false;
        Send(new PacketGlobalChatOut(0, PacketGlobalChatOut.ActionConnect, PacketGlobalChatOut.StanzaInfoQuery, xml));
        SysMessage("Global Chat is now connected.");
    }

    /// <summary>Source-X CClient::addGlobalChatStatusToggle (CClientMsg.cpp:2628).</summary>
    internal void SendGlobalChatStatusToggle()
    {
        if (!CanSendGlobalChat)
            return;
        int show = GlobalChatVisible ? 0 : 1;
        string name = _character!.Name ?? "";
        if (name.Length > 6)
            name = name[..6];
        string xml = $"<presence from=\"{GlobalChatJid}\" id=\"pres_{GlobalChatStamp()}\" name=\"{name}\" show=\"{show}\" version=\"1\" />";
        GlobalChatVisible = show != 0;
        Send(new PacketGlobalChatOut(0, PacketGlobalChatOut.ActionConnect, PacketGlobalChatOut.StanzaPresence, xml));
        SysMessage(show != 0 ? "Global Chat Online" : "Global Chat Offline");
    }

    internal static string FormatGlobalChatJid(string charName, uint uid)
    {
        string name = charName.Length > 6 ? charName[..6] : charName;
        return $"{name}_{uid:D7}@00";
    }

    /// <summary>CSTime::GetCurrentTime().GetTime() printed with "%.10u".</summary>
    private static string GlobalChatStamp() =>
        ((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString("D10");
}
