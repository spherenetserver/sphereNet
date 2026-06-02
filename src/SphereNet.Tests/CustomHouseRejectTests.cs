using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// Phase 1 custom-housing support: the engine does not yet implement the
/// house-design editor, so the incoming 0xD7 encoded design commands and the
/// 0xBF sub-0x1E design query must be safely rejected/ignored — crucially
/// WITHOUT cross-dispatching into the unrelated 0xBF extended-command handlers
/// whose subcommand IDs overlap (Build 0x06 vs party 0x06, etc.).
/// </summary>
public class CustomHouseRejectTests
{
    private static byte[] EncodedPayload(uint serial, ushort subCmd, int extra = 2)
    {
        var p = new byte[6 + extra];
        p[0] = (byte)(serial >> 24); p[1] = (byte)(serial >> 16);
        p[2] = (byte)(serial >> 8); p[3] = (byte)serial;
        p[4] = (byte)(subCmd >> 8); p[5] = (byte)subCmd;
        return p;
    }

    [Theory]
    [InlineData(EncodedCommandRegistry.Build)]   // 0x06 collides with 0xBF party
    [InlineData(EncodedCommandRegistry.Roof)]    // 0x13 collides with 0xBF context-menu
    [InlineData(EncodedCommandRegistry.Revert)]  // 0x1A collides with 0xBF stat-lock
    [InlineData(EncodedCommandRegistry.Commit)]
    public void EncodedCommand_RoutesToEncodedHandler_NotExtended(ushort subCmd)
    {
        var state = new NetState(NullLogger<NetState>.Instance);
        bool extendedCalled = false;
        ushort seenSub = 0xFFFF;
        uint seenSerial = 0;
        state.ExtendedCommandHandler = (_, _, _) => extendedCalled = true;
        state.EncodedCommandHandler = (_, sub, serial) => { seenSub = sub; seenSerial = serial; };

        var buf = new PacketBuffer(EncodedPayload(0x0A0B0C0D, subCmd));
        new PacketEncodedCommand().OnReceive(buf, state);

        Assert.False(extendedCalled); // no cross-dispatch into 0xBF handlers
        Assert.Equal(subCmd, seenSub);
        Assert.Equal(0x0A0B0C0Du, seenSerial);
        Assert.True(EncodedCommandRegistry.IsCustomHouseDesign(subCmd));
    }

    [Fact]
    public void EncodedCommand_UnwiredHandler_IsSafeNoOp()
    {
        var state = new NetState(NullLogger<NetState>.Instance);
        var buf = new PacketBuffer(EncodedPayload(0x00000001, EncodedCommandRegistry.Build));

        var ex = Record.Exception(() => new PacketEncodedCommand().OnReceive(buf, state));

        Assert.Null(ex);
        Assert.False(buf.IsUnderrun);
    }

    [Fact]
    public void ExtendedCommand_QueryDesignDetails_0x1E_IsIgnored()
    {
        var pm = new PacketManager();
        bool extendedCalled = false;
        var state = new NetState(NullLogger<NetState>.Instance)
        {
            ExtendedCommandHandler = (_, _, _) => extendedCalled = true
        };

        // 0xBF payload = subCmd (0x001E) + data. 0x1E is not a known subcommand.
        var payload = new byte[] { 0x00, 0x1E, 0x00, 0x00 };
        new PacketExtendedCommand(pm).OnReceive(new PacketBuffer(payload), state);

        Assert.False(extendedCalled);
        Assert.False(ExtendedCommandRegistry.IsKnown(0x1E));
    }

    [Fact]
    public void EncodedCommand_ThroughProcessInput_NoDesync_ConnectionStaysOpen()
    {
        var mgr = new NetworkManager(2, NullLoggerFactory.Instance);
        var state = mgr.GetState(0)!;
        typeof(NetState).GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        state.Id = 1;
        state.IsSeeded = true;
        var crypto = state.Crypto;
        crypto.GetType().GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, true);
        crypto.GetType().GetField("_encType", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, EncryptionType.None);

        ushort seenSub = 0xFFFF;
        state.EncodedCommandHandler = (_, sub, _) => seenSub = sub;
        var unknownSeen = new List<byte>();
        mgr.OnUnknownPacket += (_, op, _) => unknownSeen.Add(op);

        // Framed 0xD7 (len=11) carrying Build, followed by a trailing 0xFA (len 1).
        // If the 0xD7 length were mis-handled, the trailing 0xFA would be read at
        // the wrong offset and the unknown-opcode capture below would not see it.
        byte[] d7 = { 0xD7, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x05, 0x00, (byte)EncodedCommandRegistry.Build, 0x00, 0x00 };
        byte[] trailing = { 0xFA };
        state.InjectReceived(d7.Concat(trailing).ToArray());

        typeof(NetworkManager).GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(mgr, [state]);

        Assert.False(state.IsClosing);
        Assert.Equal(0, state.ReceivedData.Length);
        Assert.Equal(EncodedCommandRegistry.Build, seenSub);
        Assert.Contains((byte)0xFA, unknownSeen); // trailing packet reached at correct offset
    }
}
