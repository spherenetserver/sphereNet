using System.Globalization;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>A single click on an item in a player vendor's backpack shows its price.
///
/// The clicker's @ItemClick event asks <c>&lt;act.topobj.id&gt; == c_player_vendor</c>
/// and answers with <c>act.message_white [... Gold]</c>, a [FUNCTION] that runs MESSAGEUA
/// on the item. A character's ID is the defname of its CHARDEF (CHC_ID, CChar.cpp:2895:
/// ResourceGetName(pCharDef->GetResourceID())). The vendor is a named chardef built on
/// the c_man body, and its ID read as the body number ("0190"), so the comparison never
/// matched: the whole price branch was skipped and the click showed only the name.
///
/// The synthetic half carries the pack's lines and runs on CI; the real-data half replays
/// the report on the Sphere 56T custom-version script pack and save through the real
/// single-click handler and skips cleanly when the data is absent.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class VendorItemClickPriceTests
{
    private const string ScriptsDir = @"C:\56T\scripts";
    private const string SaveDir = @"C:\56T\save";
    private readonly ITestOutputHelper _out;

    public VendorItemClickPriceTests(ITestOutputHelper output) => _out = output;

    private const string Script =
        "[CHARDEF 0190]\r\nDEFNAME=c_man\r\nNAME=Man\r\n\r\n" +
        "[CHARDEF c_player_vendor]\r\nDEFNAME=c_player_vendor\r\nNAME=Vendor\r\nID=c_man\r\n\r\n" +
        "[FUNCTION message_white]\r\nmessageua 1153,6,6,0 <args>\r\n\r\n" +
        "[FUNCTION message_red]\r\nmessageua 38,6,6,0 <args>\r\n\r\n" +
        "[FUNCTION f_decimalseperator]\r\n" +
        "if (<eval strlen(<args>)> > 3)\r\n" +
        "   local.currentdigit=<eval strlen(<args>)>\r\n" +
        "   while (<local.currentdigit> > 3)\r\n" +
        "      args=<strsub 0 <eval (<local.currentdigit> - 3)> <args>>,<strsub <eval (<local.currentdigit> - 3)> 50 <args>>\r\n" +
        "      local.currentdigit -= 3\r\n" +
        "   endwhile\r\n" +
        "endif\r\n" +
        "return <args>\r\n\r\n" +
        "[EVENTS e_click_price]\r\n" +
        "On=@ItemClick\r\n" +
        "if (<act.topobj.id> == c_player_vendor)\r\n" +
        "\tif (strmatch('<act.price>',''))\r\n" +
        "\t\tact.message_Red [satilamaz]\r\n" +
        "\telseif (<act.price> <= 0)\r\n" +
        "\t\tact.message_red [satilik degil]\r\n" +
        "\telse\r\n" +
        "\t\tact.message_white [<f_decimalseperator <act.price>> Gold]\r\n" +
        "\tendif\r\n" +
        "endif\r\n";

    private static List<(uint Serial, string Text)> Speech(SphereNet.Game.Clients.GameClient client) =>
        TestHarness.GetQueuedPackets(client.NetState)
            .Where(p => p.Span[0] == 0xAE)
            .Select(p => ((uint)((p.Span[3] << 24) | (p.Span[4] << 16) | (p.Span[5] << 8) | p.Span[6]),
                System.Text.Encoding.BigEndianUnicode.GetString(p.Span[48..].ToArray()).TrimEnd('\0')))
            .ToList();

    /// <summary>The host wiring for an unknown verb reached through a reference head
    /// (Program.EngineWiring: ObjBase.RunScriptFunction) - ACT.message_white runs the
    /// [FUNCTION] on the item.</summary>
    private static void WireFunctions(ScriptRuntimeStack stack) =>
        ObjBase.RunScriptFunction = (obj, name, args, console) =>
        {
            var fnArgs = new SphereNet.Scripting.Execution.TriggerArgs { Source = console?.GetSourceChar() };
            fnArgs.InitFromRaw(args);
            return stack.Runner.TryRunFunction(name, obj, console, fnArgs, out _);
        };

    private static ScriptRuntimeStack LoadSynthetic()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string script = Path.Combine(Path.GetTempPath(), $"vendor_click_{Guid.NewGuid():N}.scp");
        File.WriteAllText(script, Script);
        try { stack.Resources.LoadResourceFile(script); }
        finally { File.Delete(script); }
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        return stack;
    }

    [Fact]
    public void ACharactersIdIsItsChardefDefname()
    {
        var stack = LoadSynthetic();
        var world = TestHarness.CreateWorld();

        var vendor = world.CreateCharacter();
        Assert.True(CharDefHelper.TryApplyDefName(vendor, "c_player_vendor", stack.Resources, refresh: false));
        Assert.Equal(0x0190, vendor.BodyId);
        Assert.True(vendor.TryGetProperty("ID", out string id));
        Assert.Equal("c_player_vendor", id, ignoreCase: true);
        Assert.True(vendor.TryGetProperty("BASEID", out string baseId));
        Assert.Equal("c_player_vendor", baseId, ignoreCase: true);

        var man = world.CreateCharacter();
        man.BodyId = 0x0190;
        man.BaseId = 0x0190;
        Assert.True(man.TryGetProperty("ID", out id));
        Assert.Equal("c_man", id, ignoreCase: true);
    }

    [Theory]
    [InlineData("400000", "[400,000 Gold]")]
    [InlineData("1500", "[1,500 Gold]")]
    [InlineData("0", "[satilik degil]")]
    public void AClickOnAVendorsItemShowsItsPrice(string price, string expected)
    {
        using var lf = LoggerFactory.Create(b => { });
        var stack = LoadSynthetic();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 19795);
        client.SetEngines(triggerDispatcher: stack.Dispatcher);
        WireFunctions(stack);

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(me, new Point3D(1000, 1000, 0, 0));
        TestHarness.AttachCharacter(client, me);
        me.Events.Add(stack.Resources.ResolveDefName("e_click_price"));

        var vendor = world.CreateCharacter();
        Assert.True(CharDefHelper.TryApplyDefName(vendor, "c_player_vendor", stack.Resources, refresh: false));
        vendor.IsPlayer = false;
        world.PlaceCharacter(vendor, new Point3D(1001, 1000, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        vendor.Equip(pack, Layer.Pack);
        // The item sits in a box inside the pack, as the report's did: TOPOBJ is the vendor.
        var box = world.CreateItem();
        box.BaseId = 0x0990;
        box.ItemType = ItemType.Container;
        pack.AddItem(box);
        var deed = world.CreateItem();
        deed.BaseId = 0x1644;
        box.AddItem(deed);
        Assert.True(deed.TrySetProperty("PRICE", price));

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleSingleClick(deed.Uid.Value);
        var said = Speech(client);
        foreach (var s in said)
            _out.WriteLine($"0x{s.Serial:X8}: {s.Text}");
        Assert.Contains(said, s => s.Serial == deed.Uid.Value && s.Text == expected);
    }

    /// <summary>The reported case on the real pack and save: a staff character with the
    /// pack's staff character events single-clicks a priced item in a player vendor's
    /// backpack, and the price goes over the item to the clicker.</summary>
    [Fact]
    public void ARealVendorsPricedItemShowsItsPriceOnClick()
    {
        if (Gate.Missing(_out, "56T scripts and save",
                !Directory.Exists(ScriptsDir) || !File.Exists(Path.Combine(SaveDir, "sphereworld.scp")))) return;

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        foreach (var f in Directory.EnumerateFiles(ScriptsDir, "*.scp", SearchOption.AllDirectories))
            stack.Resources.LoadResourceFile(f);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        var resources = stack.Resources;
        var staffEvents = resources.ResolveDefName("e_char_staff");
        Assert.True(staffEvents.IsValid);

        using var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        world.InitMap(0, 7168, 4096);
        var loader = new SphereNet.Persistence.Load.WorldLoader(lf);
        loader.ApplyCharDefFromName = (ch, defname) => CharDefHelper.TryApplyDefName(ch, defname, resources);
        loader.ResolveBodyFromCharDefIndex = idx => CharDefHelper.ResolveBodyId(idx, resources);
        loader.ResolveItemDef = defname =>
        {
            var rid = resources.ResolveDefName(defname);
            if (rid.IsValid && rid.Type == ResType.ItemDef)
            {
                var def = DefinitionLoader.GetItemDef(rid.Index);
                return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            }
            return 0;
        };
        loader.ResolveItemDefFullIndex = defname =>
        {
            var rid = resources.ResolveDefName(defname);
            return rid.IsValid && rid.Type == ResType.ItemDef ? rid.Index : 0;
        };
        loader.ResolveCharDef = defname =>
        {
            int idx = CharDefHelper.ResolveDefIndex(defname, resources);
            return idx != 0 ? CharDefHelper.ResolveBodyId(idx, resources) : (ushort)0;
        };
        Item.ResolveDefName = defname =>
        {
            var rid = resources.ResolveDefName(defname);
            return rid.IsValid && rid.Type == ResType.ItemDef ? (ushort)rid.Index : (ushort)0;
        };
        loader.Load(world, SaveDir);
        world.RebuildContainerIndex();

        // A player vendor whose backpack holds a priced item, at any depth.
        Item? priced = null;
        Character? vendor = null;
        foreach (var ch in world.GetAllCharactersSnapshot())
        {
            if (ch.IsPlayer || !ch.TryGetProperty("ID", out string chId) ||
                !chId.Equals("c_player_vendor", StringComparison.OrdinalIgnoreCase))
                continue;
            var pack = ch.Backpack ?? ch.GetEquippedItem(Layer.Pack);
            if (pack == null) continue;
            priced = FindPriced(pack);
            if (priced != null) { vendor = ch; break; }
        }
        Assert.NotNull(vendor);
        Assert.NotNull(priced);
        Assert.True(priced!.TryGetProperty("PRICE", out string priceText));
        long price = long.Parse(priceText, CultureInfo.InvariantCulture);
        _out.WriteLine($"vendor 0x{vendor!.Uid.Value:X} '{vendor.Name}', item 0x{priced.Uid.Value:X8} price {price}");

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(me, new Point3D((short)(vendor.X + 1), vendor.Y, vendor.Z, vendor.MapIndex));
        me.Events.Add(staffEvents);
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 19796);
        TestHarness.AttachCharacter(client, me);
        client.SetEngines(triggerDispatcher: stack.Dispatcher);
        WireFunctions(stack);

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleSingleClick(priced.Uid.Value);
        var said = Speech(client);
        foreach (var s in said)
            _out.WriteLine($"0x{s.Serial:X8}: {s.Text}");
        string expected = $"[{price.ToString("#,0", CultureInfo.InvariantCulture)} Gold]";
        Assert.Contains(said, s => s.Serial == priced.Uid.Value && s.Text == expected);
    }

    private static Item? FindPriced(Item container)
    {
        foreach (var inner in container.Contents)
        {
            if (inner.TryGetProperty("PRICE", out string p) &&
                long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v > 0)
                return inner;
            if (inner.Contents.Count > 0 && FindPriced(inner) is { } deeper)
                return deeper;
        }
        return null;
    }
}
