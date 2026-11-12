using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// An EVENTS/TEVENTS reference names a section, and the section says what it is.
///
/// Upstream resolves the name against the DEFNAME table BEFORE labelling it with the
/// type the caller asked for - "Do not enforce the restype. Just fill it in if we are
/// not sure what the type is" (CResourceHolder.cpp:99-104) - so a TEVENTS line that
/// names a [TYPEDEF] block reaches that block. The packs lean on this hard: 720 of
/// the live pack's 725 TEVENTS references name a typedef and only ONE names an
/// [EVENTS] block; the reference distribution has 1715 of them (ei_equipitem alone is
/// 907).
///
/// Here the name was hashed straight into the EVENTS namespace, where a typedef does
/// not live. Nothing logged - a def-level EVENTS entry that resolves to no resource is
/// simply skipped at dispatch - so almost the entire TEVENTS surface of every pack was
/// loaded and then silently inert. ObjBase had the right fallback written out for the
/// instance-level case, but it sat below a line that always succeeds, so it could
/// never run.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class EventsNameResolutionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_evn_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    private (TriggerDispatcher Dispatcher, ResourceHolder Resources) Load(string script)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "e.scp");
        File.WriteAllText(file, script);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(),
            lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        return (new TriggerDispatcher { Resources = resources, Runner = runner }, resources);
    }

    private const string Typedef =
        "[TYPEDEF ei_probe_equipitem]" + Nl +
        "ON=@DClick" + Nl +
        "TAG.PROBE=1" + Nl +
        "RETURN 1" + Nl + Nl;

    /// <summary>The 907-use case: TEVENTS on an ITEMDEF naming a typedef.</summary>
    [Fact]
    public void AnItemdefTeventsNamingATypedefRuns()
    {
        var (dispatcher, _) = Load(Typedef +
            "[ITEMDEF 013bb]" + Nl +
            "DEFNAME=i_probe_sword" + Nl +
            "TYPE=t_weapon_sword" + Nl +
            "TEVENTS=ei_probe_equipitem" + Nl);

        var item = new Item { BaseId = 0x13BB };
        Assert.True(item.TryGetProperty("ISTEVENT.ei_probe_equipitem", out string hasTemplateEvent));
        Assert.Equal("1", hasTemplateEvent);
        Assert.True(item.TryGetProperty("ISEVENT.ei_probe_equipitem", out string hasInstanceEvent));
        Assert.Equal("0", hasInstanceEvent);
        var result = dispatcher.FireItemTrigger(item, ItemTrigger.DClick,
            new SphereNet.Game.Scripting.TriggerArgs());

        Assert.Equal(TriggerResult.True, result);
        string v = item.TagValue("PROBE");
        Assert.Equal("1", v);
    }

    /// <summary>And on a CHARDEF, which loads through the same list.</summary>
    [Fact]
    public void ACharefTeventsNamingATypedefIsResolved()
    {
        var (_, resources) = Load(Typedef +
            "[CHARDEF c_probe_man]" + Nl +
            "ID=0190" + Nl +
            "TEVENTS=ei_probe_equipitem" + Nl);

        var rid = resources.ResolveDefName("c_probe_man");
        Assert.Equal(ResType.CharDef, rid.Type);
        var def = DefinitionLoader.GetCharDef(rid.Index);
        Assert.NotNull(def);

        Assert.Contains(def!.Events, rid => rid.Type == ResType.TypeDef);
        Assert.DoesNotContain(def.Events, rid => rid.Type == ResType.Events);
    }

    /// <summary>The instance-level EVENTS write resolves the same way - this is the
    /// branch ObjBase already had, shadowed by the line above it.</summary>
    [Fact]
    public void AnInstanceEventsWriteNamingATypedefIsResolved()
    {
        var (dispatcher, _) = Load(Typedef +
            "[ITEMDEF 013bb]" + Nl +
            "DEFNAME=i_probe_sword" + Nl +
            "TYPE=t_weapon_sword" + Nl);

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var item = world.CreateItem();
        item.BaseId = 0x13BB;
        Assert.True(item.TrySetProperty("EVENTS", "ei_probe_equipitem"));
        Assert.True(item.TryGetProperty("ISEVENT.ei_probe_equipitem", out string hasInstanceEvent));
        Assert.Equal("1", hasInstanceEvent);
        Assert.True(item.TryGetProperty("ISTEVENT.ei_probe_equipitem", out string hasTemplateEvent));
        Assert.Equal("0", hasTemplateEvent);

        var result = dispatcher.FireItemTrigger(item, ItemTrigger.DClick,
            new SphereNet.Game.Scripting.TriggerArgs());

        Assert.Equal(TriggerResult.True, result);
        string v = item.TagValue("PROBE");
        Assert.Equal("1", v);
    }

    [Theory]
    [InlineData(House.LockdownEvent, false)]
    [InlineData(House.SecureEvent, true)]
    public void HousingAttachesAndRestoresTypedStorageEvents(string marker, bool secure)
    {
        var (dispatcher, resources) = Load($"[TYPEDEF {marker}]" + Nl +
            "ON=@ClientTooltip" + Nl + "TAG.PROBE=1" + Nl);
        var world = TestHarness.CreateWorld();
        var engine = new HousingEngine(world, new MultiRegistry());
        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        multi.BaseId = 0x4064;
        var point = new Point3D(100, 100, 0, 0);
        Assert.True(world.PlaceItem(multi, point));
        var house = engine.RegisterExistingMulti(multi)!;
        Assert.NotNull(house);
        house.Owner = world.CreateCharacter().Uid;

        var item = world.CreateItem();
        item.ItemType = secure ? ItemType.Container : ItemType.Normal;
        Assert.True(world.PlaceItem(item, point));
        Assert.True(secure ? house.SecureContainer(item.Uid, house.Owner) : house.Lockdown(item.Uid, house.Owner));
        var typedEvent = resources.ResolveDefName(marker);
        Assert.Equal(ResType.TypeDef, typedEvent.Type);
        Assert.Equal(typedEvent, Assert.Single(item.Events));
        Assert.True(item.TryGetProperty($"ISEVENT.{marker}", out string hasMarker));
        Assert.Equal("1", hasMarker);

        dispatcher.FireItemTrigger(item, ItemTrigger.ClientTooltip,
            new SphereNet.Game.Scripting.TriggerArgs());
        Assert.Equal("1", item.TagValue("PROBE"));

        // Older saves can retain the storage record without the event on the item.
        engine.SerializeAllToTags();
        item.Events.Clear();
        engine.DeserializeFromWorld();
        Assert.Equal(typedEvent, Assert.Single(item.Events));
        house = engine.GetHouse(multi.Uid)!;
        Assert.True(secure ? house.ReleaseSecure(item.Uid, house.Owner) : house.ReleaseLockdown(item.Uid, house.Owner));
        Assert.Empty(item.Events);
    }

    [Fact]
    public void AHouseDoorAttachesItsTypedefEvent()
    {
        var (dispatcher, resources) = Load($"[TYPEDEF {CustomHousingEngine.DoorEvent}]" + Nl +
            "ON=@ClientTooltip" + Nl + "TAG.PROBE=1" + Nl);
        var door = new Item { ItemType = ItemType.Door };
        door.Events.Add(ResourceId.FromEventName(CustomHousingEngine.DoorEvent));

        CustomHousingEngine.ApplyComponentCreate(new Item(), door);

        Assert.Contains(resources.ResolveDefName(CustomHousingEngine.DoorEvent), door.Events);
        Assert.DoesNotContain(ResourceId.FromEventName(CustomHousingEngine.DoorEvent), door.Events);
        dispatcher.FireItemTrigger(door, ItemTrigger.ClientTooltip,
            new SphereNet.Game.Scripting.TriggerArgs());
        Assert.Equal("1", door.TagValue("PROBE"));
    }

    /// <summary>A name that really is an [EVENTS] block keeps working - that is the
    /// path the change reorders around, and one live-pack reference still uses it.</summary>
    [Fact]
    public void AnEventsBlockStillResolvesToItself()
    {
        var (dispatcher, _) = Load(
            "[EVENTS e_probe_block]" + Nl +
            "ON=@DClick" + Nl +
            "TAG.PROBE_EV=1" + Nl +
            "RETURN 1" + Nl + Nl +
            "[ITEMDEF 013bb]" + Nl +
            "DEFNAME=i_probe_sword" + Nl +
            "TYPE=t_weapon_sword" + Nl +
            "TEVENTS=e_probe_block" + Nl);

        var item = new Item { BaseId = 0x13BB };
        var result = dispatcher.FireItemTrigger(item, ItemTrigger.DClick,
            new SphereNet.Game.Scripting.TriggerArgs());

        Assert.Equal(TriggerResult.True, result);
        string v = item.TagValue("PROBE_EV");
        Assert.Equal("1", v);
    }

    /// <summary>A name nothing defines stays in the EVENTS namespace and stays
    /// harmless - the packs have a handful of those and they must not start
    /// resolving to something else.</summary>
    [Fact]
    public void AnUndefinedNameStaysAnEventsReference()
    {
        Load(
            "[ITEMDEF 013bb]" + Nl +
            "DEFNAME=i_probe_sword" + Nl +
            "TYPE=t_weapon_sword" + Nl +
            "TEVENTS=t_anvil_nothing_defines_this" + Nl);

        var def = DefinitionLoader.GetItemDef(0x13BB);
        Assert.NotNull(def);
        Assert.Single(def!.Events);
        Assert.Equal(ResType.Events, def.Events[0].Type);
    }

    /// <summary>An AREADEF's EVENTS list names REGIONTYPE blocks - 2169 references in
    /// the live pack, 4488 in the reference distribution, and not one of them names an
    /// [EVENTS] block. The region trigger dispatch looks each entry up as a resource,
    /// so those had to be the resolved reference, not a hash into a namespace where a
    /// regiontype does not live. What was unreachable: @Enter, @RegPeriodic and
    /// @CliPeriodic on the regiontypes - the area background music among them.</summary>
    [Fact]
    public void ARegionEventsEntryNamingARegiontypeRuns()
    {
        var (dispatcher, resources) = Load(
            "[REGIONTYPE r_probe_area]" + Nl +
            "ON=@Enter" + Nl +
            "TAG.ENTERED=1" + Nl);

        var region = new SphereNet.Game.World.Regions.Region { Name = "probe" };
        region.AddEventsFromTag("r_probe_area");

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var ch = world.CreateCharacter();

        dispatcher.FireRegionEvents(region, "Enter", ch,
            new SphereNet.Game.Scripting.TriggerArgs());

        Assert.True(region.TryGetProperty("TAG.ENTERED", out var v));
        Assert.Equal("1", v);
    }
}
