using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// The Source-X packet priority model: USEPACKETPRIORITY (off = one queue in send
/// order), the per-packet priorities of send.cpp while it is on, the packets with a
/// fixed priority either way, MAXQUEUESIZE demotion (CNetworkOutput::
/// QueuePacketTransaction) and the [PACKETPRIORITY] overrides of sphere.ini.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class PacketPriorityTests
{
    private static PacketBuffer Pkt(byte opcode)
    {
        var p = new PacketBuffer(2);
        p.WriteByte(opcode);
        p.WriteByte(0x00);
        return p;
    }

    private static PacketBuffer Extended(byte sub)
    {
        var p = new PacketBuffer(5);
        p.WriteByte(0xBF);
        p.WriteByte(0x00);
        p.WriteByte(0x05);
        p.WriteByte(0x00);
        p.WriteByte(sub);
        return p;
    }

    private static byte[] Order(NetState state) =>
        TestHarness.GetQueuedPackets(state).Select(p => p.Span[0]).ToArray();

    [Theory]
    [InlineData(0x22, PacketPriority.Highest)] // PacketMovementAck
    [InlineData(0x21, PacketPriority.Highest)] // PacketMovementRej
    [InlineData(0x1B, PacketPriority.High)]    // PacketPlayerStart
    [InlineData(0x27, PacketPriority.High)]    // PacketDragCancel
    [InlineData(0x78, PacketPriority.Normal)]  // draw object
    [InlineData(0x3C, PacketPriority.Normal)]  // container contents
    [InlineData(0xAE, PacketPriority.Normal)]  // unicode speech
    [InlineData(0x11, PacketPriority.Low)]     // PacketObjectStatus
    [InlineData(0x24, PacketPriority.Low)]     // PacketContainerOpen
    [InlineData(0x74, PacketPriority.Low)]     // PacketVendorBuyList
    [InlineData(0x9E, PacketPriority.Low)]     // PacketVendorSellList
    [InlineData(0xD4, PacketPriority.Low)]     // PacketDisplayBookNew
    [InlineData(0x90, PacketPriority.Low)]     // PacketDisplayMap
    [InlineData(0x56, PacketPriority.Low)]     // PacketMapPlot
    [InlineData(0xA9, PacketPriority.Low)]     // PacketCharacterList
    [InlineData(0x65, PacketPriority.Idle)]    // PacketWeather
    [InlineData(0x73, PacketPriority.Idle)]    // PacketPingAck
    [InlineData(0xEE, PacketPriority.Normal)]  // unlisted
    public void TheTableFollowsSourceXSendCpp(byte opcode, PacketPriority expected)
    {
        Assert.Equal(expected, PacketPriorityClassifier.Classify(opcode));
    }

    [Fact]
    public void OffByDefault_EverythingLeavesInSendOrder()
    {
        var state = TestHarness.CreateActiveNetState(NullLoggerFactory.Instance, 1);

        state.Send(Pkt(0x65));
        state.Send(Pkt(0x11));
        state.Send(Pkt(0x78));
        state.SendPriority(new PacketMoveAck(7, 0));

        Assert.False(PacketPriorityClassifier.Enabled);
        Assert.Equal(new byte[] { 0x65, 0x11, 0x78, 0x22 }, Order(state));
    }

    [Fact]
    public void FixedPrioritiesApplyEvenWhenOff()
    {
        var state = TestHarness.CreateActiveNetState(NullLoggerFactory.Instance, 2);

        state.Send(Pkt(0xD6)); // property list: Idle
        state.Send(Pkt(0x78));
        state.Send(Pkt(0xF2)); // time sync: High
        state.Send(Pkt(0x82)); // login denied: Highest

        Assert.Equal(new byte[] { 0x82, 0xF2, 0x78, 0xD6 }, Order(state));
    }

    [Fact]
    public void On_DrainsHighestFirst()
    {
        PacketPriorityClassifier.Configure(true, 75, null);
        var state = TestHarness.CreateActiveNetState(NullLoggerFactory.Instance, 3);

        state.Send(Pkt(0x65)); // Idle
        state.Send(Pkt(0x11)); // Low
        state.Send(Pkt(0x78)); // Normal
        state.Send(Pkt(0x1B)); // High
        state.SendPriority(new PacketMoveAck(7, 0)); // Highest

        Assert.Equal(new byte[] { 0x22, 0x1B, 0x78, 0x11, 0x65 }, Order(state));
    }

    [Fact]
    public void On_TheVendorSequenceKeepsItsOrder()
    {
        PacketPriorityClassifier.Configure(true, 75, null);
        var state = TestHarness.CreateActiveNetState(NullLoggerFactory.Instance, 4);

        // contents, prices, open: the prices must reach the client before the gump.
        state.Send(Pkt(0x3C));
        state.Send(Pkt(0x74));
        state.Send(Pkt(0x24));

        Assert.Equal(new byte[] { 0x3C, 0x74, 0x24 }, Order(state));
    }

    [Fact]
    public void On_AnExtendedSubcommandHasItsOwnPriority()
    {
        PacketPriorityClassifier.Configure(true, 75, null);

        Assert.Equal(PacketPriority.High, PacketPriorityClassifier.Resolve(Extended(0x26).Span));  // speed mode
        Assert.Equal(PacketPriority.Low, PacketPriorityClassifier.Resolve(Extended(0x14).Span));   // popup
        Assert.Equal(PacketPriority.Normal, PacketPriorityClassifier.Resolve(Extended(0x08).Span)); // map change
    }

    [Fact]
    public void AFullQueueMovesTheNextPacketDownALevel()
    {
        PacketPriorityClassifier.Configure(true, 2, null);
        var state = TestHarness.CreateActiveNetState(NullLoggerFactory.Instance, 5);

        state.Send(Pkt(0x78));
        state.Send(Pkt(0x77));
        state.Send(Pkt(0x20)); // Normal is full -> Low
        state.Send(Pkt(0x11)); // Low (now holding one) -> Low

        Assert.Equal(new byte[] { 0x78, 0x77, 0x20, 0x11 }, Order(state));

        state.Send(Pkt(0x1D)); // Normal full, Low full -> Idle
        Assert.Equal(new byte[] { 0x78, 0x77, 0x20, 0x11, 0x1D }, Order(state));
    }

    [Fact]
    public void IniOverridesMoveAnOpcodeAndReportBadLines()
    {
        var warnings = new List<string>();
        PacketPriorityClassifier.Configure(true, 75,
        [
            new("0x54", "Low"),
            new("0x6C", "4"),
            new("0xBF.0x26", "Idle"),
            new("0x74", "Normal"),
            new("0x55", "Fast"),
            new("speech", "Low"),
        ], warnings.Add);

        Assert.Equal(PacketPriority.Low, PacketPriorityClassifier.Classify(0x54));
        Assert.Equal(PacketPriority.Highest, PacketPriorityClassifier.Classify(0x6C));
        Assert.Equal(PacketPriority.Idle, PacketPriorityClassifier.Resolve(Extended(0x26).Span));
        Assert.Equal(PacketPriority.Normal, PacketPriorityClassifier.Classify(0x74));
        Assert.Contains(warnings, w => w.Contains("0x74") && w.Contains("sequence"));
        Assert.Contains(warnings, w => w.Contains("0x55=Fast"));
        Assert.Contains(warnings, w => w.Contains("speech"));
    }

    [Fact]
    public void OverridesDoNothingWhileOff()
    {
        PacketPriorityClassifier.Configure(false, 75, [new("0x78", "Highest")]);

        Assert.Equal(PacketPriority.Normal, PacketPriorityClassifier.Resolve(Pkt(0x78).Span));
    }

    /// <summary>The commented [PACKETPRIORITY] list at the end of the shipped sphere.ini
    /// shows every packet with its built-in priority, so an operator can uncomment the
    /// line to change. It has to say what the engine does: each listed value matches
    /// the table, and every packet the table moves off Normal is listed.</summary>
    [Fact]
    public void TheShippedTemplateListsTheBuiltInTable()
    {
        string ini = File.ReadAllText(TestRepo.Tracked("config/sphere.ini"));
        int at = ini.IndexOf("// [PACKETPRIORITY]", StringComparison.Ordinal);
        Assert.True(at >= 0, "sphere.ini has no [PACKETPRIORITY] template");

        PacketPriorityClassifier.Configure(true, 75, null);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wrong = new List<string>();
        foreach (Match m in Regex.Matches(ini[at..],
                     @"^//\s*0x([0-9A-F]{2})(?:\.0x([0-9A-F]{2}))?=(\w+)", RegexOptions.Multiline))
        {
            byte op = Convert.ToByte(m.Groups[1].Value, 16);
            var actual = m.Groups[2].Success
                ? PacketPriorityClassifier.Resolve(Extended(Convert.ToByte(m.Groups[2].Value, 16)).Span)
                : PacketPriorityClassifier.Classify(op);
            string key = m.Groups[2].Success ? $"0xBF.0x{m.Groups[2].Value}" : $"0x{m.Groups[1].Value}";
            listed.Add(key);
            if (!string.Equals(actual.ToString(), m.Groups[3].Value, StringComparison.OrdinalIgnoreCase))
                wrong.Add($"{key}: template says {m.Groups[3].Value}, engine uses {actual}");
        }

        byte[] fixedOps = [0x82, 0x26, 0xF2, 0xD6];
        for (int op = 0; op < 256; op++)
        {
            if (op == 0xBF || fixedOps.Contains((byte)op)) continue;
            if (PacketPriorityClassifier.Classify((byte)op) != PacketPriority.Normal && !listed.Contains($"0x{op:X2}"))
                wrong.Add($"0x{op:X2}: {PacketPriorityClassifier.Classify((byte)op)} in the table, missing from the template");
        }
        for (int sub = 0; sub < 256; sub++)
        {
            var p = PacketPriorityClassifier.Resolve(Extended((byte)sub).Span);
            if (p != PacketPriority.Normal && !listed.Contains($"0xBF.0x{sub:X2}"))
                wrong.Add($"0xBF.0x{sub:X2}: {p} in the table, missing from the template");
        }

        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }
}
