using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Messages;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using System.Collections.Concurrent;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using GameRegion = SphereNet.Game.World.Regions.Region;
using SphereNet.Game.World.Regions;
using SphereNet.Panel;
using SphereNet.Server.Admin;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;


namespace SphereNet.Server;

public static partial class Program
{
    private static void ShowRecordingDialog(Character gm)
    {
        if (!_clientsByCharUid.TryGetValue(gm.Uid, out var client)) return;

        bool isRecording = _recordingEngine.IsRecording(gm.Uid.Value);
        var recordings = _recordingEngine.ListRecordings();

        var gump = SphereNet.Game.Recording.RecordingDialog.Build(gm.Uid.Value, isRecording, recordings);
        client.SendGump(gump, (buttonId, _, _) =>
        {
            var action = SphereNet.Game.Recording.RecordingDialog.ParseResponse(buttonId);
            HandleRecordDialogAction(gm, action);
        });
    }

    private static void HandleRecordDialogAction(Character gm, SphereNet.Game.Recording.RecordDialogAction action)
    {
        switch (action.Type)
        {
            case SphereNet.Game.Recording.RecordActionType.StartRecord:
                _recordingEngine.StartRecording(gm);
                SendSysMessage(gm, "Recording started.");
                ShowRecordingDialog(gm);
                break;

            case SphereNet.Game.Recording.RecordActionType.StopRecord:
                var session = _recordingEngine.StopRecording(gm.Uid.Value);
                if (session != null)
                    SendSysMessage(gm, $"Recording saved: {session.Packets.Count} packets, {session.DurationMs / 1000.0:F1}s");
                ShowRecordingDialog(gm);
                break;

            case SphereNet.Game.Recording.RecordActionType.Play:
                StartReplayForPlayer(gm, action.SelectedIndex);
                break;

            case SphereNet.Game.Recording.RecordActionType.Delete:
                _recordingEngine.DeleteRecording(action.SelectedIndex);
                SendSysMessage(gm, "Recording deleted.");
                ShowRecordingDialog(gm);
                break;

            case SphereNet.Game.Recording.RecordActionType.Refresh:
                ShowRecordingDialog(gm);
                break;
        }
    }

    private static void StartReplayForPlayer(Character gm, int index)
    {
        if (_recordingEngine.IsReplaying(gm.Uid.Value))
        {
            SendSysMessage(gm, "Already replaying.");
            return;
        }
        var session = _recordingEngine.LoadRecording(index);
        if (session == null)
        {
            SendSysMessage(gm, "Recording not found.");
            return;
        }
        StartReplayForPlayer(gm, session);
    }

    private static void SendReplayOverlay(Character viewer)
    {
        uint uid = viewer.Uid.Value;
        var state = _recordingEngine.GetReplayState(uid);
        if (state == null) return;
        if (!_clientsByCharUid.TryGetValue(viewer.Uid, out var client)) return;

        int currentMs = _recordingEngine.GetElapsedMs(uid);
        var overlay = SphereNet.Game.Recording.RecordingDialog.BuildReplayOverlay(
            uid, state.Session.RecorderName, state.Session.DurationMs,
            currentMs, state.IsPaused, state.PlaybackSpeed);

        client.SendGump(overlay, (btnId, _, _) => HandleReplayControl(viewer, btnId));
    }

    private static void HandleReplayControl(Character viewer, uint btnId)
    {
        uint uid = viewer.Uid.Value;

        switch (btnId)
        {
            case 0:
            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnStop:
                FinishReplay(viewer);
                SendSysMessage(viewer, "Replay stopped.");
                return;

            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnPlayPause:
            {
                var st = _recordingEngine.GetReplayState(uid);
                if (st == null) return;
                if (st.IsPaused)
                    _recordingEngine.ResumeReplay(uid);
                else
                    _recordingEngine.PauseReplay(uid);
                break;
            }

            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnRewind:
            {
                int current = _recordingEngine.GetElapsedMs(uid);
                _recordingEngine.SeekReplay(uid, Math.Max(0, current - 10_000),
                    ReplaySendPacket, ReplayCameraUpdate);
                break;
            }

            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnForward:
            {
                int current = _recordingEngine.GetElapsedMs(uid);
                var st = _recordingEngine.GetReplayState(uid);
                if (st == null) return;
                _recordingEngine.SeekReplay(uid, Math.Min(st.Session.DurationMs, current + 10_000),
                    ReplaySendPacket, ReplayCameraUpdate);
                break;
            }

            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnSpeed1x:
                _recordingEngine.SetPlaybackSpeed(uid, 1f);
                break;
            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnSpeed2x:
                _recordingEngine.SetPlaybackSpeed(uid, 2f);
                break;
            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnSpeed4x:
                _recordingEngine.SetPlaybackSpeed(uid, 4f);
                break;

            default:
                return;
        }

        SendReplayOverlay(viewer);
    }

    // ----------------------------------------------------------------
    //  Player macro system (.MACRO)
    // ----------------------------------------------------------------

    private static SphereNet.Game.Objects.Items.Item? FindItemInBackpack(
        SphereNet.Game.Objects.Characters.Character ch, ushort dispId)
    {
        var pack = ch.Backpack;
        if (pack == null) return null;
        foreach (var item in pack.Contents)
        {
            if (item.DispIdFull == dispId) return item;
            if (item.Contents.Count > 0)
            {
                foreach (var sub in item.Contents)
                    if (sub.DispIdFull == dispId) return sub;
            }
        }
        return null;
    }

    private static void HandleMacroCommand(Character ch, string args)
    {
        if (_macroEngine == null)
        {
            SendSysMessage(ch, "Macro system is disabled.");
            return;
        }

        uint uid = ch.Uid.Value;
        string sub = args.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";

        switch (sub)
        {
            case "":
            case "REC":
            case "RECORD":
                if (_macroEngine.IsRecording(uid))
                {
                    var session = _macroEngine.StopRecording(uid);
                    if (session != null)
                        SendSysMessage(ch, $"Recording stopped: {session.Describe()}");
                    else
                        SendSysMessage(ch, "Recording stopped (empty, discarded).");
                }
                else
                {
                    if (_macroEngine.IsPlaying(uid))
                        _macroEngine.StopPlayback(uid);
                    _macroEngine.StartRecording(uid);
                    SendSysMessage(ch, "Macro recording started. Do your actions, then .MACRO STOP");
                }
                break;

            case "STOP":
                if (_macroEngine.IsRecording(uid))
                {
                    var session = _macroEngine.StopRecording(uid);
                    if (session != null)
                        SendSysMessage(ch, $"Recording saved: {session.Describe()}");
                    else
                        SendSysMessage(ch, "No actions recorded.");
                }
                else if (_macroEngine.IsPlaying(uid))
                {
                    _macroEngine.StopPlayback(uid);
                    SendSysMessage(ch, "Macro playback stopped.");
                }
                else
                    SendSysMessage(ch, "Nothing to stop.");
                break;

            case "PLAY":
                if (_macroEngine.StartPlayback(uid, loop: false))
                    SendSysMessage(ch, "Playing macro (single run)...");
                else
                    SendSysMessage(ch, "No recorded macro. Use .MACRO to record first.");
                break;

            case "LOOP":
                if (_macroEngine.StartPlayback(uid, loop: true))
                    SendSysMessage(ch, $"Looping macro (max {_config.MacroMaxLoopMinutes} min)...");
                else
                    SendSysMessage(ch, "No recorded macro. Use .MACRO to record first.");
                break;

            case "INFO":
                var rec = _macroEngine.GetRecording(uid);
                if (rec != null)
                {
                    SendSysMessage(ch, $"Recorded: {rec.Describe()}");
                    if (_macroEngine.IsPlaying(uid))
                        SendSysMessage(ch, "Status: playing");
                    else if (_macroEngine.IsRecording(uid))
                        SendSysMessage(ch, "Status: recording");
                }
                else
                    SendSysMessage(ch, "No macro recorded.");
                break;

            default:
                SendSysMessage(ch, "Usage: .MACRO [rec|stop|play|loop|info]");
                break;
        }
    }

    // ----------------------------------------------------------------
    //  State recording system (.SREC)
    // ----------------------------------------------------------------

    private static readonly Dictionary<uint, (uint TargetUid, int Page)> _stateRecBrowseState = [];
    private static readonly Dictionary<uint, List<(int Id, uint CharUid, string Label, long StartTs, long EndTs, string SharedBy)>> _stateRecSharedCache = [];

    private static void HandleStateRecordCommand(Character ch, string args)
    {
        if (_stateRecorder == null)
        {
            SendSysMessage(ch, "State recording is not available.");
            return;
        }

        args = args.Trim();

        if (args.Length == 0)
        {
            ShowStateRecBrowser(ch, 0);
            return;
        }

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string sub = parts[0].ToUpperInvariant();

        switch (sub)
        {
            case "PLAY" when parts.Length >= 2 && ch.PrivLevel >= PrivLevel.Admin:
            {
                string name = parts[1];
                int minutes = parts.Length >= 3 && int.TryParse(parts[2], out int m) ? m : 30;
                var uid = _stateRecorder.FindCharUidByName(name);
                if (uid == null) { SendSysMessage(ch, $"No state records for '{name}'."); return; }
                PlayStateRecording(ch, uid.Value, minutes);
                break;
            }

            case "PIN" when ch.PrivLevel >= PrivLevel.Admin:
            {
                int hoursAgo = parts.Length >= 2 && int.TryParse(parts[1], out int h) ? h : 0;
                int duration = parts.Length >= 3 && int.TryParse(parts[2], out int d) ? d : 1;
                string label = parts.Length >= 4 ? string.Join(' ', parts[3..]) : $"Pin {DateTime.UtcNow:MM-dd HH:mm}";
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long startTs = now - (long)hoursAgo * 3_600_000;
                long endTs = startTs + (long)duration * 3_600_000;
                _stateRecorder.PinPeriod(startTs, endTs, label, ch.Name ?? "Admin");
                SendSysMessage(ch, $"Period pinned: {label}");
                break;
            }

            case "SHARE" when parts.Length >= 2 && ch.PrivLevel >= PrivLevel.Admin:
            {
                string name = parts[1];
                int hoursAgo = parts.Length >= 3 && int.TryParse(parts[2], out int h) ? h : 0;
                int duration = parts.Length >= 4 && int.TryParse(parts[3], out int d) ? d : 1;
                string label = parts.Length >= 5 ? string.Join(' ', parts[4..]) : $"Shared {name}";
                var uid = _stateRecorder.FindCharUidByName(name);
                if (uid == null) { SendSysMessage(ch, $"No records for '{name}'."); return; }
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long startTs = now - (long)hoursAgo * 3_600_000;
                long endTs = startTs + (long)duration * 3_600_000;
                _stateRecorder.ShareView(uid.Value, startTs, endTs, label, ch.Name ?? "Admin");
                SendSysMessage(ch, $"Recording shared: {label}");
                break;
            }

            case "UNPIN" when parts.Length >= 2 && ch.PrivLevel >= PrivLevel.Admin:
            {
                if (int.TryParse(parts[1], out int pinId) && _stateRecorder.UnpinPeriod(pinId))
                    SendSysMessage(ch, $"Pin #{pinId} removed.");
                else
                    SendSysMessage(ch, "Invalid pin ID.");
                break;
            }

            case "UNSHARE" when parts.Length >= 2 && ch.PrivLevel >= PrivLevel.Admin:
            {
                if (int.TryParse(parts[1], out int shareId) && _stateRecorder.UnshareView(shareId))
                    SendSysMessage(ch, $"Share #{shareId} removed.");
                else
                    SendSysMessage(ch, "Invalid share ID.");
                break;
            }

            default:
                SendSysMessage(ch, "Usage: .srec | .srec play <name> [min] | .srec pin [h_ago] [dur] [label] | .srec share <name> [h_ago] [dur] [label]");
                break;
        }
    }

    private static void ShowStateRecBrowser(Character ch, int page, string? searchFilter = null)
    {
        if (_stateRecorder == null || !_clientsByCharUid.TryGetValue(ch.Uid, out var client)) return;

        if (ch.PrivLevel >= PrivLevel.Admin)
        {
            var chars = _stateRecorder.GetRecordedCharacters(searchFilter);
            long dbMb = _stateRecorder.GetDbSizeBytes() / (1024 * 1024);
            var displayList = new List<(uint Uid, string Name, bool IsPlayer, string LastSeen, int Records)>();
            foreach (var (uid, name, isPlayer, lastTs, records) in chars)
            {
                string lastSeen = DateTimeOffset.FromUnixTimeMilliseconds(lastTs).LocalDateTime.ToString("MM-dd HH:mm");
                displayList.Add((uid, name, isPlayer, lastSeen, records));
            }

            var gump = SphereNet.Game.Recording.StateRecordingDialog.BuildCharacterList(
                ch.Uid.Value, displayList, page, dbMb, searchFilter ?? "");
            _stateRecBrowseState[ch.Uid.Value] = (0, page);
            client.SendGump(gump, (btnId, _, textEntries) =>
            {
                string? searchText = null;
                foreach (var (id, text) in textEntries)
                {
                    if (id == SphereNet.Game.Recording.StateRecordingDialog.SearchEntryId)
                    { searchText = text; break; }
                }
                HandleStateRecGumpResponse(ch, btnId, displayList, null, searchText);
            });
        }
        else
        {
            ShowSharedRecordings(ch, page);
        }
    }

    private static void ShowSharedRecordings(Character ch, int page)
    {
        if (_stateRecorder == null || !_clientsByCharUid.TryGetValue(ch.Uid, out var client)) return;

        var shared = _stateRecorder.GetSharedViews();
        _stateRecSharedCache[ch.Uid.Value] = shared;

        var displayItems = new List<(int Id, string Label, string CharName, string TimeRange, string SharedBy)>();
        foreach (var (id, charUid, label, startTs, endTs, sharedBy) in shared)
        {
            string charName = "UID:" + charUid.ToString("X");
            var charObj = _world.FindChar(new Serial(charUid));
            if (charObj != null) charName = charObj.Name ?? charName;

            string timeRange = DateTimeOffset.FromUnixTimeMilliseconds(startTs).LocalDateTime.ToString("MM-dd HH:mm");
            displayItems.Add((id, label, charName, timeRange, sharedBy));
        }

        var gump = SphereNet.Game.Recording.StateRecordingDialog.BuildSharedList(
            ch.Uid.Value, displayItems, page);
        client.SendGump(gump, (btnId, _, _) => HandleSharedGumpResponse(ch, btnId, shared));
    }

    private static void ShowHourBuckets(Character ch, uint targetUid, string targetName, int page)
    {
        if (_stateRecorder == null || !_clientsByCharUid.TryGetValue(ch.Uid, out var client)) return;

        var buckets = _stateRecorder.GetHourBuckets(targetUid);
        var displayList = new List<(string HourKey, string Display, int Snapshots, int Moves)>();
        foreach (var (hourKey, startTs, snapCount, moveCount) in buckets)
        {
            string display = DateTimeOffset.FromUnixTimeMilliseconds(startTs).LocalDateTime.ToString("MM-dd HH:mm");
            displayList.Add((hourKey, display, snapCount, moveCount));
        }

        _stateRecBrowseState[ch.Uid.Value] = (targetUid, page);
        var gump = SphereNet.Game.Recording.StateRecordingDialog.BuildHourBuckets(
            ch.Uid.Value, targetUid, targetName, displayList, page);
        client.SendGump(gump, (btnId, _, _) => HandleHourBucketResponse(ch, btnId, targetUid, targetName, displayList, buckets));
    }

    private static void HandleStateRecGumpResponse(Character ch, uint btnId,
        List<(uint Uid, string Name, bool IsPlayer, string LastSeen, int Records)> chars,
        List<(int Id, uint CharUid, string Label, long StartTs, long EndTs, string SharedBy)>? shared,
        string? searchText = null)
    {
        var resp = SphereNet.Game.Recording.StateRecordingDialog.ParseResponse(btnId);
        string? filter = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
        switch (resp.Action)
        {
            case SphereNet.Game.Recording.StateRecAction.Close:
                break;

            case SphereNet.Game.Recording.StateRecAction.SearchChar:
                ShowStateRecBrowser(ch, 0, filter);
                break;

            case SphereNet.Game.Recording.StateRecAction.PageNext:
            {
                _stateRecBrowseState.TryGetValue(ch.Uid.Value, out var st);
                ShowStateRecBrowser(ch, st.Page + 1, filter);
                break;
            }

            case SphereNet.Game.Recording.StateRecAction.PagePrev:
            {
                _stateRecBrowseState.TryGetValue(ch.Uid.Value, out var st);
                ShowStateRecBrowser(ch, Math.Max(0, st.Page - 1), filter);
                break;
            }

            case SphereNet.Game.Recording.StateRecAction.SelectChar when resp.Index >= 0 && resp.Index < chars.Count:
            {
                var (uid, name, _, _, _) = chars[resp.Index];
                ShowHourBuckets(ch, uid, name, 0);
                break;
            }
        }
    }

    private static void HandleHourBucketResponse(Character ch, uint btnId,
        uint targetUid, string targetName,
        List<(string HourKey, string Display, int Snapshots, int Moves)> hours,
        List<(string HourKey, long StartTs, int SnapshotCount, int MoveCount)>? rawBuckets = null)
    {
        var resp = SphereNet.Game.Recording.StateRecordingDialog.ParseResponse(btnId);
        switch (resp.Action)
        {
            case SphereNet.Game.Recording.StateRecAction.Close:
                break;

            case SphereNet.Game.Recording.StateRecAction.BackToList:
                ShowStateRecBrowser(ch, 0);
                break;

            case SphereNet.Game.Recording.StateRecAction.PageNext:
            {
                _stateRecBrowseState.TryGetValue(ch.Uid.Value, out var st);
                ShowHourBuckets(ch, targetUid, targetName, st.Page + 1);
                break;
            }

            case SphereNet.Game.Recording.StateRecAction.PagePrev:
            {
                _stateRecBrowseState.TryGetValue(ch.Uid.Value, out var st);
                ShowHourBuckets(ch, targetUid, targetName, Math.Max(0, st.Page - 1));
                break;
            }

            case SphereNet.Game.Recording.StateRecAction.PlayLast30:
                PlayStateRecording(ch, targetUid, 30);
                break;

            case SphereNet.Game.Recording.StateRecAction.PlayHour when rawBuckets != null && resp.Index >= 0 && resp.Index < rawBuckets.Count:
            {
                var rb = rawBuckets[resp.Index];
                PlayStateRecording(ch, targetUid, rb.StartTs, rb.StartTs + 3_600_000);
                break;
            }

            case SphereNet.Game.Recording.StateRecAction.PinHour when rawBuckets != null && resp.Index >= 0 && resp.Index < rawBuckets.Count:
            {
                var rb = rawBuckets[resp.Index];
                var display = resp.Index < hours.Count ? hours[resp.Index].Display : rb.HourKey;
                _stateRecorder!.PinPeriod(rb.StartTs, rb.StartTs + 3_600_000,
                    $"{targetName} {display}", ch.Name ?? "Admin");
                SendSysMessage(ch, $"Hour pinned: {display}");
                ShowHourBuckets(ch, targetUid, targetName, 0);
                break;
            }

            case SphereNet.Game.Recording.StateRecAction.ShareHour when rawBuckets != null && resp.Index >= 0 && resp.Index < rawBuckets.Count:
            {
                var rb = rawBuckets[resp.Index];
                var display = resp.Index < hours.Count ? hours[resp.Index].Display : rb.HourKey;
                _stateRecorder!.ShareView(targetUid, rb.StartTs, rb.StartTs + 3_600_000,
                    $"{targetName} {display}", ch.Name ?? "Admin");
                SendSysMessage(ch, $"Hour shared: {display}");
                ShowHourBuckets(ch, targetUid, targetName, 0);
                break;
            }
        }
    }

    private static void HandleSharedGumpResponse(Character ch, uint btnId,
        List<(int Id, uint CharUid, string Label, long StartTs, long EndTs, string SharedBy)> shared)
    {
        var resp = SphereNet.Game.Recording.StateRecordingDialog.ParseResponse(btnId);
        if (resp.Action == SphereNet.Game.Recording.StateRecAction.WatchShared &&
            resp.Index >= 0 && resp.Index < shared.Count)
        {
            var (_, charUid, _, startTs, endTs, _) = shared[resp.Index];
            if (_stateRecorder!.CanView(ch.PrivLevel, charUid, startTs, endTs))
                PlayStateRecording(ch, charUid, startTs, endTs);
            else
                SendSysMessage(ch, "You don't have access to this recording.");
        }
    }

    private static void PlayStateRecording(Character ch, uint targetUid, int lastMinutes)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        PlayStateRecording(ch, targetUid, now - (long)lastMinutes * 60_000, now);
    }

    private static void PlayStateRecording(Character ch, uint targetUid, long startMs, long endMs)
    {
        if (_stateRecorder == null) return;
        if (_recordingEngine.IsReplaying(ch.Uid.Value))
        {
            SendSysMessage(ch, "Already replaying. Stop current replay first.");
            return;
        }

        var session = _stateRecorder.BuildReplaySession(targetUid, startMs, endMs);
        if (session == null || session.Packets.Count == 0)
        {
            SendSysMessage(ch, "No state records found for this character/time range.");
            return;
        }

        StartReplayForPlayer(ch, session);
    }

    private static void StartReplayForPlayer(Character gm, SphereNet.Game.Recording.RecordingSession session)
    {
        var state = _recordingEngine.StartReplay(gm, session);
        if (state == null) return;

        gm.SetStatFlag(StatFlag.Invisible);
        gm.SetStatFlag(StatFlag.Freeze);
        gm.IsReplaySpectator = true;
        _world.MoveCharacter(gm, session.Center);

        if (_clientsByCharUid.TryGetValue(gm.Uid, out var client))
        {
            var center = session.Center;
            client.NetState.Send(new PacketDrawObject(
                gm.Uid.Value, gm.BodyId, center.X, center.Y, center.Z,
                (byte)gm.Direction, gm.Hue, 0x80, 0, [],
                client.NetState.SupportsNewMobileIncoming).Build());
        }

        SendSysMessage(gm, $"State replay: {session.RecorderName}, {session.DurationMs / 1000.0:F0}s, {session.Packets.Count} packets");
        SendReplayOverlay(gm);
    }

    private static void ReplaySendPacket(uint uid, byte[] data)
    {
        if (_clientsByCharUid.TryGetValue(new Serial(uid), out var c))
            c.NetState.Send(new PacketBuffer(data));
    }

    private static void ReplayCameraUpdate(uint viewerUid, short x, short y, sbyte z, byte dir)
    {
        var ch = _world.FindChar(new Serial(viewerUid));
        if (ch == null) return;

        int dx = x - ch.Position.X;
        int dy = y - ch.Position.Y;
        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));

        byte map = ch.Position.Map;
        _world.MoveCharacter(ch, new Point3D(x, y, z, map));

        if (dist == 0) return;

        if (_clientsByCharUid.TryGetValue(new Serial(viewerUid), out var c))
        {
            if (dist == 1)
            {
                byte walkDir = (dx, dy) switch
                {
                    (0, -1) => 0,
                    (1, -1) => 1,
                    (1, 0) => 2,
                    (1, 1) => 3,
                    (0, 1) => 4,
                    (-1, 1) => 5,
                    (-1, 0) => 6,
                    (-1, -1) => 7,
                    _ => 0,
                };
                walkDir |= (byte)(dir & 0x80);
                c.NetState.Send(new PacketWalkForce(walkDir).Build());
            }
            else
            {
                c.NetState.Send(new PacketDrawPlayer(
                    ch.Uid.Value, ch.BodyId, ch.Hue, 0x80,
                    x, y, z, (byte)(dir & 0x07)).Build());
            }
        }
    }

    private static void FinishReplay(Character gm)
    {
        var phantoms = _recordingEngine.GetPhantomSerials(gm.Uid.Value);
        var state = _recordingEngine.GetReplayState(gm.Uid.Value);
        if (state != null)
        {
            _world.MoveCharacter(gm, state.OriginalPosition);
            if (!state.WasInvisible)
                gm.ClearStatFlag(StatFlag.Invisible);
            gm.ClearStatFlag(StatFlag.Freeze);
            gm.IsReplaySpectator = false;
        }
        _recordingEngine.StopReplay(gm.Uid.Value);

        if (_clientsByCharUid.TryGetValue(gm.Uid, out var client))
        {
            foreach (uint phantom in phantoms)
                client.NetState.Send(new PacketDeleteObject(phantom).Build());
            client.Resync();
        }
    }

    private static ushort MapAnimToMounted(ushort action)
    {
        return action switch
        {
            (ushort)AnimationType.CastDirected or
            (ushort)AnimationType.CastArea => (ushort)AnimationType.HorseSlap,
            (ushort)AnimationType.AttackWeapon or
            (ushort)AnimationType.Attack1HPierce or
            (ushort)AnimationType.Attack1HBash or
            (ushort)AnimationType.Attack2HBash or
            (ushort)AnimationType.Attack2HSlash or
            (ushort)AnimationType.Attack2HPierce or
            (ushort)AnimationType.AttackWrestle => (ushort)AnimationType.HorseAttack,
            (ushort)AnimationType.AttackBow => (ushort)AnimationType.HorseAttackBow,
            (ushort)AnimationType.AttackXBow => (ushort)AnimationType.HorseAttackXBow,
            (ushort)AnimationType.GetHit => (ushort)AnimationType.HorseSlap,
            (ushort)AnimationType.Block => (ushort)AnimationType.HorseSlap,
            (ushort)AnimationType.Bow or
            (ushort)AnimationType.Salute or
            (ushort)AnimationType.Eat => (ushort)AnimationType.HorseSlap,
            _ => action
        };
    }

    private static void TickReplayPackets()
    {
        _recordingEngine.TickReplays(ReplaySendPacket,
            uid =>
            {
                var ch = _world.FindChar(new Serial(uid));
                if (ch != null)
                {
                    FinishReplay(ch);
                    SendSysMessage(ch, "Replay finished.");
                }
            },
            ReplayCameraUpdate);
    }

    private static void TickReplayOverlays()
    {
        long now = Environment.TickCount64;
        foreach (var uid in _recordingEngine.GetActiveReplayUids())
        {
            var state = _recordingEngine.GetReplayState(uid);
            if (state == null) continue;
            if (now - state.LastOverlayTick < 1000) continue;
            state.LastOverlayTick = now;

            var ch = _world.FindChar(new Serial(uid));
            if (ch != null)
                SendReplayOverlay(ch);
        }
    }

    private static void ResyncCharacterClient(Character ch)
    {
        if (_clientsByCharUid.TryGetValue(ch.Uid, out var client))
            client.Resync();
    }

    private static void SendSysMessage(Character ch, string text)
    {
        if (_clientsByCharUid.TryGetValue(ch.Uid, out var client))
            client.SysMessage(text);
    }

    private static void HandleBotCommand(int count, string behavior, bool isStop)
    {
        if (_botEngine == null) return;

        if (isStop)
        {
            _botEngine.StopAllBots();
            return;
        }

        if (behavior.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
        {
            _botEngine.LogStats();
            return;
        }

        if (behavior.Equals("START", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    int port = _config?.ServPort ?? 2593;
                    await _botEngine.RestartBotsAsync("127.0.0.1", port);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[BOT] Failed to restart bots");
                }
            });
            return;
        }

        if (behavior.Equals("CLEAN", StringComparison.OrdinalIgnoreCase))
        {
            CleanBotCharacters();
            return;
        }

        if (behavior.StartsWith("SPAWN:", StringComparison.OrdinalIgnoreCase))
        {
            string cityName = behavior[6..];
            var city = cityName.ToUpperInvariant() switch
            {
                "BRITAIN" => SphereNet.Game.Diagnostics.BotSpawnCity.Britain,
                "TRINSIC" => SphereNet.Game.Diagnostics.BotSpawnCity.Trinsic,
                "MOONGLOW" => SphereNet.Game.Diagnostics.BotSpawnCity.Moonglow,
                "YEW" => SphereNet.Game.Diagnostics.BotSpawnCity.Yew,
                "MINOC" => SphereNet.Game.Diagnostics.BotSpawnCity.Minoc,
                "VESPER" => SphereNet.Game.Diagnostics.BotSpawnCity.Vesper,
                "SKARA" => SphereNet.Game.Diagnostics.BotSpawnCity.Skara,
                "JHELOM" => SphereNet.Game.Diagnostics.BotSpawnCity.Jhelom,
                _ => SphereNet.Game.Diagnostics.BotSpawnCity.All
            };
            _botEngine.SetSpawnCity(city);
            return;
        }

        // Parse behavior and optional city from "BEHAVIOR:CITY" format
        string behaviorPart = behavior;
        string? cityPart = null;
        int colonIdx = behavior.IndexOf(':');
        if (colonIdx > 0)
        {
            behaviorPart = behavior[..colonIdx];
            cityPart = behavior[(colonIdx + 1)..];
        }

        var botBehavior = behaviorPart.ToUpperInvariant() switch
        {
            "WALK" => SphereNet.Game.Diagnostics.BotBehavior.RandomWalk,
            "COMBAT" => SphereNet.Game.Diagnostics.BotBehavior.Combat,
            "IDLE" => SphereNet.Game.Diagnostics.BotBehavior.Idle,
            "SMART" => SphereNet.Game.Diagnostics.BotBehavior.SmartAI,
            _ => SphereNet.Game.Diagnostics.BotBehavior.SmartAI
        };

        // Set city if specified
        if (!string.IsNullOrEmpty(cityPart))
        {
            var city = cityPart.ToUpperInvariant() switch
            {
                "BRITAIN" => SphereNet.Game.Diagnostics.BotSpawnCity.Britain,
                "TRINSIC" => SphereNet.Game.Diagnostics.BotSpawnCity.Trinsic,
                "MOONGLOW" => SphereNet.Game.Diagnostics.BotSpawnCity.Moonglow,
                "YEW" => SphereNet.Game.Diagnostics.BotSpawnCity.Yew,
                "MINOC" => SphereNet.Game.Diagnostics.BotSpawnCity.Minoc,
                "VESPER" => SphereNet.Game.Diagnostics.BotSpawnCity.Vesper,
                "SKARA" => SphereNet.Game.Diagnostics.BotSpawnCity.Skara,
                "JHELOM" => SphereNet.Game.Diagnostics.BotSpawnCity.Jhelom,
                _ => SphereNet.Game.Diagnostics.BotSpawnCity.All
            };
            _botEngine.SetSpawnCity(city);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                int port = _config?.ServPort ?? 2593;
                await _botEngine.SpawnBotsAsync(count, botBehavior, "127.0.0.1", port);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[BOT] Failed to spawn bots");
            }
        });
    }

    private static void RestockBotCharacters()
    {
        if (_world == null) return;
        foreach (var ch in _world.OnlinePlayers)
        {
            if (!SphereNet.Game.Diagnostics.BotClient.IsBotCharName(ch.Name ?? "")) continue;
            var pack = ch.Backpack;
            if (pack == null) continue;

            RestockItem(pack, 0x0E21, "Bandage", 30);  // bandages
            RestockItem(pack, 0x0F0C, "Heal Potion", 5); // heal potions

            var weapon = ch.GetEquippedItem(Layer.OneHanded) ?? ch.GetEquippedItem(Layer.TwoHanded);
            if (weapon == null)
            {
                var sword = _world.CreateItem();
                sword.BaseId = 0x0F5E; // broadsword
                sword.Name = "Broadsword";
                ch.Equip(sword, Layer.TwoHanded);
            }

            // Spawn a mount nearby if none exists within 5 tiles
            bool hasMountNearby = ch.GetEquippedItem(Layer.Horse) != null;
            if (!hasMountNearby)
            {
                foreach (var nearby in _world.GetCharsInRange(ch.Position, 5))
                {
                    if (nearby != ch && nearby.BodyId is >= 0x00C8 and <= 0x00E4)
                    { hasMountNearby = true; break; }
                }
            }
            if (!hasMountNearby && !ch.IsDead)
            {
                var horse = _world.CreateCharacter();
                horse.BodyId = 0x00C8;
                horse.Name = "Horse";
                horse.NpcBrain = NpcBrainType.Animal;
                horse.Hits = 50;
                horse.MaxHits = 50;
                horse.SetTag("STRESS_TEST", "1");
                var horsePos = new Point3D(
                    (short)(ch.X + 1), (short)(ch.Y + 1), ch.Z, ch.MapIndex);
                _world.PlaceCharacter(horse, horsePos);
            }
        }
    }

    private static void RestockItem(Item pack, ushort baseId, string name, int targetAmount)
    {
        if (_world == null) return;
        int current = 0;
        foreach (var item in pack.Contents)
        {
            if (item.BaseId == baseId)
                current += item.Amount;
        }
        if (current >= targetAmount / 2) return;
        int toAdd = targetAmount - current;
        if (toAdd <= 0) return;
        var newItem = _world.CreateItem();
        newItem.BaseId = baseId;
        newItem.Name = name;
        newItem.Amount = (ushort)toAdd;
        pack.AddItem(newItem);
    }

    private static void CleanBotCharacters()
    {
        if (_botEngine == null || _world == null) return;

        _log.LogInformation("[BOT] Cleaning bot characters and accounts...");

        // Step 1: Find and delete bot characters (and their items)
        int charsDeleted = 0;
        int itemsDeleted = 0;
        var toDelete = new List<SphereNet.Game.Objects.Characters.Character>();

        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is SphereNet.Game.Objects.Characters.Character ch && 
                ch.Name != null && 
                SphereNet.Game.Diagnostics.BotClient.IsBotCharName(ch.Name))
            {
                toDelete.Add(ch);
            }
        }

        foreach (var ch in toDelete)
        {
            try
            {
                // Delete items in backpack/equipment first
                var backpack = ch.Backpack;
                if (backpack != null)
                {
                    var items = _world.GetContainerContents(backpack.Uid).ToList();
                    foreach (var item in items)
                    {
                        _world.DeleteObject(item);
                        itemsDeleted++;
                    }
                    _world.DeleteObject(backpack);
                    itemsDeleted++;
                }

                _world.DeleteObject(ch);
                charsDeleted++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[BOT] Failed to delete character {Name}", ch.Name);
            }
        }

        // Step 2: Delete bot accounts
        int accountsDeleted = 0;
        var botAccounts = _accounts.GetAllAccounts()
            .Where(a => SphereNet.Game.Diagnostics.BotClient.IsBotAccountName(a.Name))
            .Select(a => a.Name)
            .ToList();

        foreach (var accName in botAccounts)
        {
            if (_accounts.DeleteAccount(accName))
                accountsDeleted++;
        }

        _botEngine.ResetBotCounter();
        _log.LogInformation("[BOT] Cleanup complete: {Chars} characters, {Items} items, {Accounts} accounts deleted.",
            charsDeleted, itemsDeleted, accountsDeleted);
    }

    private static void ShowSectorListDialog(SphereNet.Game.Objects.Characters.Character gm)
    {
        if (!_clientsByCharUid.TryGetValue(gm.Uid, out var client)) return;

        var sectorSet = new HashSet<SphereNet.Game.World.Sectors.Sector>();
        foreach (var player in _world.OnlinePlayers)
        {
            var sector = _world.GetSector(player.Position);
            if (sector != null)
                sectorSet.Add(sector);
        }

        var entries = new List<SphereNet.Game.Diagnostics.SectorListDialog.SectorEntry>();
        int totalNpcs = 0;
        foreach (var sector in sectorSet)
        {
            int npcs = 0;
            foreach (var ch in sector.Characters)
            {
                if (!ch.IsPlayer && !ch.IsDeleted)
                    npcs++;
            }
            totalNpcs += npcs;
            entries.Add(new SphereNet.Game.Diagnostics.SectorListDialog.SectorEntry(
                sector.SectorX, sector.SectorY, sector.MapIndex,
                sector.OnlinePlayers.Count, npcs, sector.ItemCount, sector.IsSleeping));
        }

        entries.Sort((a, b) => b.NpcCount.CompareTo(a.NpcCount));

        var gump = SphereNet.Game.Diagnostics.SectorListDialog.Build(
            gm.Uid.Value, entries, totalNpcs, _world.OnlinePlayers.Count);

        client.SendGump(gump, (buttonId, switches, textEntries) =>
        {
            if (buttonId == SphereNet.Game.Diagnostics.SectorListDialog.BtnRefresh)
            {
                ShowSectorListDialog(gm);
            }
            else if (buttonId >= SphereNet.Game.Diagnostics.SectorListDialog.BtnGoBase)
            {
                int idx = (int)(buttonId - SphereNet.Game.Diagnostics.SectorListDialog.BtnGoBase);
                if (idx < entries.Count)
                {
                    var s = entries[idx];
                    int x = s.SectorX * SphereNet.Game.World.Sectors.Sector.SectorSize + SphereNet.Game.World.Sectors.Sector.SectorSize / 2;
                    int y = s.SectorY * SphereNet.Game.World.Sectors.Sector.SectorSize + SphereNet.Game.World.Sectors.Sector.SectorSize / 2;
                    var dest = new SphereNet.Core.Types.Point3D((short)x, (short)y, 0, s.MapIndex);
                    _world.MoveCharacter(gm, dest);
                    client.Resync();
                }
            }
        });
    }

    private static void ShowBotManagerDialog(SphereNet.Game.Objects.Characters.Character gm)
    {
        if (_botEngine == null) return;

        // Find the GameClient for this character
        if (!_clientsByCharUid.TryGetValue(gm.Uid, out var client)) return;

        var stats = _botEngine.GetStats();
        var currentCity = _botEngine.SpawnCity;
        int lastCount = _botEngine.GetMaxBotId() > 0 ? _botEngine.GetMaxBotId() : 100;

        var gump = SphereNet.Game.Diagnostics.BotManagerDialog.Build(
            gm.Uid.Value, stats, currentCity, lastCount);

        client.SendGump(gump, (buttonId, switches, textEntries) =>
        {
            var action = SphereNet.Game.Diagnostics.BotManagerDialog.ParseResponse(buttonId, switches, textEntries);
            HandleBotDialogAction(gm, action);
        });
    }

    private static void HandleBotDialogAction(SphereNet.Game.Objects.Characters.Character gm, 
        SphereNet.Game.Diagnostics.BotDialogAction action)
    {
        if (_botEngine == null) return;

        switch (action.ActionType)
        {
            case SphereNet.Game.Diagnostics.BotActionType.Start:
                if (action.BotCount <= 0) action.BotCount = 100;
                _botEngine.SetSpawnCity(action.City);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        int port = _config?.ServPort ?? 2593;
                        await _botEngine.SpawnBotsAsync(action.BotCount, action.Behavior, "127.0.0.1", port);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "[BOT] Failed to spawn bots");
                    }
                });
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var startClient))
                    startClient.SysMessage($"Starting {action.BotCount} bots with {action.Behavior} in {action.City}...");
                break;

            case SphereNet.Game.Diagnostics.BotActionType.Stop:
                _botEngine.StopAllBots();
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var stopClient))
                    stopClient.SysMessage("All bots stopped.");
                break;

            case SphereNet.Game.Diagnostics.BotActionType.Clean:
                CleanBotCharacters();
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var cleanClient))
                    cleanClient.SysMessage("Bot characters cleaned.");
                break;

            case SphereNet.Game.Diagnostics.BotActionType.Refresh:
                ShowBotManagerDialog(gm);
                return;
        }

        // Reopen dialog after action (except for refresh which already does it)
        if (action.ActionType != SphereNet.Game.Diagnostics.BotActionType.Refresh &&
            action.ActionType != SphereNet.Game.Diagnostics.BotActionType.None)
        {
            Task.Delay(500).ContinueWith(_ => ShowBotManagerDialog(gm));
        }
    }

    /// <summary>
    /// Script hot-reload (Source-X RESYNC). Reloads all modified .scp files
    /// from disk without restarting the server. Triggered via:
    ///   - Console key 'R'
    ///   - GM command ".RESYNC"
    ///   - Telnet "RESYNC"
    /// After reload, re-processes definitions (spells, items, chars).
    /// </summary>
}
