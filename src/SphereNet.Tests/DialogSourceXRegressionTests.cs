using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Scripting.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogSourceXRegressionTests(ITestOutputHelper output)
{
    [Fact]
    public void DynamicHtmlPreservesBreaksAndFormattingAfterLocalConcatenation()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-html-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [DEFNAME html]
            BR <br>
            BFONT_WHITE <basefont color="#ffffff">
            [DIALOG d_html_probe]
            0,0
            LOCAL.LINES = Sunucu yasi: 10<DEF.BR>
            LOCAL.LINES .= Son yedekleme: 20<DEF.BR>
            LOCAL.LINES .= Oyuncu: 3<DEF.BR>
            DHTMLGUMP 10 10 250 180 0 1 <DEF.BFONT_WHITE><LOCAL.LINES>
            DTEXT 10 200 0 <LOCAL.LINES>
            DCROPPEDTEXT 10 220 250 20 0 <LOCAL.LINES>
            DTEXTENTRY 10 240 250 20 0 1 <LOCAL.LINES>
            DTEXTENTRYLIMITED 10 260 250 20 0 2 200 <LOCAL.LINES>
            HTMLGUMP 10 280 250 20 0 0 1
            [DIALOG d_html_probe TEXT]
            <DEF.BFONT_WHITE>Indexed<DEF.BR>Text
            """);
        try
        {
            stack.Resources.LoadResourceFile(path);
            stack.Interpreter.ServerPropertyResolver = p =>
                p.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) && stack.Resources.TryGetDefValue(p[4..], out var value) ? value : null;
            var world = TestHarness.CreateWorld();
            using var lf = LoggerFactory.Create(_ => { });
            var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 19434);
            var me = world.CreateCharacter();
            TestHarness.AttachCharacter(client, me);
            client.SetEngines(commands: new SphereNet.Game.Speech.CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_html_probe", 0));
            byte[] packet = TestHarness.GetQueuedPackets(client.NetState)
                .Last(p => p.Span[0] == 0xDD || p.Span[0] == 0xB0).Span.ToArray();
            byte[] textBytes;
            if (packet[0] == 0xDD)
            {
                int layoutLength = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(19, 4)) - 4;
                int textHeader = 27 + layoutLength;
                int textLength = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(textHeader + 4, 4)) - 4;
                using var input = new MemoryStream(packet, textHeader + 12, textLength);
                using var z = new ZLibStream(input, CompressionMode.Decompress);
                using var data = new MemoryStream();
                z.CopyTo(data);
                textBytes = data.ToArray();
            }
            else
            {
                int layoutLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(19, 2));
                textBytes = packet[(23 + layoutLength)..];
            }
            string text = Encoding.BigEndianUnicode.GetString(textBytes);
            output.WriteLine(text);
            Assert.Contains("<basefont color=\"#ffffff\">Sunucu yasi: 10<br>Son yedekleme: 20<br>Oyuncu: 3<br>", text);
            Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(text, "Sunucu yasi: 10<br>Son yedekleme: 20<br>Oyuncu: 3<br>").Count);
            Assert.Contains("<basefont color=\"#ffffff\">Indexed<br>Text", text);
        }
        finally { File.Delete(path); }
    }

    private static SphereNet.Scripting.Resources.ResourceHolder Resources(ILoggerFactory lf, string script)
    {
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(lf.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string path = Path.Combine(Path.GetTempPath(), $"dialog-parity-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, script);
        try { resources.LoadResourceFile(path); }
        finally { File.Delete(path); }
        return resources;
    }

    [Fact]
    public void ForInstancesMustMatchNamedDefinitionInsteadOfGraphic()
    {
        using var lf = LoggerFactory.Create(_ => { });
        var resources = Resources(lf, """
            [ITEMDEF i_gmpage]
            ID=01ea7
            [ITEMDEF i_other_memory]
            ID=01ea7
            [CHARDEF c_dialog_npc]
            ID=0190
            """);
        var rid = resources.ResolveDefName("i_gmpage");
        Assert.True(rid.IsValid);
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 19433);
        var me = world.CreateCharacter();
        TestHarness.AttachCharacter(client, me);
        client.SetEngines(commands: new SphereNet.Game.Speech.CommandHandler { Resources = resources });
        var item = world.CreateItem();
        item.BaseId = 0x1EA7;
        item.SetTag("SCRIPTDEF", rid.Index.ToString());
        var other = world.CreateItem();
        other.BaseId = item.BaseId;
        other.SetTag("SCRIPTDEF", resources.ResolveDefName("i_other_memory").Index.ToString());
        var truncated = world.CreateItem();
        truncated.BaseId = (ushort)rid.Index;
        var matches = client.QueryScriptObjects("FORINSTANCES", me, "i_gmpage", null);
        output.WriteLine($"i_gmpage definition={rid.Index:X}, graphic={item.BaseId:X}, matches={matches.Count}");
        Assert.Same(item, Assert.Single(matches));
        var npc = world.CreateCharacter();
        npc.BaseId = 0x190;
        npc.CharDefIndex = resources.ResolveDefName("c_dialog_npc").Index;
        var otherNpc = world.CreateCharacter();
        otherNpc.BaseId = npc.BaseId;
        Assert.Same(npc, Assert.Single(client.QueryScriptObjects("FORINSTANCES", me, "c_dialog_npc", null)));
        Assert.Empty(client.QueryScriptObjects("FORINSTANCES", me, "i_missing", null));
    }

    [Fact]
    public void AbsoluteCoordinatesMustNotChangeOrigin()
    {
        var method = typeof(ClientDialogHandler).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "ResolveDialogCoord" && m.GetParameters().Length == 3);
        object[] args = ["10", 20, 20];
        method.Invoke(null, args);
        args[0] = "+0";
        var actual = (int)method.Invoke(null, args)!;
        output.WriteLine($"Source-X origin=20, absolute=10, then +0: expected 20, actual {actual}");
        Assert.Equal(20, actual);
        args[0] = "*5";
        Assert.Equal(25, method.Invoke(null, args));
        args[0] = "-2";
        Assert.Equal(23, method.Invoke(null, args));
        args[0] = "+0";
        Assert.Equal(25, method.Invoke(null, args));
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(1, 1, 2)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 3)]
    public void RequestedPageMustRemapPageAndNavigation(int requestedPage, int firstPage, int secondPage)
    {
        var world = TestHarness.CreateWorld();
        using var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 19432);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        var resources = Resources(lf, "[DIALOG d_audit]\n0,0\n");
        client.SetEngines(commands: new SphereNet.Game.Speech.CommandHandler { Resources = resources });
        var section = new ScriptSection("DIALOG", "d_audit", new ScriptContext());
        section.Keys.AddRange(new[] {
            new ScriptKey("0,0", ""), new ScriptKey("PAGE", "0"),
            new ScriptKey("BUTTON", "0 0 4006 4007 0 2 0"),
            new ScriptKey("BUTTONTILEART", "0 0 4006 4007 0 2 0 1 0 0 0"),
            new ScriptKey("DORIGIN", "20 40"),
            new ScriptKey("RESIZEPIC", "10 10 9200 100 100"),
            new ScriptKey("DTEXT", "+0 +0 0 Common"),
            new ScriptKey("PAGE", "1"), new ScriptKey("DTEXT", "10 10 0 First"),
            new ScriptKey("PAGE", "2"), new ScriptKey("DTEXT", "20 20 0 Second") });
        var handler = typeof(GameClient).GetProperty("Dialogs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);
        typeof(ClientDialogHandler).GetMethod("RenderScriptDialog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(handler, ["d_audit", requestedPage, section, Serial.Invalid, null, null]);
        byte[] packet = TestHarness.GetQueuedPackets(client.NetState)
            .Last(p => p.Span[0] == 0xDD || p.Span[0] == 0xB0).Span.ToArray();
        string layout;
        if (packet[0] == 0xDD)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(19, 4)) - 4;
            using var input = new MemoryStream(packet, 27, length);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(z, Encoding.ASCII);
            layout = reader.ReadToEnd();
        }
        else
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(19, 2));
            layout = Encoding.ASCII.GetString(packet, 21, length);
        }
        output.WriteLine(layout);
        Assert.Contains($"button 0 0 4006 4007 0 {secondPage} 0", layout);
        Assert.Contains($"buttontileart 0 0 4006 4007 0 {secondPage} 0", layout);
        Assert.Contains($"{{ page {firstPage} }}{{ text 10 10 0 1 }}", layout);
        Assert.Contains($"{{ page {secondPage} }}{{ text 20 20 0 2 }}", layout);
        Assert.Contains("text 20 40 0 0", layout);
    }
}
