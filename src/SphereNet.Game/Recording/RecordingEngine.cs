using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Recording;

public sealed class RecordedPacket
{
    public int TickOffset { get; set; }
    public byte[] Data { get; set; } = [];
}

public sealed class RecordingSession
{
    public string Id { get; set; } = "";
    public string RecorderName { get; set; } = "";
    public uint RecorderUid { get; set; }
    public Point3D Center { get; set; }
    public long StartTick { get; set; }
    public int CaptureRange { get; set; } = 18;
    public List<RecordedPacket> Packets { get; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int DurationMs => Packets.Count > 0 ? Packets[^1].TickOffset : 0;
}

public sealed class ReplayState
{
    public RecordingSession Session { get; set; } = null!;
    public int PacketIndex { get; set; }
    public long StartTick { get; set; }
    public Point3D OriginalPosition { get; set; }
    public bool WasInvisible { get; set; }
    public Dictionary<uint, uint> SerialMap { get; } = [];
    public uint NextPhantomSerial { get; set; } = 0x3FFF0001;
    public uint NextPhantomItemSerial { get; set; } = 0x7FFE0001;
    public bool IsPaused { get; set; }
    public int PausedAtOffsetMs { get; set; }
    public float PlaybackSpeed { get; set; } = 1.0f;
    public long LastOverlayTick { get; set; }

    /// <summary>Packets this replay refused to forward because nobody has checked
    /// where that opcode keeps its serials. Counted rather than silent: a replay
    /// missing a packet is a question someone can answer, and a replay carrying a
    /// live serial is not.</summary>
    public int RefusedPackets { get; set; }

    /// <summary>The distinct opcodes refused - the work list for extending the
    /// table, kept by the engine instead of by guesswork.</summary>
    public HashSet<byte> RefusedOpcodes { get; } = [];
}

public sealed class RecordingEngine
{
    private readonly Dictionary<uint, RecordingSession> _activeRecordings = [];
    private readonly Dictionary<uint, ReplayState> _activeReplays = [];
    private readonly string _recordingsDir;

    public RecordingEngine(string recordingsDir)
    {
        _recordingsDir = recordingsDir;
        Directory.CreateDirectory(_recordingsDir);
    }

    public bool IsRecording(uint charUid) => _activeRecordings.ContainsKey(charUid);
    public bool IsReplaying(uint charUid) => _activeReplays.ContainsKey(charUid);
    public bool HasActiveRecordings => _activeRecordings.Count > 0;

    public Func<Point3D, int, List<byte[]>>? SnapshotNearbyCharacters { get; set; }

    public void StartRecording(Character recorder, int captureRange = 18)
    {
        uint uid = recorder.Uid.Value;
        if (_activeRecordings.ContainsKey(uid)) return;

        var session = new RecordingSession
        {
            Id = $"rec_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{uid:X8}",
            RecorderName = recorder.Name ?? "Unknown",
            RecorderUid = uid,
            Center = recorder.Position,
            StartTick = Environment.TickCount64,
            CaptureRange = captureRange
        };

        var snapshots = SnapshotNearbyCharacters?.Invoke(recorder.Position, captureRange);
        if (snapshots != null)
        {
            foreach (var pkt in snapshots)
                session.Packets.Add(new RecordedPacket { TickOffset = 0, Data = pkt });
        }

        _activeRecordings[uid] = session;
    }

    public RecordingSession? StopRecording(uint charUid)
    {
        if (!_activeRecordings.Remove(charUid, out var session))
            return null;

        SaveRecording(session);
        return session;
    }

    public void CapturePacket(uint recorderUid, Point3D packetOrigin, byte[] rawPacket)
    {
        if (!_activeRecordings.TryGetValue(recorderUid, out var session))
            return;

        if (session.Center.GetDistanceTo(packetOrigin) > session.CaptureRange)
            return;

        int offset = (int)(Environment.TickCount64 - session.StartTick);
        session.Packets.Add(new RecordedPacket
        {
            TickOffset = offset,
            Data = rawPacket
        });
    }

    public void CaptureFromBroadcast(Point3D broadcastCenter, int broadcastRange, byte[] rawPacket,
        uint moverUid = 0)
    {
        foreach (var (uid, session) in _activeRecordings)
        {
            bool isRecorder = moverUid != 0 && moverUid == session.RecorderUid;
            if (isRecorder)
            {
                session.Center = broadcastCenter;
            }

            if (isRecorder || session.Center.GetDistanceTo(broadcastCenter) <= session.CaptureRange + broadcastRange)
            {
                int offset = (int)(Environment.TickCount64 - session.StartTick);
                session.Packets.Add(new RecordedPacket
                {
                    TickOffset = offset,
                    Data = rawPacket
                });
            }
        }
    }

    public ReplayState? StartReplay(Character viewer, RecordingSession session)
    {
        uint uid = viewer.Uid.Value;
        if (_activeReplays.ContainsKey(uid)) return null;

        var state = new ReplayState
        {
            Session = session,
            PacketIndex = 0,
            StartTick = Environment.TickCount64,
            OriginalPosition = viewer.Position,
            WasInvisible = viewer.IsInvisible
        };
        _activeReplays[uid] = state;
        return state;
    }

    public ReplayState? GetReplayState(uint charUid)
    {
        _activeReplays.TryGetValue(charUid, out var state);
        return state;
    }

    public void StopReplay(uint charUid)
    {
        _activeReplays.Remove(charUid);
    }

    public bool HasActiveReplays => _activeReplays.Count > 0;
    public IEnumerable<uint> GetActiveReplayUids() => _activeReplays.Keys;

    public int GetElapsedMs(uint charUid)
    {
        if (!_activeReplays.TryGetValue(charUid, out var state))
            return 0;
        return GetElapsedMs(state);
    }

    private static int GetElapsedMs(ReplayState state)
    {
        if (state.IsPaused)
            return state.PausedAtOffsetMs;
        int elapsed = (int)((Environment.TickCount64 - state.StartTick) * state.PlaybackSpeed);
        return Math.Clamp(elapsed, 0, state.Session.DurationMs);
    }

    public void PauseReplay(uint charUid)
    {
        if (!_activeReplays.TryGetValue(charUid, out var state) || state.IsPaused)
            return;
        state.PausedAtOffsetMs = GetElapsedMs(state);
        state.IsPaused = true;
        state.LastOverlayTick = 0;
    }

    public void ResumeReplay(uint charUid)
    {
        if (!_activeReplays.TryGetValue(charUid, out var state) || !state.IsPaused)
            return;
        long now = Environment.TickCount64;
        state.StartTick = now - (long)(state.PausedAtOffsetMs / (double)state.PlaybackSpeed);
        state.IsPaused = false;
        state.LastOverlayTick = 0;
    }

    public void SetPlaybackSpeed(uint charUid, float speed)
    {
        if (!_activeReplays.TryGetValue(charUid, out var state))
            return;
        if (!state.IsPaused)
        {
            int currentMs = GetElapsedMs(state);
            state.PlaybackSpeed = speed;
            long now = Environment.TickCount64;
            state.StartTick = now - (long)(currentMs / (double)speed);
        }
        else
        {
            state.PlaybackSpeed = speed;
        }
        state.LastOverlayTick = 0;
    }

    public void SeekReplay(uint charUid, int targetMs,
        Action<uint, byte[]> sendRawPacket,
        Action<uint, short, short, sbyte, byte>? onCameraUpdate = null)
    {
        if (!_activeReplays.TryGetValue(charUid, out var state))
            return;

        targetMs = Math.Clamp(targetMs, 0, state.Session.DurationMs);

        foreach (uint phantom in state.SerialMap.Values)
        {
            var del = new byte[5];
            del[0] = 0x1D;
            WriteUInt32(del, 1, phantom);
            sendRawPacket(charUid, del);
        }

        state.SerialMap.Clear();
        state.NextPhantomSerial = 0x3FFF0001;
        state.NextPhantomItemSerial = 0x7FFE0001;

        var packets = state.Session.Packets;
        short lastX = 0, lastY = 0;
        sbyte lastZ = 0;
        byte lastDir = 0;
        bool hasCam = false;
        state.PacketIndex = packets.Count;

        for (int i = 0; i < packets.Count; i++)
        {
            if (packets[i].TickOffset > targetMs)
            {
                state.PacketIndex = i;
                break;
            }

            var data = RemapSerials(packets[i].Data, charUid, state);
            if (data != null)
            {
                sendRawPacket(charUid, data);

                if (packets[i].Data.Length >= 12 && packets[i].Data[0] == 0x77)
                {
                    uint origSerial = ReadUInt32(packets[i].Data, 1);
                    if (origSerial == state.Session.RecorderUid)
                    {
                        lastX = (short)(data[7] << 8 | data[8]);
                        lastY = (short)(data[9] << 8 | data[10]);
                        lastZ = (sbyte)data[11];
                        lastDir = (byte)(data[12] & 0x87);
                        hasCam = true;
                    }
                }
            }
        }

        long now = Environment.TickCount64;
        if (state.IsPaused)
            state.PausedAtOffsetMs = targetMs;
        else
            state.StartTick = now - (long)(targetMs / (double)state.PlaybackSpeed);

        state.LastOverlayTick = 0;

        if (hasCam)
            onCameraUpdate?.Invoke(charUid, lastX, lastY, lastZ, lastDir);
    }

    public void TickReplays(Action<uint, byte[]> sendRawPacket, Action<uint>? onReplayFinished,
        Action<uint, short, short, sbyte, byte>? onCameraUpdate = null)
    {
        long now = Environment.TickCount64;
        List<uint>? finished = null;

        foreach (var (uid, state) in _activeReplays)
        {
            if (state.IsPaused)
                continue;

            int elapsed = (int)((now - state.StartTick) * state.PlaybackSpeed);
            var packets = state.Session.Packets;

            while (state.PacketIndex < packets.Count)
            {
                var pkt = packets[state.PacketIndex];
                if (pkt.TickOffset > elapsed)
                    break;

                var data = RemapSerials(pkt.Data, uid, state);
                if (data != null)
                {
                    sendRawPacket(uid, data);

                    if (onCameraUpdate != null && pkt.Data.Length >= 12 && pkt.Data[0] == 0x77)
                    {
                        uint origSerial = ReadUInt32(pkt.Data, 1);
                        if (origSerial == state.Session.RecorderUid)
                        {
                            short x = (short)(data[7] << 8 | data[8]);
                            short y = (short)(data[9] << 8 | data[10]);
                            sbyte z = (sbyte)data[11];
                            byte dir = (byte)(data[12] & 0x87);
                            onCameraUpdate(uid, x, y, z, dir);
                        }
                    }
                }
                state.PacketIndex++;
            }

            if (state.PacketIndex >= packets.Count)
            {
                finished ??= [];
                finished.Add(uid);
            }
        }

        if (finished != null)
        {
            foreach (uint uid in finished)
            {
                onReplayFinished?.Invoke(uid);
                if (_activeReplays.ContainsKey(uid))
                    _activeReplays.Remove(uid);
            }
        }
    }

    public List<uint> GetPhantomSerials(uint viewerUid)
    {
        if (!_activeReplays.TryGetValue(viewerUid, out var state))
            return [];
        return [.. state.SerialMap.Values];
    }

    private static byte[]? RemapSerials(byte[] data, uint viewerUid, ReplayState state)
    {
        if (data.Length < 2) return null;
        byte opcode = data[0];

        if (opcode == 0x20 || opcode == 0x22 || opcode == 0x21)
            return null;

        if (opcode == 0x78)
            return RemapDrawObject(data, state);
        if (opcode == 0x3C)
            return RemapContainerContents(data, state);
        if (opcode == 0x1A)
            return RemapWorldItem(data, state);
        if (opcode == 0x89)
            return RemapCorpseEquipment(data, state);

        int[] serialOffsets = GetSerialOffsets(opcode, data.Length);
        if (serialOffsets.Length > 0)
        {
            byte[] copy = (byte[])data.Clone();
            foreach (int offset in serialOffsets)
                RemapSerialAt(copy, offset, state);
            return copy;
        }

        // Nothing is passed through on a guess any more. A packet whose layout nobody
        // has checked may carry a live serial, and forwarding it hands the spectator's
        // client a real object to bind to - the failure this whole finding is about.
        // Refusing costs the replay one packet and says so; passing it costs
        // correctness and says nothing (review finding B10).
        if (!CarriesNoSerial.Contains(opcode))
        {
            state.RefusedPackets++;
            state.RefusedOpcodes.Add(opcode);
            return null;
        }
        return data;
    }

    private static byte[]? RemapDrawObject(byte[] data, ReplayState state)
    {
        if (data.Length < 19) return null;
        byte[] copy = (byte[])data.Clone();

        RemapSerialAt(copy, 3, state);

        int pos = 19;
        while (pos + 4 <= copy.Length)
        {
            uint itemSerial = ReadUInt32(copy, pos);
            if (itemSerial == 0) break;

            RemapItemSerialAt(copy, pos, state);
            pos += 4;

            if (pos + 2 > copy.Length) break;
            ushort itemId = (ushort)(copy[pos] << 8 | copy[pos + 1]);
            if ((itemId & 0x8000) != 0)
            {
                pos += 2 + 1 + 2; // itemId + layer + hue
            }
            else
            {
                pos += 2 + 1; // itemId + layer
            }
        }

        return copy;
    }

    /// <summary>0x1A keeps a FLAG in the top bit of its serial: the writer sets
    /// 0x80000000 when an amount follows (PacketWorldItem.Build). Treating the whole
    /// word as a serial mapped the flagged value - a different key from the same
    /// item's plain serial elsewhere, so one item became two phantoms - and wrote
    /// back a phantom with the flag gone, after which the client stops expecting the
    /// amount field and reads the next two bytes as a coordinate. The entry was in
    /// the table and looked right.</summary>
    private static byte[] RemapWorldItem(byte[] data, ReplayState state)
    {
        byte[] copy = (byte[])data.Clone();
        if (copy.Length < 7) return copy;

        const uint AmountFlag = 0x80000000;
        uint raw = ReadUInt32(copy, 3);
        uint flag = raw & AmountFlag;
        uint serial = raw & ~AmountFlag;
        if (serial == 0) return copy;

        WriteUInt32(copy, 3, serial);
        RemapSerialAt(copy, 3, state);
        WriteUInt32(copy, 3, ReadUInt32(copy, 3) | flag);
        return copy;
    }

    /// <summary>0x3C is a repeating structure, so no fixed offset list can describe
    /// it: opcode, length, count, then one entry per item carrying BOTH the item and
    /// its container (PacketContainerContents.Build). It was not in the table at all,
    /// which meant a replayed container listing went out with live serials in it.</summary>
    private static byte[]? RemapContainerContents(byte[] data, ReplayState state)
    {
        if (data.Length < 5) return null;
        byte[] copy = (byte[])data.Clone();

        int count = copy[3] << 8 | copy[4];
        int body = copy.Length - 5;
        if (count <= 0) return copy;

        // The grid-index byte per entry is client-version dependent, and the packet
        // says which it is by its own size rather than by a field.
        int perItem = body / count;
        if (perItem != 19 && perItem != 20)
            return null;                       // not a shape we can read: refuse it
        int containerOffset = perItem == 20 ? 15 : 14;

        for (int i = 0; i < count; i++)
        {
            int entry = 5 + i * perItem;
            if (entry + perItem > copy.Length) break;
            RemapItemSerialAt(copy, entry, state);
            RemapSerialAt(copy, entry + containerOffset, state);
        }
        return copy;
    }

    /// <summary>0x89 is the corpse's equipment list: the corpse serial, then a
    /// terminated run of (layer, item serial) pairs (PacketCorpseEquipment.Build).
    /// Another repeating shape no offset list can describe, and another packet that
    /// was going out with live serials in it.</summary>
    private static byte[]? RemapCorpseEquipment(byte[] data, ReplayState state)
    {
        if (data.Length < 8) return null;
        byte[] copy = (byte[])data.Clone();

        RemapSerialAt(copy, 3, state);          // opcode, length:2, corpse serial

        int pos = 7;
        while (pos < copy.Length)
        {
            byte layer = copy[pos];
            if (layer == 0) break;              // terminator
            if (pos + 5 > copy.Length) break;
            RemapItemSerialAt(copy, pos + 1, state);
            pos += 5;
        }
        return copy;
    }

    /// <summary>Opcodes checked against their writer and found to carry no serial at
    /// all. Anything not here and not in the offset table is refused rather than
    /// forwarded, so the list is a claim about a packet's layout, not a convenience.</summary>
    private static readonly HashSet<byte> CarriesNoSerial =
    [
        0x54,   // PacketSound: mode, sound id, volume, x, y, z
        0x4F,   // PacketGlobalLight: one light level
        0x65,   // PacketWeather: type, count, temperature
        0x6D,   // PacketPlayMusic: music id
        0x53,   // PacketPopupMessage: message index
        0x72,   // PacketWarModeResponse: flag + three unused bytes
        0xBC,   // PacketSeason: season + play sound flag
    ];

    private static void RemapSerialAt(byte[] data, int offset, ReplayState state)
    {
        if (offset + 4 > data.Length) return;
        uint serial = ReadUInt32(data, offset);
        if (serial == 0) return;

        if (!state.SerialMap.TryGetValue(serial, out uint phantom))
        {
            bool isItem = (serial & 0x40000000) != 0;
            phantom = isItem ? state.NextPhantomItemSerial++ : state.NextPhantomSerial++;
            state.SerialMap[serial] = phantom;
        }
        WriteUInt32(data, offset, phantom);
    }

    private static void RemapItemSerialAt(byte[] data, int offset, ReplayState state)
    {
        if (offset + 4 > data.Length) return;
        uint serial = ReadUInt32(data, offset);
        if (serial == 0) return;

        if (!state.SerialMap.TryGetValue(serial, out uint phantom))
        {
            phantom = state.NextPhantomItemSerial++;
            state.SerialMap[serial] = phantom;
        }
        WriteUInt32(data, offset, phantom);
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    /// <summary>
    /// Where a packet keeps its object serials, by opcode.
    ///
    /// Being wrong here is not a missed remap: it is four bytes written over whatever
    /// the packet actually keeps at that offset, and the spectator then sees a
    /// different sound, a different effect type or an item at a different position
    /// (review finding B10). Each entry below is checked against the writer that
    /// produces the packet, named in the comment - a table maintained by memory drifts
    /// from the packets it describes and nothing complains.
    ///
    /// A packet that carries NO serial belongs here as an empty entry rather than as a
    /// guess, and an opcode that is not listed is passed through untouched.
    /// </summary>
    private static int[] GetSerialOffsets(byte opcode, int length)
    {
        return opcode switch
        {
            // Fixed packets whose serial follows the opcode directly.
            0x77 => [1],          // PacketMobileMoving
            0x1D => [1],          // PacketDeleteObject
            0x6E => [1],          // PacketCharacterAnimation
            0x0B => [1],          // PacketDamage

            // Effects keep TWO serials, after a one-byte type: source at 2, target at
            // 6 (WriteBaseEffect). Remapping byte 1 scribbled on the effect type,
            // mangled the source and left the target pointing at a live object.
            0x70 => length >= 10 ? [2, 6] : [],   // PacketEffect
            0xC0 => length >= 10 ? [2, 6] : [],   // PacketEffectHued

            // No serial at all. 0x54 is mode + sound id + volume + position
            // (PacketSound) and 0x4F is a single light level (PacketGlobalLight);
            // both used to have their first real field overwritten.
            0x54 => [],
            0x4F => [],

            // Variable-length packets: opcode, length:2, then the serial.
            0x1A => [3],          // PacketObjectInfo
            0x1C => [3],          // PacketAsciiMessage
            0xAE => [3],          // PacketUnicodeMessage
            0xCC => [3],          // PacketLocMessage
            0xC1 => [3],          // PacketLocMessageAffix

            // Item serial at 1; the container serial sits after the grid index byte on
            // 6.0.1.7+ clients and in its place on older ones (PacketContainerItem).
            // One fixed offset cannot be right for both eras - and the offset in use
            // was the stack offset, so neither serial moved and the amount did.
            0x25 => length >= 21 ? [1, 15] : length >= 20 ? [1, 14] : [],
            0x2E => [1, 9],       // PacketWornItem: item serial + wearer serial

            // Combat swing: a leading zero byte, then attacker and defender
            // (PacketSwing.Build). Recorded through the combat broadcast.
            0x2F => length >= 10 ? [2, 6] : [],

            // Particle effect: the same base-effect header as 0x70/0xC0 - type at 1,
            // source at 2, target at 6 - plus the effect's own uid near the end
            // (PacketEffectParticle.Build writes it 7 bytes before the packet ends).
            0xC7 => length >= 49 ? [2, 6, 42] : [],

            // SA world item: 0x0001, a data-type byte, then the serial
            // (PacketWorldItemSA.Build).
            0xF3 => length >= 8 ? [4] : [],

            // The rest of what a nearby broadcast can carry. Each is the first field
            // after the opcode, or after the variable-length prefix, and each was
            // being REFUSED until it was checked - which is safe but strips the
            // replay of death, dragging, animation and health changes.
            0xAF => length >= 13 ? [1, 5] : [],   // PacketDeathAnimation: mobile + corpse
            0xE2 => length >= 10 ? [1] : [],      // PacketNewAnimation
            0xA1 => length >= 9 ? [1] : [],       // PacketUpdateHealth
            0xA2 => length >= 9 ? [1] : [],       // PacketUpdateMana
            0xA3 => length >= 9 ? [1] : [],       // PacketUpdateStamina
            0x17 => length >= 12 ? [3] : [],      // PacketHealthBarStatus (variable)
            0x16 => length >= 9 ? [3] : [],       // PacketHealthBarStatusNew (variable)

            // Drag animation: item id, an unused byte, hue and amount come first, then
            // the two ends of the drag (PacketDragAnimation.Build).
            0x23 => length >= 26 ? [8, 15] : [],

            _ => []
        };
    }

    public List<(string Id, string Recorder, DateTime Date, int DurationMs, int PacketCount)> ListRecordings()
    {
        var result = new List<(string, string, DateTime, int, int)>();
        if (!Directory.Exists(_recordingsDir)) return result;

        foreach (var file in Directory.GetFiles(_recordingsDir, "*.rec"))
        {
            try
            {
                using var fs = File.OpenRead(file);
                using var br = new BinaryReader(fs);
                string id = br.ReadString();
                string recorder = br.ReadString();
                long ticks = br.ReadInt64();
                var date = new DateTime(ticks, DateTimeKind.Utc);
                int durationMs = br.ReadInt32();
                int packetCount = br.ReadInt32();
                br.ReadUInt32(); // RecorderUid
                result.Add((id, recorder, date, durationMs, packetCount));
            }
            catch { }
        }

        result.Sort((a, b) => b.Item3.CompareTo(a.Item3));
        return result;
    }

    public RecordingSession? LoadRecording(int index)
    {
        var list = ListRecordingFiles();
        if (index < 0 || index >= list.Count) return null;
        return LoadRecordingFromFile(list[index]);
    }

    public bool DeleteRecording(int index)
    {
        var list = ListRecordingFiles();
        if (index < 0 || index >= list.Count) return false;
        try { File.Delete(list[index]); return true; } catch { return false; }
    }

    public RecordingSession? LoadRecordingById(string id)
    {
        string path = Path.Combine(_recordingsDir, id + ".rec");
        if (!File.Exists(path)) return null;
        return LoadRecordingFromFile(path);
    }

    private List<string> ListRecordingFiles()
    {
        if (!Directory.Exists(_recordingsDir))
            return [];

        var files = Directory.GetFiles(_recordingsDir, "*.rec");
        Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
        return [.. files];
    }

    private void SaveRecording(RecordingSession session)
    {
        string path = Path.Combine(_recordingsDir, session.Id + ".rec");
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        bw.Write(session.Id);
        bw.Write(session.RecorderName);
        bw.Write(session.CreatedAt.Ticks);
        bw.Write(session.DurationMs);
        bw.Write(session.Packets.Count);
        bw.Write(session.RecorderUid);

        bw.Write(session.Center.X);
        bw.Write(session.Center.Y);
        bw.Write(session.Center.Z);
        bw.Write(session.Center.Map);

        foreach (var pkt in session.Packets)
        {
            bw.Write(pkt.TickOffset);
            bw.Write((ushort)pkt.Data.Length);
            bw.Write(pkt.Data);
        }
    }

    private static RecordingSession? LoadRecordingFromFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            var session = new RecordingSession
            {
                Id = br.ReadString(),
                RecorderName = br.ReadString(),
                CreatedAt = new DateTime(br.ReadInt64(), DateTimeKind.Utc)
            };
            int durationMs = br.ReadInt32();
            int packetCount = br.ReadInt32();
            session.RecorderUid = br.ReadUInt32();

            short cx = br.ReadInt16();
            short cy = br.ReadInt16();
            sbyte cz = br.ReadSByte();
            byte cmap = br.ReadByte();
            session.Center = new Point3D(cx, cy, cz, cmap);

            // A recording is all of its packets or none of them. ReadBytes returns
            // what it HAS rather than throwing, so a file cut short produced a session
            // whose last packet was a few bytes shy of the length it declared - and
            // playback sends that malformed packet to a real client (review finding
            // B11). A short read is a corrupt file, not a shorter recording.
            if (packetCount < 0)
                return null;

            for (int i = 0; i < packetCount; i++)
            {
                int tickOffset = br.ReadInt32();
                ushort len = br.ReadUInt16();
                byte[] data = br.ReadBytes(len);
                if (data.Length != len)
                    return null;
                session.Packets.Add(new RecordedPacket { TickOffset = tickOffset, Data = data });
            }

            return session;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or FormatException or ArgumentException)
        {
            // Truncated/corrupt .rec file or FS failure — treat as "no such
            // recording" so the browser dialog just omits it. Anything else
            // (engine bug) propagates.
            return null;
        }
    }
}
