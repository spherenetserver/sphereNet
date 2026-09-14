using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Game.Recording;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What a .rec file has to prove before it is played back, and what writing one
/// must not destroy (review finding B11, the rest of it).
///
/// The short-read half was repaired earlier: a file cut mid-packet no longer loads
/// as a shorter recording. The rest of the finding is still here — nothing bounds
/// the file, nothing checks that a packet's own declared length agrees with the
/// length the recorder wrote around it, nothing checks that time moves forwards,
/// the file is written straight to its final name so a crash publishes a half
/// recording, and the id has one-second resolution, so a GM who records twice in
/// the same second overwrites the first recording with the second.
/// </summary>
public sealed class RecordingFileIntegrityTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public RecordingFileIntegrityTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_rec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private RecordingEngine Engine() => new(_dir, NullLogger.Instance);

    private static RecordingSession? Load(string path)
    {
        var m = typeof(RecordingEngine).GetMethod("LoadRecordingFromFile",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (RecordingSession?)m.Invoke(null, [path]);
    }

    private static void Save(RecordingEngine engine, RecordingSession session)
    {
        var m = typeof(RecordingEngine).GetMethod("SaveRecording",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        m.Invoke(engine, [session]);
    }

    private static RecordingSession Session(string id, params (int Offset, byte[] Data)[] packets)
    {
        var s = new RecordingSession
        {
            Id = id,
            RecorderName = "GM",
            RecorderUid = 0x1234,
            Center = new Point3D(100, 100, 0, 0),
            CreatedAt = DateTime.UtcNow,

        };
        foreach (var (off, data) in packets)
            s.Packets.Add(new RecordedPacket { TickOffset = off, Data = data });
        return s;
    }

    /// <summary>A minimal well-formed variable-length packet: opcode, its own length,
    /// then a body. 0x1C is ASCII speech.</summary>
    private static byte[] VariablePacket(int totalLength)
    {
        var p = new byte[totalLength];
        p[0] = 0x1C;
        p[1] = (byte)(totalLength >> 8);
        p[2] = (byte)totalLength;
        return p;
    }

    // ------------------------------------------------------------------

    [Fact]
    public void TwoRecordingsInTheSameSecondDoNotOverwriteEachOther()
    {
        var engine = Engine();
        var first = Session("ignored", (0, [0x77, 1, 2, 3]));
        var second = Session("ignored", (0, [0x77, 9, 9, 9]));

        // The id is built from the clock at one-second resolution plus the recorder's
        // uid, so a GM who records twice inside the same second produces the same file
        // name twice and the second save silently replaces the first.
        var idFor = typeof(RecordingEngine).GetMethod("NewRecordingId",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        string idA = (string)idFor.Invoke(null, [now, 0x1234u])!;
        string idB = (string)idFor.Invoke(null, [now.AddMilliseconds(40), 0x1234u])!;

        _out.WriteLine($"same second, same GM: '{idA}' vs '{idB}'");
        Assert.NotEqual(idA, idB);

        first.Id = idA; second.Id = idB;
        Save(engine, first);
        Save(engine, second);

        Assert.Equal(2, Directory.GetFiles(_dir, "*.rec").Length);
    }

    [Fact]
    public void APacketWhoseOwnLengthDisagreesIsRefused()
    {
        var engine = Engine();
        // The recorder wrote 12 bytes around a packet that says it is 40 long. One of
        // the two is wrong, and playback would hand the client whichever it believes.
        var bad = VariablePacket(12);
        bad[1] = 0; bad[2] = 40;
        Save(engine, Session("rec_bad_len", (0, bad)));

        string path = Path.Combine(_dir, "rec_bad_len.rec");
        var loaded = Load(path);

        _out.WriteLine($"declared 40, stored 12: loaded={(loaded == null ? "refused" : "accepted")}");
        Assert.Null(loaded);
    }

    [Fact]
    public void AnEmptyPacketIsRefused()
    {
        var engine = Engine();
        Save(engine, Session("rec_empty_pkt", (0, [])));

        // A zero-length packet has no opcode. Nothing downstream can do anything with
        // it except read past its end.
        Assert.Null(Load(Path.Combine(_dir, "rec_empty_pkt.rec")));
    }

    [Fact]
    public void TimeHasToMoveForwards()
    {
        var engine = Engine();
        Save(engine, Session("rec_backwards",
            (0, [0x77, 1, 2, 3]),
            (500, [0x77, 1, 2, 3]),
            (200, [0x77, 1, 2, 3])));     // earlier than the one before it

        // The recorder appends in order, so a decreasing offset is a corrupt file, and
        // playback schedules by these offsets: accepting it means a packet that waits
        // for a time that has already passed.
        var loaded = Load(Path.Combine(_dir, "rec_backwards.rec"));
        _out.WriteLine($"offsets 0,500,200: loaded={(loaded == null ? "refused" : "accepted")}");
        Assert.Null(loaded);
    }

    [Fact]
    public void ANegativeOffsetIsRefused()
    {
        var engine = Engine();
        Save(engine, Session("rec_negative", (-5, [0x77, 1, 2, 3])));
        Assert.Null(Load(Path.Combine(_dir, "rec_negative.rec")));
    }

    [Fact]
    public void AFileTooBigToBeARecordingIsRefusedBeforeItIsRead()
    {
        string path = Path.Combine(_dir, "rec_huge.rec");
        using (var fs = File.Create(path))
        {
            fs.SetLength(RecordingEngine.MaxRecordingFileBytes + 1);
        }

        // Refused on its size alone, without reading it: the count and lengths inside
        // a file that large are not worth trusting one at a time.
        var loaded = Load(path);
        _out.WriteLine($"{new FileInfo(path).Length / (1024 * 1024)} MB file: " +
                       $"loaded={(loaded == null ? "refused" : "accepted")}");
        Assert.Null(loaded);
    }

    [Fact]
    public void AGoodRecordingStillLoads()
    {
        var engine = Engine();
        var session = Session("rec_good",
            (0, [0x77, 1, 2, 3]),
            (100, VariablePacket(9)),
            (250, [0x1D, 0, 0, 0, 1]));
        Save(engine, session);

        // The control. Every refusal above would pass just as well if loading had
        // stopped working altogether.
        var loaded = Load(Path.Combine(_dir, "rec_good.rec"));
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Packets.Count);
        Assert.Equal([0, 100, 250], loaded.Packets.Select(p => p.TickOffset).ToArray());
    }

    [Fact]
    public void AFileTheLoaderWouldRefuseIsNeverPublished()
    {
        var engine = Engine();

        // A session the loader will reject - time running backwards. Writing goes to a
        // temporary name and is read back with the loader's OWN checks before it is
        // published, so the writer and the reader cannot disagree about what a valid
        // recording is. Writing straight to the final name would publish this.
        Save(engine, Session("rec_rejected",
            (0, [0x77, 1, 2, 3]),
            (500, [0x77, 1, 2, 3]),
            (200, [0x77, 1, 2, 3])));

        string final = Path.Combine(_dir, "rec_rejected.rec");
        _out.WriteLine($"after saving an invalid session: " +
                       $"{(Directory.GetFiles(_dir).Length == 0 ? "nothing published" : string.Join(", ", Directory.GetFiles(_dir).Select(Path.GetFileName)))}");

        Assert.False(File.Exists(final), "an unloadable recording must not be published");
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void AGoodRecordingIsPublishedAndLeavesNoTemporaryBehind()
    {
        var engine = Engine();
        Save(engine, Session("rec_partial", (0, [0x77, 1, 2, 3])));

        string final = Path.Combine(_dir, "rec_partial.rec");
        Assert.True(File.Exists(final));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.NotNull(Load(final));
    }
}
