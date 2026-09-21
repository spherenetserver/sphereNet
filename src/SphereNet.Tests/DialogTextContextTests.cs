using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogTextContextTests
{
    [Theory]
    [InlineData("TEXT 1 1 0 0")]
    [InlineData("HTMLGUMP 1 1 200 40 0 0 0")]
    [InlineData("CROPPEDTEXT 1 1 200 40 0 0")]
    [InlineData("TEXTENTRY 1 1 200 40 0 1 0")]
    [InlineData("TEXTENTRYLIMITED 1 1 200 40 0 1 0 200")]
    public void IndexedTextUsesInitialSubjectAndArgumentsPerOpen(string control)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-text-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, $"[DIALOG d_text_context]\n0,0\nTAG.STATE=Changed\n{control}\n[DIALOG d_text_context TEXT]\n<NAME>|<SRC.NAME>|<ARGN1>|<ARGS>|<ARGV[0]>|<ARGO.NAME>|<TAG.STATE>\n");
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19510);
            var player = world.CreateCharacter();
            player.Name = "Player";
            var subject = world.CreateItem();
            subject.Name = "Subject";
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            foreach (string argument in new[] { "alpha", "beta" })
            {
                subject.SetTag("STATE", "Before");
                Assert.True(subject.TryExecuteCommand("DIALOG", $"d_text_context,2,{argument}", client));
                byte[] packet = TestHarness.GetQueuedPackets(client.NetState).Last(p => p.Span[0] is 0xDD or 0xB0).Span.ToArray();
                byte[] text;
                if (packet[0] == 0xDD)
                {
                    int header = 27 + BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(19, 4)) - 4;
                    int length = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(header + 4, 4)) - 4;
                    using var input = new MemoryStream(packet, header + 12, length);
                    using var zip = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zip.CopyTo(output);
                    text = output.ToArray();
                }
                else text = packet[(23 + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(19, 2)))..];
                Assert.Contains($"Subject|Player|2|{argument}|{argument}|Subject|Before", Encoding.BigEndianUnicode.GetString(text));
            }
        }
        finally { File.Delete(path); }
    }
}
