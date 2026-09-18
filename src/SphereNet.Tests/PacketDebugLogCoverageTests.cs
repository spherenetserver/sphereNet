using Microsoft.Extensions.Logging;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

/// <summary>
/// The packet log sees everything that goes out, including the priority sends.
///
/// There were two send paths and only one of them logged: the buffer overload wrote a
/// line, the priority overloads went straight to the queue. Everything sent at an
/// explicit priority was therefore invisible - which is every movement ack and reject,
/// the exact packets a "walking stutters" report is about.
///
/// A diagnostic that cannot see the stream it exists to explain is worse than none: it
/// sends the reader looking for the bug somewhere else. These cover the two priority
/// paths, which are the ones that were silent; the buffer path bails on a state with no
/// socket, so it cannot be driven from here without one.
/// </summary>
public sealed class PacketDebugLogCoverageTests
{
    private sealed class Sink(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> fmt) => lines.Add(fmt(state, ex));
    }

    private static (SphereNet.Network.State.NetState State, List<string> Lines) Wired()
    {
        var lines = new List<string>();
        var state = new SphereNet.Network.State.NetState(new Sink(lines))
        {
            DebugPackets = true
        };
        return (state, lines);
    }

    /// <summary>The movement ack, which goes out at Highest and was invisible.</summary>
    [Fact]
    public void APrioritySendIsLoggedToo()
    {
        var (state, lines) = Wired();
        state.SendPriority(new PacketMoveAck(7, 0));
        Assert.Contains(lines, l => l.Contains("SEND") && l.Contains("0x22"));
    }

    /// <summary>And an explicit-priority send.</summary>
    [Fact]
    public void AnExplicitPrioritySendIsLoggedToo()
    {
        var (state, lines) = Wired();
        state.Send(new PacketMoveAck(8, 0), PacketPriority.Highest);
        Assert.Contains(lines, l => l.Contains("SEND") && l.Contains("0x22"));
    }

    /// <summary>With the switch off, none of them write anything.</summary>
    [Fact]
    public void NothingIsLoggedWhenTheSwitchIsOff()
    {
        var (state, lines) = Wired();
        state.DebugPackets = false;
        state.Send(new PacketMoveAck(9, 0), PacketPriority.Highest);
        state.SendPriority(new PacketMoveAck(10, 0));
        Assert.Empty(lines);
    }
}
