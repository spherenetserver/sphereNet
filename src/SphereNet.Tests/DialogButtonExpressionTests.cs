using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogButtonExpressionTests
{
    [Theory]
    [InlineData("BUTTON (10 + 5) (20 + 6) button_down button_up 1 0 (7 + 2)", "button 15 26 4005 4007 1 0 9")]
    [InlineData("BUTTON +(10 + 5) *(20 + 6) 4005 4007 1 0 9", "button 115 226 4005 4007 1 0 9")]
    [InlineData("BUTTON 10+5 4005 4007 1 0 9", "button 10 205 4005 4007 1 0 9")]
    [InlineData("BUTTON - - 4005 4007 1 0 button_id", "button 100 200 4005 4007 1 0 9")]
    [InlineData("BUTTON 010,020,4005,4007,1,0,9", "button 16 32 4005 4007 1 0 9")]
    [InlineData("BUTTON *(5 + 5) *(10 + 5) 4005 4007 1 0 8\nBUTTON +0 +0 4005 4007 1 0 9", "button 110 215 4005 4007 1 0 9")]
    [InlineData("BUTTONTILEART 10 20 button_down button_up 1 0 button_id (01000 + 1) (020 + 2) (3 + 1) (4 + 1)", "buttontileart 10 20 4005 4007 1 0 9 4097 34 4 5")]
    [InlineData("RESIZEPIC (10 + 5) (20 + 6) button_down (100 + 20) (40 + 5)", "resizepic 15 26 4005 120 45")]
    [InlineData("GUMPPICTILED (10 + 5) (20 + 6) (100 + 20) (40 + 5) button_down", "gumppictiled 15 26 120 45 4005")]
    [InlineData("CHECKERTRANS +(10 + 5) *(20 + 6) (100 + 20) (40 + 5)", "checkertrans 115 226 120 45")]
    [InlineData("DTEXT (10 + 5) (20 + 6) button_id Hello world", "text 15 26 9 0")]
    [InlineData("DCROPPEDTEXT (10 + 5) (20 + 6) (100 + 20) (40 + 5) button_id Hello world", "croppedtext 15 26 120 45 9 0")]
    [InlineData("DHTMLGUMP (10 + 5) (20 + 6) (100 + 20) (40 + 5) 1 0 Hello world", "htmlgump 15 26 120 45 0 1 0")]
    [InlineData("DTEXT (10 + 5) (20 + 6) button_id .Hello world", "text 15 26 9 0", "Hello world")]
    [InlineData("DCROPPEDTEXT 10 20 120 45 button_id ..Hello world", "croppedtext 10 20 120 45 9 0", ".Hello world")]
    [InlineData("DHTMLGUMP 10 20 (100 + 20) 45 1 0 <TAG.MARKUP>", "htmlgump 10 20 120 45 0 1 0", "First<br>Second")]
    [InlineData("DTEXTENTRY (10 + 5) (20 + 6) (100 + 20) (40 + 5) button_id (7 + 2) Hello world", "textentry 15 26 120 45 9 9 0", "Hello world")]
    [InlineData("DTEXTENTRYLIMITED (10 + 5) (20 + 6) (100 + 20) (40 + 5) button_id (7 + 2) (30 + 2) Hello world", "textentrylimited 15 26 120 45 9 9 0 32", "Hello world")]
    [InlineData("DTEXTENTRY 10 20 120 45 9 9 .Hello world", "textentry 10 20 120 45 9 9 0", ".Hello world")]
    [InlineData("DTEXTENTRYLIMITED 10 20 120 45 9 9 32", "textentrylimited 10 20 120 45 9 9 0 32", "")]
    [InlineData("CHECKBOX +(10 + 5) *(20 + 6) button_down button_up (1 - 1) button_id", "checkbox 115 226 4005 4007 0 9")]
    [InlineData("RADIO (10 + 5) (20 + 6) button_down button_up (2 - 1) button_id", "radio 15 26 4005 4007 1 9")]
    [InlineData("GUMPPIC (10 + 5) (20 + 6) button_down", "gumppic 15 26 4005")]
    [InlineData("GUMPPIC 10 20 4005 010", "gumppic 10 20 4005 hue=010")]
    [InlineData("GUMPPIC 10 20 4005 0", "gumppic 10 20 4005 hue=0")]
    [InlineData("TILEPIC +(10 + 5) *(20 + 6) (01000 + 1)", "tilepic 115 226 4097")]
    [InlineData("TILEPICHUE (10 + 5) (20 + 6) (01000 + 1) 010", "tilepichue 15 26 4097 010")]
    [InlineData("TILEPICHUE 10 20 4097", "tilepichue 10 20 4097")]
    [InlineData("PICINPIC (10 + 5) (20 + 6) button_down (100 + 20) (40 + 5) (2 + 1) (3 + 1)", "picinpic 15 26 4005 120 45 3 4")]
    [InlineData("XMFHTMLGUMP (10 + 5) (20 + 6) (100 + 20) (40 + 5) (1000000 + 1) 1 0", "xmfhtmlgump 15 26 120 45 1000001 1 0")]
    [InlineData("XMFHTMLGUMPCOLOR 10 20 120 45 1000001 1 0 010", "xmfhtmlgumpcolor 10 20 120 45 1000001 1 0  010")]
    [InlineData("XMFHTMLGUMPCOLOR 10 20 120 45 1000001 1 0", "xmfhtmlgumpcolor 10 20 120 45 1000001 1 0")]
    [InlineData("XMFHTMLTOK (10 + 5) (20 + 6) (100 + 20) (40 + 5) 1 0 button_id (1000000 + 1) @First value\tSecond value", "xmfhtmltok 15 26 120 45 1 0 9 1000001 @First value\tSecond value")]
    [InlineData("XMFHTMLGUMP 10 20 120 45 1000001 2 3", "xmfhtmlgump 10 20 120 45 1000001 2 3")]
    [InlineData("XMFHTMLTOK 10 20 120 45 2 3 9 1000001", "xmfhtmltok 10 20 120 45 2 3 9 1000001")]
    [InlineData("PAGE button_id", "page 9")]
    [InlineData("PAGE (2 + 3)", "page 5")]
    [InlineData("PAGE 2+3", "page 2")]
    [InlineData("DORIGIN (10 + 5) (20 + 6)\nBUTTON +0 +0 4005 4007 1 0 9", "button 15 26 4005 4007 1 0 9")]
    [InlineData("DORIGIN button_id button_down\nBUTTON +0 +0 4005 4007 1 0 9", "button 9 4005 4005 4007 1 0 9")]
    [InlineData("DORIGIN *(10 + 5) -\nBUTTON +0 +0 4005 4007 1 0 9", "button 115 200 4005 4007 1 0 9")]
    [InlineData("DORIGIN +(10 + 5) -(20 + 6)\nBUTTON +0 +0 4005 4007 1 0 9", "button 15 -26 4005 4007 1 0 9")]
    [InlineData("TOOLTIP 1000001,@First value\tSecond value", "tooltip 1000001 @First value\tSecond value")]
    [InlineData("TOOLTIP 1000001 = @Value", "tooltip 1000001 @Value")]
    [InlineData("TOOLTIP 4294967295 @Value", "tooltip 4294967295 @Value")]
    [InlineData("TOOLTIP 010 @Value", "tooltip 16 @Value")]
    [InlineData("TOOLTIP 123tail @Value", "tooltip 123 @Value")]
    [InlineData("TOOLTIP 100A @Value", "tooltip 1610 @Value")]
    [InlineData("TOOLTIP 1000001,", "tooltip 1000001")]
    [InlineData("BUTTON 1 1 4005 4007 1 0 9\nTOOLTIP 1000001", "tooltip ", null, true)]
    [InlineData("BUTTON 1 1 4005 4007 1 0 9\nTOOLTIP unknown_id @Value", "tooltip ", null, true)]
    [InlineData("BUTTON 1 1 4005 4007 1 0 9\nTOOLTIP -1 @Value", "tooltip ", null, true)]
    [InlineData("BUTTON 1 1 4005 4007 1 0 9\nTOOLTIP 4294967296 @Value", "tooltip ", null, true)]
    [InlineData("DHTMLGUMP 10 20 120 45 2 3 Options", "htmlgump 10 20 120 45 0 2 3", "Options")]
    [InlineData("DHTMLGUMP 10 20 120 45 (1 + 1) (2 + 1) Options", "htmlgump 10 20 120 45 0 2 3", "Options")]
    [InlineData("CHECKBOX 10 20 4005 4007 2 9", "checkbox 10 20 4005 4007 2 9")]
    [InlineData("RADIO 10 20 4005 4007 3 9", "radio 10 20 4005 4007 3 9")]
    [InlineData("DTEXTENTRYLIMITED 10 20 120 45 9 7 -1 Value", "textentrylimited 10 20 120 45 9 7 0 -1", "Value")]
    [InlineData("DTEXTENTRYLIMITED 10 20 120 45 9 7 0 Value", "textentrylimited 10 20 120 45 9 7 0 0", "Value")]
    public void ControlOperandsFollowSourceXGetSingle(string control, string expected, string? expectedText = null, bool absent = false)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-button-expression-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, $"[DEFNAME button_expression_test]\nbutton_down=4005\nbutton_up=4007\nbutton_id=9\n[DIALOG d_button_expression]\n0,0\nDORIGIN 100 200\n{control}\n");
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19521);
            var player = world.CreateCharacter();
            player.SetTag("MARKUP", "First<br>Second");
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_button_expression", 0));
            byte[] packet = TestHarness.GetQueuedPackets(client.NetState).Last(p => p.Span[0] is 0xDD or 0xB0).Span.ToArray();
            string layout;
            if (packet[0] == 0xDD)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(19, 4)) - 4;
                using var input = new MemoryStream(packet, 27, length);
                using var zip = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zip.CopyTo(output);
                layout = Encoding.ASCII.GetString(output.ToArray());
            }
            else layout = Encoding.ASCII.GetString(packet, 21, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(19, 2)));
            if (absent) Assert.DoesNotContain(expected, layout);
            else Assert.Contains(expected, layout);
            if (expectedText != null)
            {
                byte[] texts;
                if (packet[0] == 0xDD)
                {
                    int header = 23 + BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(19, 4));
                    int length = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(header + 4, 4)) - 4;
                    using var input = new MemoryStream(packet, header + 12, length);
                    using var zip = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zip.CopyTo(output);
                    texts = output.ToArray();
                }
                else texts = packet[(23 + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(19, 2)))..];
                int characters = BinaryPrimitives.ReadUInt16BigEndian(texts);
                Assert.Equal(expectedText, Encoding.BigEndianUnicode.GetString(texts, 2, characters * 2));
            }
        }
        finally { File.Delete(path); }
    }
}
