using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// FINDID / FINDTYPE / FINDCONT are references (Source-X CContainer::r_GetRefContainer,
/// CContainer.cpp:652-685) on a character and on a container alike: the token is read
/// as ResourceGetID_EatStr with RES_ITEMDEF / RES_TYPEDEF as the default type and
/// CContainer::ContentFind (CContainer.cpp:216) walks the searchable sub-containers.
/// A bare reference reads the UID (0 for none), ISVALID reads 1/0, a trailing key
/// reads off the item found and a trailing verb runs on it (CScriptObj.cpp:497-528).
///
/// And the global EVENTSPET / EVENTSPLAYER / EVENTSITEM / EVENTSREGION lists are
/// loaded the way CResourceRefArray::r_LoadVal loads them (CResourceRef.cpp:72-136):
/// +name adds, -name removes, -0 / -* clears, and a name no loaded section answers
/// to is reported.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class FindReferenceParityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_findref_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Console : SphereNet.Core.Interfaces.ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "test";
    }

    private sealed record Bench(GameWorld World, ResourceHolder Resources, Character Owner, Item Pack);

    private Bench Load()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        File.WriteAllLines(file, new[]
        {
            "[ITEMDEF 0e21]", "DEFNAME=i_bandage", "NAME=Clean Bandage%s", "TYPE=t_bandage",
            "[ITEMDEF i_bandage_salve]", "ID=i_bandage", "TYPE=T_BANDAGE",
            "[ITEMDEF 0e75]", "DEFNAME=i_backpack", "TYPE=t_container",
            "[ITEMDEF 0e76]", "DEFNAME=i_bag", "TYPE=t_container",
            "[ITEMDEF 09ab]", "DEFNAME=i_bankbox", "TYPE=t_eq_bank_box",
            "[EVENTS e_test_known]",
            "[TYPEDEF t_test_scripted]",
        });

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pack = Make(world, resources, "i_backpack", 1);
        owner.Equip(pack, Layer.Pack);
        return new Bench(world, resources, owner, pack);
    }

    private static Item Make(GameWorld world, ResourceHolder resources, string defname, int amount)
    {
        var rid = resources.ResolveDefName(defname);
        Assert.True(rid.IsValid, defname);
        var item = world.CreateItem();
        item.BaseId = ItemDefHelper.CreateGraphic(DefinitionLoader.GetItemDef(rid.Index), rid.Index);
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, rid.Index, fireCreate: false));
        item.Amount = (ushort)amount;
        return item;
    }

    private static Item Put(Bench b, Item container, string defname, int amount)
    {
        var item = Make(b.World, b.Resources, defname, amount);
        Assert.True(container.TryAddItem(item));
        return item;
    }

    private static string Read(ObjBase o, string key)
    {
        Assert.True(o.TryGetProperty(key, out string v), $"{key} did not answer");
        return v;
    }

    [Fact]
    public void FindTypeOnACharacterTakesATypedefNameAndSearchesIntoThePack()
    {
        var b = Load();
        var bag = Put(b, b.Pack, "i_bag", 1);
        var stack = Put(b, bag, "i_bandage", 7);

        Assert.Equal($"0{stack.Uid.Value:X}", Read(b.Owner, "FINDTYPE.t_bandage"));
        Assert.Equal("7", Read(b.Owner, "FINDTYPE.t_bandage.AMOUNT"));
        Assert.Equal("1", Read(b.Owner, "FINDTYPE.t_bandage.ISVALID"));
        Assert.Equal($"0{stack.Uid.Value:X}", Read(b.Owner, $"FINDTYPE.0{(int)ItemType.Bandage:X}"));
        Assert.Equal("0", Read(b.Owner, "FINDTYPE.t_spellbook"));
        Assert.Equal("0", Read(b.Owner, "FINDTYPE.t_spellbook.ISVALID"));
        Assert.Equal("0", Read(b.Owner, "FINDTYPE.t_spellbook.AMOUNT"));
    }

    [Fact]
    public void FindOnACharacterNeverReachesTheBank()
    {
        var b = Load();
        var bank = Make(b.World, b.Resources, "i_bankbox", 1);
        bank.ItemType = ItemType.EqBankBox;
        b.Owner.Equip(bank, Layer.BankBox);
        Put(b, bank, "i_bandage", 3);

        Assert.Equal("0", Read(b.Owner, "FINDTYPE.t_bandage"));
        Assert.Equal("0", Read(b.Owner, "FINDID.i_bandage"));
    }

    [Fact]
    public void AVerbAfterAFindReferenceRunsOnTheItemFound()
    {
        var b = Load();
        var first = Put(b, b.Pack, "i_bandage", 2);
        var salve = Put(b, b.Pack, "i_bandage_salve", 4);

        Assert.True(b.Owner.TryExecuteCommand("FINDID.i_bandage_salve.COLOR", "021", new Console()));
        Assert.Equal(0x21, salve.Hue.Value);
        Assert.NotEqual(0x21, first.Hue.Value);

        Assert.True(b.Owner.TryExecuteCommand("FINDTYPE.t_bandage.REMOVE", "", new Console()));
        Assert.True(first.IsDeleted);              // the first of that TYPE, in content order
        Assert.False(salve.IsDeleted);
    }

    [Fact]
    public void FindContReadsTheNthWornItem()
    {
        var b = Load();
        Assert.Equal($"0{b.Pack.Uid.Value:X}", Read(b.Owner, "FINDCONT.0"));
        Assert.Equal("1", Read(b.Owner, "FINDCONT.0.ISVALID"));
        Assert.Equal("0", Read(b.Owner, "FINDCONT.05"));
    }

    [Fact]
    public void AContainerFindsByDefinitionAndTypeThroughItsSubContainers()
    {
        var b = Load();
        var bag = Put(b, b.Pack, "i_bag", 1);
        var stack = Put(b, bag, "i_bandage", 9);

        Assert.Equal($"0{stack.Uid.Value:X}", Read(b.Pack, "FINDID.i_bandage"));
        Assert.Equal("9", Read(b.Pack, "FINDID.i_bandage.AMOUNT"));
        Assert.Equal($"0{stack.Uid.Value:X}", Read(b.Pack, "FINDTYPE.t_bandage"));
        Assert.Equal($"0{bag.Uid.Value:X}", Read(b.Pack, "FINDCONT.0"));
        Assert.Equal("0", Read(b.Pack, "FINDID.i_bandage_salve"));     // another definition
        Assert.Equal("0", Read(b.Pack, "FINDCONT.1"));

        Assert.True(b.Pack.TryExecuteCommand("FINDID.i_bandage.REMOVE", "", new Console()));
        Assert.True(stack.IsDeleted);
    }

    private static List<string> LoadList(List<ResourceId> target, string raw, ResType type,
        ResourceHolder resources)
    {
        var method = typeof(SphereNet.Server.Program).GetMethod("LoadResourceList",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (List<string>)method.Invoke(null, [target, raw, type, resources])!;
    }

    [Fact]
    public void AGlobalEventListReportsANameNoSectionAnswersTo()
    {
        var b = Load();
        var target = new List<ResourceId>();

        var unknown = LoadList(target, "e_test_known, e_npc_generic_event", ResType.Events, b.Resources);
        Assert.Equal(new[] { "e_npc_generic_event" }, unknown);
        Assert.Contains(DefinitionLoader.ResolveEventName("e_test_known", b.Resources), target);

        // A TYPEDEF answers an event list too, as it does on a TEVENTS line.
        Assert.Empty(LoadList(target, "+t_test_scripted", ResType.Events, b.Resources));
        Assert.Single(target);
    }

    [Fact]
    public void AGlobalEventListTakesTheRefArrayPrefixes()
    {
        var b = Load();
        var target = new List<ResourceId>();

        Assert.Empty(LoadList(target, "+e_test_known, -e_test_known", ResType.Events, b.Resources));
        Assert.Empty(target);
        Assert.Empty(LoadList(target, "e_test_known, t_test_scripted, -*", ResType.Events, b.Resources));
        Assert.Empty(target);
        Assert.Empty(LoadList(target, "", ResType.Events, b.Resources));
    }
}
