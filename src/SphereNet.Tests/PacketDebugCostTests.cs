using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What the packet debug log costs per packet, and that the filter actually saves it.
///
/// The formatting is an allocation per packet on the thread that is about to send a
/// movement acknowledgement, so a debug session can produce the delay it was opened to
/// explain. Two things had to be true and were not: the DebugPacketOpcodes whitelist
/// reached only the receive side, and the hex string was built whether or not a sink was
/// listening.
///
/// The numbers go to the test output rather than into an assertion, because a timing
/// figure on a shared machine is not something to fail a build over. The assertions are
/// about the SHAPE: a filtered-out packet must do no formatting work, and a disabled log
/// must do none at all.
/// </summary>
public sealed class PacketDebugCostTests(ITestOutputHelper outp)
{
    private sealed class CountingSink(Func<LogLevel, bool> enabled) : ILogger
    {
        public int Lines;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => enabled(logLevel);
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> fmt)
        {
            Lines++;
            fmt(state, ex);   // the sink formats, as a file sink would
        }
    }

    private static (NetState State, CountingSink Sink) Wired(
        bool debug, HashSet<byte>? filter, bool sinkListens = true,
        HashSet<string>? categories = null)
    {
        var sink = new CountingSink(_ => sinkListens);
        var state = new NetState(sink)
        {
            DebugPackets = debug,
            DebugPacketOpcodeFilter = filter,
            DebugPacketCategoryFilter = categories
        };
        return (state, sink);
    }

    /// <summary>A movement ack outside the whitelist writes nothing - which is the
    /// point of narrowing the log while chasing a stutter.</summary>
    [Fact]
    public void AnOpcodeOutsideTheWhitelistIsNotLogged()
    {
        var (state, sink) = Wired(true, [0xBF]);
        state.SendPriority(new PacketMoveAck(1, 0));
        Assert.Equal(0, sink.Lines);
    }

    [Fact]
    public void AnOpcodeInsideItIsLogged()
    {
        var (state, sink) = Wired(true, [0x22]);
        state.SendPriority(new PacketMoveAck(1, 0));
        Assert.Equal(1, sink.Lines);
    }

    /// <summary>With no whitelist the log is everything, as before.</summary>
    [Fact]
    public void NoWhitelistMeansEverything()
    {
        var (state, sink) = Wired(true, null);
        state.SendPriority(new PacketMoveAck(1, 0));
        Assert.Equal(1, sink.Lines);
    }

    /// <summary>A sink that is not listening at Debug gets nothing, and nothing is
    /// formatted for it.</summary>
    [Fact]
    public void ASinkThatIsNotListeningCostsNothing()
    {
        var (state, sink) = Wired(true, null, sinkListens: false);
        state.SendPriority(new PacketMoveAck(1, 0));
        Assert.Equal(0, sink.Lines);
    }

    /// <summary>Report the per-packet cost of the three cases, for whoever is deciding
    /// whether to leave the log on.</summary>
    [Fact]
    public void ReportThePerPacketCost()
    {
        const int N = 200_000;
        foreach (var (label, debug, filter) in new (string, bool, HashSet<byte>?)[]
                 {
                     ("off", false, null),
                     ("on, filtered out", true, [0xBF]),
                     ("on, logging", true, null),
                 })
        {
            var (state, _) = Wired(debug, filter);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
                state.SendPriority(new PacketMoveAck((byte)i, 0));
            sw.Stop();
            outp.WriteLine($"{label,-18} {sw.Elapsed.TotalMilliseconds * 1000.0 / N:F2} us/packet");
        }
    }

    /// <summary>The category filter, which is the one that matters on a busy shard: a
    /// street of creatures sends far more about them than about the player.</summary>
    [Fact]
    public void ACategoryOutsideTheListIsNotLogged()
    {
        // A move ack carries no serial, so it classifies as "packet".
        var (state, sink) = Wired(true, null, categories: ["npc"]);
        state.SendPriority(new PacketMoveAck(1, 0));
        Assert.Equal(0, sink.Lines);
    }

    [Fact]
    public void ACategoryInsideItIsLogged()
    {
        var (state, sink) = Wired(true, null, categories: ["packet"]);
        state.SendPriority(new PacketMoveAck(1, 0));
        Assert.Equal(1, sink.Lines);
    }

    /// <summary>Saying nothing keeps every category, so an existing shard is
    /// unchanged.</summary>
    [Fact]
    public void NoCategoryListMeansEveryCategory()
    {
        var (state, sink) = Wired(true, null, categories: null);
        state.SendPriority(new PacketMoveAck(1, 0));
        Assert.Equal(1, sink.Lines);
    }
}
