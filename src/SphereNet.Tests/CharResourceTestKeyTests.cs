using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
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
/// A character answers the CContainer script keys RESTEST and RESCOUNT over what it
/// wears - Source-X CChar::r_WriteVal falls through to CContainer::r_WriteValContainer
/// (CChar.cpp:2332, CContainer.cpp:689). A Sphere 56T custom-version bandage TYPEDEF
/// gates its @TargOn_Char on "&lt;SRC.RESTEST 1 &lt;BASEID&gt;&gt;" before it CONSUMEs one;
/// with the key unanswered on a character the test read 0 and every bandage use was
/// refused as "no bandages" while the pack held a full stack.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CharResourceTestKeyTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_charrestest_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed record Bench(GameWorld World, TriggerDispatcher Dispatcher, Character Healer, Item Pack);

    private Bench Load()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        File.WriteAllLines(file, new[]
        {
            "[ITEMDEF 0e21]", "DEFNAME=i_bandage", "NAME=Clean Bandage%s", "TYPE=t_bandage",
            "VALUE=1", "WEIGHT=0.2", "DUPELIST=0ee9",
            "[ITEMDEF 0ee9]", "DUPEITEM=0e21",
            "[ITEMDEF i_bandage_salve]", "ID=i_bandage", "TYPE=T_BANDAGE",
            "[ITEMDEF 0e75]", "DEFNAME=i_backpack", "TYPE=t_container",
            "[ITEMDEF 09ab]", "DEFNAME=i_bankbox", "TYPE=t_eq_bank_box",
            // the shape of the 56T bandage cursor handler
            "[TYPEDEF T_BANDAGE]",
            "on=@targon_char",
            "if !(<src.restest 1 <baseid>>)",
            "src.tag.outcome=nobandage",
            "return 1",
            "elseif (<topobj> != <src>)",
            "src.tag.outcome=notcarried",
            "return 1",
            "endif",
            "src.tag.found=<src.findid.i_bandage>",
            "src.consume 1 <baseid>",
            "src.tag.outcome=started",
            "return 1",
        });

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var healer = world.CreateCharacter();
        healer.IsPlayer = true;
        world.PlaceCharacter(healer, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        healer.Equip(pack, Layer.Pack);
        return new Bench(world, dispatcher, healer, pack);
    }

    private static Item Bandages(Bench b, Item container, ushort graphic, int amount)
    {
        var item = b.World.CreateItem();
        item.BaseId = graphic;
        ItemDefHelper.ApplyInstanceMetadata(item, graphic, fireCreate: false);
        item.Amount = (ushort)amount;
        Assert.True(container.TryAddItem(item));
        return item;
    }

    private static string Read(ObjBase o, string key)
    {
        Assert.True(o.TryGetProperty(key, out string v), $"{key} did not answer");
        return v;
    }

    [Fact]
    public void SelfBandageWithAStackInThePackStartsAndSpendsOne()
    {
        var b = Load();
        var stack = Bandages(b, b.Pack, 0x0E21, 50);

        b.Dispatcher.FireItemTrigger(stack, ItemTrigger.TargOnChar,
            new SphereNet.Game.Scripting.TriggerArgs { CharSrc = b.Healer, ItemSrc = stack, O1 = b.Healer });

        Assert.Equal("started", b.Healer.Tags.Get("OUTCOME"));
        Assert.Equal($"0{stack.Uid.Value:X}", b.Healer.Tags.Get("FOUND"));
        Assert.Equal(49, stack.Amount);
    }

    [Fact]
    public void RestestOnACharacterWalksThePackButNotTheBank()
    {
        var b = Load();
        Bandages(b, b.Pack, 0x0E21, 3);
        Bandages(b, b.Pack, 0x0EE9, 2);                 // the DUPEITEM graphic is the same resource

        Assert.Equal("1", Read(b.Healer, "RESTEST 5 i_bandage"));
        Assert.Equal("0", Read(b.Healer, "RESTEST 6 i_bandage"));
        Assert.Equal("0", Read(b.Healer, "RESTEST 1 i_bandage_salve"));   // another definition
        Assert.Equal("5", Read(b.Healer, "RESCOUNT i_bandage"));
        Assert.Equal("0", Read(b.Healer, "RESTEST"));                   // empty list loads nothing

        var bank = b.World.CreateItem();
        bank.BaseId = 0x09AB;
        bank.ItemType = ItemType.EqBankBox;
        b.Healer.Equip(bank, Layer.BankBox);
        Bandages(b, bank, 0x0E21, 10);
        Assert.Equal("5", Read(b.Healer, "RESCOUNT i_bandage"));
        Assert.Equal("0", Read(b.Healer, "RESTEST 6 i_bandage"));
    }

    /// <summary>FINDID is a reference (CContainer::r_GetRefContainer): a defname is the
    /// ITEMDEF it names (ContentFind), a trailing key reads off the item found, and the
    /// chained verb reaches it too.</summary>
    [Fact]
    public void FindIdByDefnameResolvesReadsAndRemoves()
    {
        var b = Load();
        var salve = Bandages(b, b.Pack, 0x0E21, 4);
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(salve,
            b.Dispatcher.Resources!.ResolveDefName("i_bandage_salve").Index, fireCreate: false));
        salve.Tags.Set("SALVE", "1");
        var plain = Bandages(b, b.Pack, 0x0E21, 7);

        Assert.Equal($"0{plain.Uid.Value:X}", Read(b.Healer, "FINDID.i_bandage"));
        Assert.Equal($"0{salve.Uid.Value:X}", Read(b.Healer, "FINDID.i_bandage_salve"));
        Assert.Equal("1", Read(b.Healer, "FINDID.i_bandage_salve.TAG.SALVE"));
        Assert.Equal("0", Read(b.Healer, "FINDID.i_backpack_missing"));

        Assert.True(b.Healer.TryExecuteCommand("FINDID.i_bandage_salve.REMOVE", "", null!));
        Assert.True(salve.IsDeleted);
        Assert.Equal("0", Read(b.Healer, "FINDID.i_bandage_salve"));
        Assert.False(plain.IsDeleted);
    }

    [Fact]
    public void SelfBandageWithNoneCarriedIsRefused()
    {
        var b = Load();
        var ground = b.World.CreateItem();
        ground.BaseId = 0x0E21;
        ItemDefHelper.ApplyInstanceMetadata(ground, 0x0E21, fireCreate: false);
        b.World.PlaceItem(ground, new Point3D(101, 100, 0, 0));

        b.Dispatcher.FireItemTrigger(ground, ItemTrigger.TargOnChar,
            new SphereNet.Game.Scripting.TriggerArgs { CharSrc = b.Healer, ItemSrc = ground, O1 = b.Healer });

        Assert.Equal("nobandage", b.Healer.Tags.Get("OUTCOME"));
    }
}
