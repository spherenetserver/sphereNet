using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogRawControlTests
{
    [Theory]
    [InlineData("TEXT 010 020 030 1")]
    [InlineData("HTMLGUMP 010 020 120 45 1 0 0")]
    [InlineData("CROPPEDTEXT 010 020 120 45 030 1")]
    [InlineData("TEXTENTRY 010 020 120 45 030 7 1")]
    [InlineData("TEXTENTRYLIMITED 010 020 120 45 030 7 1 32")]
    [InlineData("GROUP 010")]
    [InlineData("ITEMPROPERTY 010")]
    [InlineData("TEXT 10 20 0 1", "GUMPIC", false)]
    [InlineData("TEXT 10 20 0 1", "GUMPIC", true)]
    [InlineData("TEXT 10 20 0 1", "RESIZE", false)]
    [InlineData("TEXT 10 20 0 1", "RESIZE", true)]
    [InlineData("TEXT 10 20 0 1", "f_dialog_helper", false)]
    [InlineData("TEXT 10 20 0 1", "f_dialog_helper", true)]
    [InlineData("TEXT 10 20 0 1", "NAME", false)]
    [InlineData("TEXT 10 20 0 1", "NAME", true)]
    [InlineData("TEXT 10 20 0 1", "SOUND", false)]
    [InlineData("TEXT 10 20 0 1", "SOUND", true)]
    [InlineData("TEXT 10 20 0 1", "NAME", false, true)]
    [InlineData("TEXT 10 20 0 1", "SOUND", false, true)]
    public void RawControlsKeepArgumentsAndOriginalTextIndices(string control, string? function = null, bool explicitCall = false, bool useTry = false)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        // Match Program.EngineWiring: priority dispatch checks that a named
        // function exists before bypassing the subject's native command.
        stack.Interpreter.FunctionLookup = stack.Runner.HasFunction;
        string path = Path.Combine(Path.GetTempPath(), $"dialog-raw-{Guid.NewGuid():N}.scp");
        try
        {
            string dynamicLine = function == null ? "DTEXT 1 1 0 Dynamic"
                : $"{(explicitCall ? "CALL " : useTry ? "TRY " : "")}{function} Dynamic";
            string helper = function == null ? "" : $"[FUNCTION {function}]\nDTEXT 1 1 0 <ARGV[0]>\nRETURN 1\n";
            File.WriteAllText(path, $"[DIALOG d_raw_control]\n0,0\n{dynamicLine}\n{control}\n[DIALOG d_raw_control TEXT]\nFirst\nSecond\n{helper}");
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19522);
            TestHarness.AttachCharacter(client, world.CreateCharacter());
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_raw_control", 0));
            byte[] packet = TestHarness.GetQueuedPackets(client.NetState).Last(p => p.Span[0] is 0xDD or 0xB0).Span.ToArray();
            byte[] layout, texts;
            if (packet[0] == 0xDD)
            {
                byte[] Inflate(int offset, int length)
                {
                    using var input = new MemoryStream(packet, offset, length);
                    using var zip = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zip.CopyTo(output);
                    return output.ToArray();
                }
                int length = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(19, 4)) - 4;
                layout = Inflate(27, length);
                int header = 27 + length;
                texts = Inflate(header + 12, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(header + 4, 4)) - 4);
            }
            else
            {
                int length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(19, 2));
                layout = packet[21..(21 + length)];
                texts = packet[(23 + length)..];
            }
            string controls = Encoding.ASCII.GetString(layout);
            Assert.Contains(control.ToLowerInvariant(), controls.ToLowerInvariant());
            Assert.Contains("text 1 1 0 2", controls);
            var actualTexts = new List<string>();
            for (int offset = 0; offset < texts.Length;)
            {
                int bytes = 2 * BinaryPrimitives.ReadUInt16BigEndian(texts.AsSpan(offset, 2));
                offset += 2;
                actualTexts.Add(Encoding.BigEndianUnicode.GetString(texts, offset, bytes));
                offset += bytes;
            }
            Assert.Equal(new[] { "First", "Second", "Dynamic" }, actualTexts);
        }
        finally { File.Delete(path); }
    }
}
