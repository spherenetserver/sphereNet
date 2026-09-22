using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The WEBLINK verb's packet, checked on the wire.
///
/// Upstream's is two lines — <c>initLength(); writeStringASCII(url);</c>
/// (PacketWebPage, send.cpp:3226-3234) — so 0xA5 is opcode, a two-byte total
/// length, then the address and a terminating zero. A length that does not match
/// the bytes that follow is the one way this packet can hurt: the client's reader
/// takes the next packet from the wrong offset and the session dies, which from the
/// server looks like an unexplained ConnectionReset.
///
/// Worth pinning because the verb was effectively unreachable until now. The
/// reference pack guards its links with IF !(&lt;ISBLANK &lt;url&gt;&gt;), and while
/// IsBlank answered "blank" for every string those branches never ran.
/// </summary>
public sealed class WebLinkPacketTests(ITestOutputHelper output)
{
    private static byte[] CaptureWebLink(string verbArgs, out int count)
    {
        using var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 19611);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Mortal";
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        client.TryExecuteScriptCommand(player, "WEBLINK", verbArgs, null);

        var frames = TestHarness.GetQueuedPackets(client.NetState)
            .Select(p => p.Span.ToArray()).Where(b => b.Length > 0 && b[0] == 0xA5).ToArray();
        count = frames.Length;
        return frames.Length > 0 ? frames[0] : [];
    }

    [Theory]
    [InlineData("https://example.com/donates")]
    [InlineData("https://sphere.example.com/a?b=1&c=2")]
    [InlineData("http://x.io")]
    public void TheFrameIsOpcodeLengthAddressAndZero(string url)
    {
        byte[] frame = CaptureWebLink(url, out int count);
        output.WriteLine($"{url} -> {count} frame(s), {frame.Length} bytes");
        Assert.Equal(1, count);

        Assert.Equal(0xA5, frame[0]);
        int declared = (frame[1] << 8) | frame[2];
        // The declared length must be the whole frame, or the client reads the next
        // packet from the wrong place.
        Assert.Equal(frame.Length, declared);
        Assert.Equal(url.Length + 4, declared);
        Assert.Equal(url, System.Text.Encoding.ASCII.GetString(frame, 3, url.Length));
        Assert.Equal(0, frame[^1]);
    }

    [Fact]
    public void AnEmptyAddressSendsNothingAtAll()
    {
        // Nothing to open, and an empty frame is exactly the sort of thing that is
        // read as a stray byte. Upstream's verb has nothing to send either.
        CaptureWebLink("   ", out int count);
        output.WriteLine($"blank address -> {count} frame(s)");
        Assert.Equal(0, count);
    }
}
