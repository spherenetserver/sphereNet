using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A help-gump "stuck" teleport built from script items: the button runs a function that
/// wears a cooldown item and a counting item on LAYER_SPECIAL, freezes the wearer and adds
/// an event that refuses double-clicks and skills; the counting item's @Timer re-arms
/// itself once a second and, on the thirtieth, teleports the wearer to the named city,
/// clears the freeze, removes the event and deletes itself.
///
/// The live report was a character left with the event for good - unable to double-click
/// anything or cast - after pressing the button. Three engine faults met there:
///
///  * The counting item is "ID=i_memory / TYPE=t_eq_script". Wearing it on LAYER_SPECIAL
///    retyped it to a memory object, and the character tick's memory sweep deletes an
///    expired memory with no link - so the item vanished on its first second, @Timer never
///    ran, and nothing ever removed the event. Upstream keeps the type (LayerAdd,
///    CCharAct.cpp:301) and only hands IT_EQ_MEMORY_OBJ to Memory_OnTick
///    (CChar::OnTickEquip, CCharAct.cpp:4082-4097).
///  * Drawing the dialog ran the stuck function itself, with no city: a helper returned
///    the tab label "Stuck", the RETURN was read as a number, the bare word resolved to
///    [FUNCTION f_stuck] through an invented "f_" name prefix, and it ran. Upstream looks
///    a function up by its exact name (r_GetFunctionIndex, CScriptObj.cpp:207) and a
///    RETURN's GetArgVal never calls one (CExpression::GetSingle, CExpression.cpp:1230).
///  * "TOPOBJ.FLAGS ..." written on a character set nothing, so the function's freeze did
///    not take; and the cooldown item, which has no layer of its own, was not worn at all
///    (CanEquipLayer puts IT_EQ_SCRIPT on LAYER_SPECIAL, CCharStatus.cpp:360-367).
///
/// The synthetic half carries the sections the flow needs; the real-data halves replay it
/// on the Sphere 56T custom-version pack and resume a stuck character from its save, and
/// skip cleanly when that data is absent.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class StuckMemoryScriptTests : IDisposable
{
    private const string Pack56T = @"C:\56T\scripts";
    private const string Save56T = @"C:\56T\save";
    private const string Nl = "\r\n";

    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_stuck_" + Guid.NewGuid().ToString("N"));
    private readonly object? _prevWorld;
    private readonly object? _prevResources;
    private static readonly Type ProgramType = typeof(SphereNet.Server.Program);
    private static readonly FieldInfo WorldField =
        ProgramType.GetField("_world", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ResourcesField =
        ProgramType.GetField("_resources", BindingFlags.Static | BindingFlags.NonPublic)!;

    public StuckMemoryScriptTests(ITestOutputHelper output)
    {
        _out = output;
        _prevWorld = WorldField.GetValue(null);
        _prevResources = ResourcesField.GetValue(null);
    }

    public void Dispose()
    {
        WorldField.SetValue(null, _prevWorld);
        ResourcesField.SetValue(null, _prevResources);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // The sections the flow touches, as the pack writes them (the item and event bodies
    // verbatim; the help dialog cut down to its tab loop and the stuck page's buttons).
    private static readonly string SyntheticPack = string.Join(Nl,
        "[ITEMDEF 02007]",
        "DEFNAME=i_memory",
        "TYPE=t_eq_memory_obj",
        "LAYER=layer_special",
        "",
        "[ITEMDEF 0e76]",
        "DEFNAME=i_bag",
        "TYPE=t_container",
        "",
        "[defname help_page]",
        "help_type_en Announces, Help, Stuck, Statistics",
        "help_stuck Britain, Minoc, Moonglow, Delucia",
        "stuck_tile 130,133,136,139",
        "",
        "[FUNCTION f_array]",
        "local.temp = <argv[<eval <argv> - 1>]> -1",
        "return <argv[<dlocal.temp>]>",
        "",
        "[DIALOG d_helppage]",
        "50,50",
        "PAGE 0",
        "dorigin 44 220",
        "for 1 4",
        "\tlocal.page <f_array <def.help_type_en>,<dlocal._for>>",
        "\tbutton +0 *32 349 350 1 0 <dlocal._for>",
        "\tdtext 70 +3 0 <local.page>",
        "endfor",
        "PAGE 3 // STUCK",
        "dorigin 160 100",
        "for i 1 4",
        "\tbutton *0 *90 <f_array <def.stuck_tile>,<dlocal.i>> <eval <f_array <def.stuck_tile>,<dlocal.i>>+1> 1 0 <eval <dlocal.i>+30>",
        "endfor",
        "",
        "[DIALOG d_helppage button]",
        "on=30 34 // STUCK",
        "ref1 <src.findid.i_stuck_timer>",
        "if !(<ref1>)",
        "\tdialogclose d_item_store",
        "\tlocal.city <f_array <def.help_stuck>,<eval <argn>-30>>",
        "\tsrc.tag.stuck_tile <f_array <def.stuck_tile>,<eval <argn>-30>>",
        "\tsrc.f_stuck <local.city>",
        "else",
        "\tsrc.sysmessage on cooldown",
        "endif",
        "",
        "[function f_stuck]",
        "serv.newitem i_stuck_timer",
        "new.equip",
        "topobj.flags <topobj.flags>|04",
        "src.say_white * Stucked *",
        "src.sys_white 30 saniye icinde <args> bolgesine ulasmis olacaksiniz",
        "src.events +e_stuck",
        "serv.newitem i_stuck",
        "new.name <args>",
        "new.equip",
        "",
        "[itemdef i_stuck]",
        "name Stuck item",
        "id i_memory",
        "weight 0",
        "type t_eq_script",
        "layer layer_special",
        "",
        "ON=@Create",
        "ATTR=attr_invis|attr_decay",
        "MORE1=0",
        "",
        "ON=@Equip",
        "TIMER=1",
        "",
        "ON=@Timer",
        "if (<topobj.isonline>)",
        "\tif (<more1> >= 30)",
        "\t\ttrysrc <topobj.uid> topobj.dialogclose d_stuck_install",
        "\t\ttopobj.go <name>",
        "\t\ttopobj.flags <topobj.flags>&~04",
        "\t\ttopobj.tag.stuck_tile",
        "\t\ttopobj.speedmode 0",
        "\t\ttopobj.events -e_stuck",
        "\t\tremove",
        "\telse",
        "\t\ttrysrc <topobj.uid> topobj.dialogclose d_stuck_install",
        "\t\ttrysrc <topobj.uid> topobj.dialog d_stuck_install",
        "\t\tmore1 += 1",
        "\t\ttimer 1",
        "\t\ttopobj.flags <topobj.flags>|04",
        "\t\ttopobj.speedmode 4",
        "\tendif",
        "else",
        "\tif (<more1> >= 30)",
        "\t\ttopobj.go <name>",
        "\t\ttopobj.flags <topobj.flags>&~04",
        "\t\ttopobj.tag.stuck_tile",
        "\t\ttopobj.speedmode 0",
        "\t\ttopobj.events -e_stuck",
        "\t\tremove",
        "\telse",
        "\t\tmore1 += 1",
        "\t\ttimer 1",
        "\t\ttopobj.flags <topobj.flags>|04",
        "\tendif",
        "endif",
        "return 1",
        "",
        "[dialog d_stuck_install]",
        "0,0",
        "nomove",
        "noclose",
        "local.more1 <eval <src.findid.i_stuck.more1>>",
        "gumppic +0 +0 365",
        "",
        "[itemdef i_stuck_timer]",
        "name=Stuck Hakki",
        "ID=i_memory",
        "WEIGHT=0",
        "TYPE=t_eq_script",
        "",
        "on=@create",
        "attr=attr_decay",
        "",
        "on=@equip",
        "timer 900",
        "",
        "on=@timer",
        "topobj.sysmessage cooldown over",
        "remove",
        "return 1",
        "",
        "[events e_stuck]",
        "on=@skillstart",
        "src.action -1",
        "return 1",
        "",
        "on=@itemdclick",
        "return 1",
        "",
        "[eof]",
        "");

    private sealed class Rig
    {
        public required ScriptRuntimeStack Stack;
        public required GameWorld World;
        public required GameClient Client;
        public required Character Me;
        public required SpellEngine Spells;
        public required Point3D Britain;
    }

    private Rig Build(IEnumerable<string> files, Action<GameWorld, ScriptRuntimeStack>? load = null, Character? me = null)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        foreach (var f in files)
            stack.Resources.LoadResourceFile(f);
        var registry = new SpellRegistry();
        new DefinitionLoader(stack.Resources, registry).LoadAll();
        var resources = stack.Resources;

        var world = TestHarness.CreateWorld();
        world.InitMap(0, 7168, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Character.ResolveCharByUid = world.FindChar;
        ResourcesField.SetValue(null, resources);
        WorldField.SetValue(null, world);

        // The server's script wiring (Program.EngineWiring): SERV.* through the server
        // resolver, NEW and the object reference heads, @Create / @Timer / script EQUIP.
        var resolve = ProgramType.GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        stack.Interpreter.ServerPropertyResolver = s => (string?)resolve.Invoke(null, [s]);
        stack.Interpreter.FunctionLookup = stack.Runner.HasFunction;
        stack.Interpreter.ResolveObjectRef = (obj, head) =>
            head.Equals("NEW", StringComparison.OrdinalIgnoreCase) ? world.FindObject(world.LastNewObject)
            : obj is ObjBase o ? o.ResolveScriptRefHead(head) : null;
        Item.CreateTriggerHook = it => stack.Dispatcher.FireItemTrigger(it, ItemTrigger.Create,
            new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = it });
        Item.OnTimerExpired = it => stack.Dispatcher.FireItemTrigger(it, ItemTrigger.Timer,
            new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = it });
        Character.ScriptEquipItem = (wearer, item) =>
        {
            var layer = item.EquipLayer;
            if (layer == Layer.None)
                layer = DefinitionLoader.GetItemDef(ItemDefHelper.ResolveInstanceDefIndex(item, resources))?.Layer ?? Layer.None;
            if (layer == Layer.None)
                layer = CharacterMemoryState.DefaultLayerFor(item);
            if (layer == Layer.None || !wearer.Equip(item, layer))
                return false;
            stack.Dispatcher.FireItemTrigger(item, ItemTrigger.Equip,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = wearer, ItemSrc = item });
            return true;
        };

        load?.Invoke(world, stack);

        var britain = new Point3D(1495, 1629, 10, 0);
        var region = new Region { Name = "Britain", MapIndex = 0, P = britain };
        region.AddRect(1400, 1500, 1600, 1750);
        world.AddRegion(region);

        if (me == null)
        {
            me = world.CreateCharacter();
            me.Name = "Tester";
            world.PlaceCharacter(me, new Point3D(1000, 1000, 0, 0));
        }
        me.IsPlayer = true;
        me.PrivLevel = PrivLevel.Player;
        if (me.Backpack == null)
        {
            var pack = world.CreateItem();
            pack.BaseId = 0x0E75;
            pack.ItemType = ItemType.Container;
            me.Equip(pack, Layer.Pack);
        }

        var heal = new SpellDef { Id = SpellType.Heal, ManaCost = 10, CastTimeBase = 5, Flags = SpellFlag.Heal | SpellFlag.TargChar };
        var spellRegistry = new SpellRegistry();
        spellRegistry.Register(heal);
        var spells = new SpellEngine(world, spellRegistry) { TriggerDispatcher = stack.Dispatcher };
        spells.OnSysMessage = (c, m) => _out.WriteLine("SYSMSG " + m);
        var book = world.CreateItem();
        book.BaseId = 0x0EFA;
        book.ItemType = ItemType.Spellbook;
        me.Backpack!.AddItem(book);
        book.TryLearnSpell((int)SpellType.Heal);
        me.MaxMana = me.Mana = 100;
        Character.MagicFlags = 0;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 19791);
        TestHarness.AttachCharacter(client, me);
        client.SetEngines(commands: new SphereNet.Game.Speech.CommandHandler { Resources = resources },
            spellEngine: spells, triggerDispatcher: stack.Dispatcher);
        me.MaxMana = me.Mana = 100;

        return new Rig { Stack = stack, World = world, Client = client, Me = me, Spells = spells, Britain = britain };
    }

    private Rig BuildSynthetic()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "stuck.scp");
        File.WriteAllText(file, SyntheticPack);
        return Build([file]);
    }

    private static Item? Worn(Character ch, string defname, ScriptRuntimeStack stack)
    {
        int index = stack.Resources.ResolveDefName(defname).Index;
        return ch.Memories.FirstOrDefault(m => !m.IsDeleted &&
            ItemDefHelper.ResolveInstanceDefIndex(m, stack.Resources) == index);
    }

    private static bool HasStuckEvent(Rig r) =>
        r.Me.Events.Contains(r.Stack.Resources.ResolveDefName("e_stuck"));

    /// <summary>One second of server time: the counting item's 1 s timer comes due, the
    /// wearer's own tick runs (where the memory sweep lives), then the world's item timers.
    /// The order is the one that lost the item: the character tick saw it first.</summary>
    private static void OneSecond(Rig r)
    {
        long now = Environment.TickCount64;
        foreach (var m in r.Me.Memories.ToList())
            if (m.Timeout > 0 && m.Timeout - now <= 1000)
                m.SetTimeout(now - 1);
        r.Me.OnTick();
        r.World.OnTick();
    }

    private static bool DClickOpens(Rig r)
    {
        var bag = r.World.CreateItem();
        bag.BaseId = 0x0E76;
        bag.ItemType = ItemType.Container;
        r.World.PlaceItem(bag, new Point3D((short)(r.Me.X + 1), r.Me.Y, r.Me.Z, r.Me.MapIndex));
        TestHarness.ClearQueuedPackets(r.Client.NetState);
        r.Client.HandleDoubleClick(bag.Uid.Value);
        return TestHarness.GetQueuedPackets(r.Client.NetState).Any(p => p.Span[0] == 0x24);
    }

    private bool CanCast(Rig r)
    {
        int started = r.Spells.CastStart(r.Me, SpellType.Heal, r.Me.Uid, r.Me.Position);
        _out.WriteLine($"cast: {started} {r.Spells.LastCastRefusal}");
        if (started > 0)
            r.Me.ClearCastState(notifyAbort: false);
        return started > 0;
    }

    private void PressStuckAndWait(Rig r, int button, string city)
    {
        var start = r.Me.Position;
        Assert.True(r.Client.TryShowScriptDialog("d_helppage", 3, r.Me));
        // Drawing the dialog runs no part of the stuck flow.
        Assert.False(HasStuckEvent(r), "drawing the help dialog ran the stuck function");
        Assert.Null(Worn(r.Me, "i_stuck", r.Stack));
        Assert.True(DClickOpens(r));
        Assert.True(CanCast(r));

        r.Client.HandleGumpResponse(r.Me.Uid.Value, r.Client.Gumps.OpenScriptDialogs["d_helppage"], (uint)button, [], []);

        var counter = Worn(r.Me, "i_stuck", r.Stack);
        Assert.NotNull(counter);
        Assert.Equal(city, counter!.Name);
        Assert.Equal(ItemType.EqScript, counter.ItemType);
        Assert.Equal(Layer.Special, counter.EquipLayer);
        var cooldown = Worn(r.Me, "i_stuck_timer", r.Stack);
        Assert.NotNull(cooldown);
        Assert.True(cooldown!.Timeout > Environment.TickCount64 + 800_000);
        Assert.True(r.Me.IsStatFlag(StatFlag.Freeze), "the function's TOPOBJ.FLAGS |04 did not freeze");
        Assert.True(HasStuckEvent(r));
        Assert.False(DClickOpens(r));
        Assert.False(CanCast(r));

        for (int s = 0; s < 29; s++)
        {
            OneSecond(r);
            Assert.False(counter.IsDeleted, $"the counting item vanished after {s + 1}s");
            Assert.True(HasStuckEvent(r));
        }
        Assert.Equal(29u, counter.More1);
        Assert.Equal(start, r.Me.Position);

        for (int s = 0; s < 3 && !counter.IsDeleted; s++)
            OneSecond(r);

        Assert.True(counter.IsDeleted);
        Assert.False(HasStuckEvent(r));
        Assert.False(r.Me.IsStatFlag(StatFlag.Freeze));
        Assert.Equal(r.Britain.X, r.Me.X);
        Assert.Equal(r.Britain.Y, r.Me.Y);
        Assert.False(r.Me.TryGetTag("STUCK_TILE", out string? tile) && !string.IsNullOrEmpty(tile));
        Assert.True(DClickOpens(r));
        Assert.True(CanCast(r));
        // The cooldown is still worn and still refuses a second use.
        Assert.False(cooldown.IsDeleted);
    }

    [Fact]
    public void StuckButton_CountsThirtySeconds_ThenTeleportsAndReleases()
    {
        var r = BuildSynthetic();
        PressStuckAndWait(r, 31, "Britain");
    }

    /// <summary>The function a dialog line calls returns the label "Stuck"; reading that
    /// RETURN as a number must not call anything, and no "f_" is put in front of a name
    /// to find a function.</summary>
    [Fact]
    public void AReturnedWordIsNotCalledAsAFunction()
    {
        var r = BuildSynthetic();
        string label = r.Stack.Interpreter.ExpandText("<f_array <def.help_type_en>,3>", r.Me, r.Client,
            new SphereNet.Scripting.Execution.TriggerArgs { Source = r.Me }, new SphereNet.Scripting.Execution.ScriptScope());
        Assert.Equal("Stuck", label.Trim());
        Assert.False(HasStuckEvent(r));
        Assert.False(r.Stack.Runner.HasFunction("stuck"));
        Assert.True(r.Stack.Runner.HasFunction("f_stuck"));
    }

    /// <summary>An IT_EQ_SCRIPT item on LAYER_SPECIAL keeps its type and its own timer:
    /// the character's memory sweep leaves it alone.</summary>
    [Fact]
    public void AScriptItemOnTheSpecialLayerIsNotSweptAsAMemory()
    {
        var r = BuildSynthetic();
        var item = r.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, r.Stack.Resources.ResolveDefName("i_stuck").Index));
        Assert.True(r.Me.Equip(item, Layer.Special));
        Assert.Equal(ItemType.EqScript, item.ItemType);
        Assert.False(CharacterMemoryState.IsMemoryObject(item));
        item.SetTimeout(Environment.TickCount64 - 1);
        r.Me.OnTick();
        Assert.False(item.IsDeleted);
        Assert.Contains(item, r.Me.Memories);
    }

    /// <summary>CONT naming a character wears the item (CItem::LoadSetContainer,
    /// CItem.cpp:2548-2561): a script item with no wearable layer of its own goes on
    /// LAYER_SPECIAL, and something that cannot be worn falls into the pack.</summary>
    [Fact]
    public void ContOnACharacterWearsTheItem()
    {
        var r = BuildSynthetic();
        var marker = r.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(marker, r.Stack.Resources.ResolveDefName("i_stuck_timer").Index));
        Assert.True(marker.TrySetProperty("CONT", $"0{r.Me.Uid.Value:X}"));
        Assert.True(marker.IsEquipped);
        Assert.Equal(Layer.Special, marker.EquipLayer);
        Assert.Contains(marker, r.Me.Memories);

        var loose = r.World.CreateItem();
        loose.BaseId = 0x0EED;
        Assert.True(loose.TrySetProperty("CONT", $"0{r.Me.Uid.Value:X}"));
        Assert.False(loose.IsEquipped);
        Assert.Equal(r.Me.Backpack!.Uid, loose.ContainedIn);
    }

    /// <summary>The reported flow on the real Sphere 56T custom-version pack.</summary>
    [Fact]
    public void StuckButton_OnThe56TPack()
    {
        if (Gate.Missing(_out, "56T scripts and save", !Directory.Exists(Pack56T))) return;
        var r = Build(Directory.EnumerateFiles(Pack56T, "*.scp", SearchOption.AllDirectories));
        PressStuckAndWait(r, 31, "Britain");
    }

    /// <summary>A character the 56T save left mid-count - EVENTS=e_stuck and an i_stuck
    /// with MORE1 under 30 and TIMER=0 (an elapsed timer, CObjBase.cpp:1978) - finishes
    /// the count after a load and is released.</summary>
    [Fact]
    public void ACharacterSavedMidCount_IsReleasedAfterLoad()
    {
        if (Gate.Missing(_out, "56T scripts and save",
                !Directory.Exists(Pack56T) || !File.Exists(Path.Combine(Save56T, "spherechars.scp")))) return;

        Character? stuck = null;
        Item? counter = null;
        var r = Build(Directory.EnumerateFiles(Pack56T, "*.scp", SearchOption.AllDirectories), (world, stack) =>
        {
            var resources = stack.Resources;
            using var lf = LoggerFactory.Create(_ => { });
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
            loader.Load(world, Save56T);
            world.RebuildContainerIndex();
            var ev = resources.ResolveDefName("e_stuck");
            int stuckIndex = resources.ResolveDefName("i_stuck").Index;
            stuck = world.GetAllCharactersSnapshot().FirstOrDefault(c => c.Events.Contains(ev) &&
                c.Memories.Any(m => ItemDefHelper.ResolveInstanceDefIndex(m, resources) == stuckIndex));
            counter = stuck?.Memories.First(m => ItemDefHelper.ResolveInstanceDefIndex(m, resources) == stuckIndex);
        });
        if (Gate.MissingValue(_out, "56T scripts and save", stuck)) return;
        var loaded = stuck;
        _out.WriteLine($"{loaded.Name} 0x{loaded.Uid.Value:X}: counter '{counter!.Name}' type={counter.ItemType} more1={counter.More1} timeout={counter.Timeout}");

        Assert.Equal(ItemType.EqScript, counter.ItemType);
        Assert.True(counter.Timeout > 0, "a saved TIMER=0 is an elapsed timer and must run");

        // Offline: no client is attached to the loaded character, so the item runs the
        // script's logged-out branch.
        for (int s = 0; s < 40 && !counter.IsDeleted; s++)
        {
            long now = Environment.TickCount64;
            if (counter.Timeout > 0 && counter.Timeout - now <= 1000)
                counter.SetTimeout(now - 1);
            loaded.OnTick();
            r.World.OnTick();
        }

        Assert.True(counter.IsDeleted);
        Assert.DoesNotContain(r.Stack.Resources.ResolveDefName("e_stuck"), loaded.Events);
        Assert.False(loaded.IsStatFlag(StatFlag.Freeze));
    }
}
