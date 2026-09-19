using System.Reflection;

namespace SphereNet.Tests;

/// <summary>
/// A crash on any thread leaves a record, with enough around it to diagnose.
///
/// The tick loop already contains a fault in any of its phases, so a bad script or a bad
/// packet does not take the shard down. What it cannot contain is an exception on one of
/// the other threads - the network accept loop, a timer, a save worker, the console
/// reader - or one thrown during boot before the loop exists. Those ended the process
/// with whatever the console happened to still be showing, which on a detached or
/// reconnected session is nothing.
///
/// The Host has had this since an RDP drop killed it silently. The server, which is the
/// process holding the world, had none.
/// </summary>
public sealed class ServerCrashRecorderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_crash_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static void Install(string dir) =>
        typeof(SphereNet.Server.Program)
            .GetMethod("InstallCrashRecorder", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [dir]);

    private static void Record(Exception ex, string source, bool terminating) =>
        typeof(SphereNet.Server.Program)
            .GetMethod("RecordCrash", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [ex, source, terminating]);

    private string CrashFile => Path.Combine(_dir, "server-crash.log");

    [Fact]
    public void ARecordCarriesTheExceptionAndItsStack()
    {
        Install(_dir);
        Exception caught;
        try { throw new InvalidOperationException("probe failure"); }
        catch (Exception ex) { caught = ex; }

        Record(caught, "probe", true);

        string text = File.ReadAllText(CrashFile);
        Assert.Contains("probe failure", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains(nameof(ARecordCarriesTheExceptionAndItsStack), text);  // the stack
        Assert.Contains("terminating=True", text);
    }

    /// <summary>The part that turns a stack trace into a diagnosis: what the shard was
    /// doing. Each field is read defensively, so a half-initialised server still
    /// produces the rest.</summary>
    [Fact]
    public void ARecordCarriesTheShardState()
    {
        Install(_dir);
        Record(new Exception("x"), "probe", false);

        string text = File.ReadAllText(CrashFile);
        Assert.Contains("uptime", text);
        Assert.Contains("thread", text);
        Assert.Contains("tick=", text);
        Assert.Contains("loopFaults=", text);
        Assert.Contains("gcHeapMB=", text);
    }

    /// <summary>A crash loop leaves a history rather than overwriting its own
    /// evidence.</summary>
    [Fact]
    public void RecordsAreAppended()
    {
        Install(_dir);
        Record(new Exception("first"), "probe", false);
        Record(new Exception("second"), "probe", false);

        string text = File.ReadAllText(CrashFile);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }

    /// <summary>An inner exception is the one that usually says why, so the whole chain
    /// goes in.</summary>
    [Fact]
    public void TheInnerExceptionIsRecorded()
    {
        Install(_dir);
        Record(new InvalidOperationException("outer", new IOException("the real cause")),
               "probe", false);

        Assert.Contains("the real cause", File.ReadAllText(CrashFile));
    }

    /// <summary>A directory it cannot write to must not turn the recorder itself into
    /// the crash.</summary>
    [Fact]
    public void AnUnwritableDestinationDoesNotThrow()
    {
        Install(Path.Combine(_dir, "\0invalid"));
        Assert.Null(Xunit.Record.Exception(() => Record(new Exception("x"), "probe", true)));
    }
}
