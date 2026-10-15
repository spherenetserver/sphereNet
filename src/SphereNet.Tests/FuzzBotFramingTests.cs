using System.Buffers.Binary;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Game.Diagnostics;
using SphereNet.Game.Diagnostics.Behaviors;
using SphereNet.Network.Manager;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The fuzz bot attacks the handlers behind a valid frame, so every packet it builds
/// must frame exactly as the server frames it: a fixed packet at the length the
/// server expects from the 7.0 client the bot announces (0x08 is 15 bytes there, not
/// the table's 14), a variable one with a length field equal to its size. One
/// misframed packet would hand the start of the next one to the wrong handler and
/// turn the rest of the run into noise.
/// </summary>
public sealed class FuzzBotFramingTests
{
    [Fact]
    public void EveryPacketTheFuzzBotBuildsIsFramedAsTheServerFramesIt()
    {
        var bot = new BotClient(1, NullLogger.Instance);
        var w = bot.World;
        w.CharUid = 0x00000123;
        w.BackpackSerial = 0x40000100;
        w.KnownItems[0x40000200] = new BotKnownItem { Serial = 0x40000200 };
        w.Equipment[0x15] = new BotKnownItem { Serial = 0x40000300, Layer = 0x15 };
        w.Mobiles[0x00000456] = new KnownMobile { Serial = 0x00000456 };
        w.ActiveGump = new BotGumpState { Serial = 0x00000123, GumpId = 0x1234 };
        w.HasPendingTarget = true;
        w.TargetCursorId = 77;

        var opcodes = new NetworkManager(1, NullLoggerFactory.Instance).Packets.RegisteredOpcodes().ToArray();
        Assert.Contains((byte)0xB1, opcodes);
        var fuzz = new FuzzBot(seed: 42, opcodes);

        var seen = new HashSet<byte>();
        for (int i = 0; i < 50_000; i++)
        {
            byte[] p = fuzz.Next(bot);
            Assert.NotEmpty(p);
            seen.Add(p[0]);
            int fixedLen = FuzzBot.FixedLength(p[0]);
            if (fixedLen > 0)
            {
                Assert.True(p.Length == fixedLen, $"0x{p[0]:X2} is {p.Length} bytes, the server frames {fixedLen}");
            }
            else
            {
                Assert.True(p.Length >= 3, $"variable 0x{p[0]:X2} shorter than its header");
                int declared = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(1));
                Assert.True(declared == p.Length, $"0x{p[0]:X2} declares {declared} bytes and is {p.Length}");
                // Past 128 bytes the server may read 0xBE as the 5-byte legacy form.
                if (p[0] == 0xBE)
                    Assert.True(p.Length <= FuzzBot.MaxAssistVersionLength, $"0xBE of {p.Length} bytes");
            }
        }

        // It reaches the whole handler surface, not a corner of it.
        var missed = opcodes.Where(op => !seen.Contains(op)).ToArray();
        Assert.True(missed.Length == 0, "never sent: " + string.Join(' ', missed.Select(b => b.ToString("X2"))));
    }
}
