using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Mark and the rune it leaves, against Source-X. CItem::OnSpellEffect SPELL_Mark
/// (CItem.cpp:5721) takes only a rune or a telepad the caster carries, stores the
/// spot, the rune's strength (uses left, from the EFFECT curve) and the area's name,
/// and says nothing - DEFMSG_SPELL_MARK_CONT is the refusal for a rune not carried,
/// which SphereNet used to print on every successful mark. CChar::Spell_Recall
/// (CCharSpell.cpp:395) refuses a faded rune and wears one use off per trip; runes
/// used to last for ever.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class RuneMarkWearTests
{
    private static (GameWorld World, SpellEngine Engine, Character Caster, List<string> Said) Setup()
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Character.SpellbookRequiredEnabled = false;
        var registry = new SpellRegistry();
        registry.Register(Flat(SpellType.Mark, effect: 10));
        registry.Register(Flat(SpellType.Recall, effect: 0));
        var said = new List<string>();
        var engine = new SpellEngine(world, registry) { OnSysMessage = (_, text) => said.Add(text) };

        var town = new Region { Name = "Britain", MapIndex = 0, P = new Point3D(100, 100, 0, 0) };
        town.AddRect(90, 90, 110, 110);
        world.AddRegion(town);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.Player;
        caster.Int = 100; caster.MaxMana = 100; caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        caster.Backpack = pack;
        caster.Equip(pack, Layer.Pack);
        return (world, engine, caster, said);
    }

    private static SpellDef Flat(SpellType id, int effect) => new()
    {
        Id = id,
        Name = id.ToString(),
        Flags = SpellFlag.TargItem,
        CastTimeBase = 1,
        EffectBase = effect,
        EffectScale = effect,
    };

    private static void Cast(SpellEngine engine, Character caster, SpellType spell, Item target)
    {
        Assert.True(engine.CastStart(caster, spell, target.Uid, target.Position) > 0);
        caster.CastSkillSucceeded = true;
        engine.CastDone(caster);
    }

    private static Item Rune(GameWorld world, Character caster, bool carried = true)
    {
        var rune = world.CreateItem();
        rune.BaseId = 0x1F14;
        rune.ItemType = ItemType.Rune;
        if (carried)
            caster.Backpack!.AddItem(rune);
        else
            world.PlaceItem(rune, new Point3D((short)(caster.X + 1), caster.Y, 0, 0));
        return rune;
    }

    [Fact]
    public void MarkingACarriedRuneStoresTheSpotStrengthAndAreaQuietly()
    {
        var (world, engine, caster, said) = Setup();
        var rune = Rune(world, caster);

        Cast(engine, caster, SpellType.Mark, rune);

        Assert.True(rune.TryGetRuneMark(out var mark));
        Assert.Equal((caster.X, caster.Y), (mark.X, mark.Y));
        Assert.Equal(10u, rune.More1);
        Assert.Equal("Britain", rune.Name);
        Assert.DoesNotContain(ServerMessages.Get(Msg.SpellMarkCont), said);
    }

    [Fact]
    public void ARuneOnTheGroundIsNotMarked()
    {
        var (world, engine, caster, said) = Setup();
        var rune = Rune(world, caster, carried: false);

        Cast(engine, caster, SpellType.Mark, rune);

        Assert.False(rune.TryGetRuneMark(out _));
        Assert.Contains(ServerMessages.Get(Msg.SpellMarkCont), said);
    }

    [Fact]
    public void SomethingThatIsNotARuneIsNotMarked()
    {
        var (world, engine, caster, said) = Setup();
        var cup = world.CreateItem();
        cup.BaseId = 0x0995;
        caster.Backpack!.AddItem(cup);

        Cast(engine, caster, SpellType.Mark, cup);

        Assert.False(cup.TryGetRuneMark(out _));
        Assert.Contains(ServerMessages.Get(Msg.SpellRecallNotrune), said);
    }

    [Fact]
    public void EachRecallWearsOneUseAndWarnsUnderTen()
    {
        var (world, engine, caster, said) = Setup();
        var rune = Rune(world, caster);
        rune.SetRuneMark(new Point3D(300, 310, 0, 0));
        rune.More1 = 10;

        Cast(engine, caster, SpellType.Recall, rune);

        Assert.Equal((300, 310), ((int)caster.X, (int)caster.Y));
        Assert.Equal(9u, rune.More1);
        Assert.Contains(ServerMessages.Get(Msg.SpellRecallSfade), said);
    }

    [Fact]
    public void AFadedRuneGoesNowhere()
    {
        var (world, engine, caster, said) = Setup();
        var rune = Rune(world, caster);
        rune.SetRuneMark(new Point3D(300, 310, 0, 0));
        rune.More1 = 0;

        Cast(engine, caster, SpellType.Recall, rune);

        Assert.Equal((100, 100), ((int)caster.X, (int)caster.Y));
        Assert.Contains(ServerMessages.Get(Msg.SpellRecallFaded), said);
    }
}
