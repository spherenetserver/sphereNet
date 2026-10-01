using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// LAYER_FLAG_Stuck end to end, as Source-X runs it:
/// <list type="bullet">
/// <item>LayerAdd / OnRemoveObj side effects (CCharAct.cpp:358, 466): freeze and the
/// paralyze icon come and go with the item;</item>
/// <item>Use_Item_Web (CCharUse.cpp:621-701): STR tears the web (MORE1), a char still
/// on its spot wears a timed IT_EQ_STUCK hold, the hold's ATTR_DECAY timer frees it;</item>
/// <item>Spell_Field's direct cast (CCharSpell.cpp:2251-2301): characters on a field
/// tile lose paralysis and the stuck hold, items on it take the spell.</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class StuckLayerWebAndFieldParityTests
{
    private static Item Web(GameWorld world, uint hits, Point3D at, bool movable = false)
    {
        var web = world.CreateItem();
        web.BaseId = 0x0EE3;
        web.ItemType = ItemType.Web;
        if (!movable)
            web.SetAttr(ObjAttributes.Move_Never);
        web.More1 = hits;
        world.PlaceItem(web, at);
        return web;
    }

    private static Character Walker(GameWorld world, int str, Point3D at)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = (short)str;
        world.PlaceCharacter(ch, at);
        return ch;
    }

    // ---- LayerAdd / OnRemoveObj ------------------------------------------------

    [Fact]
    public void EquippingTheStuckLayerFreezesAndShowsTheParalyzeIcon_RemovingLiftsBoth()
    {
        var world = TestHarness.CreateWorld();
        var ch = Walker(world, 50, new Point3D(100, 100, 0, 0));
        var buffs = new List<(BuffIcon Icon, bool On, ushort Seconds)>();
        Character.OnClientBuffChanged = (c, icon, on, secs, _) => { if (c == ch) buffs.Add((icon, on, secs)); };

        var hold = world.CreateItem();
        hold.ItemType = ItemType.EqStuck;
        hold.SetTimeout(Environment.TickCount64 + 5_500);
        Assert.True(ch.Equip(hold, Layer.FlagStuck));

        Assert.True(ch.IsStatFlag(StatFlag.Freeze));
        Assert.Contains(buffs, b => b.Icon == BuffIcon.Paralyze && b.On && b.Seconds is 4 or 5);

        buffs.Clear();
        ch.Unequip(Layer.FlagStuck);

        Assert.False(ch.IsStatFlag(StatFlag.Freeze));
        Assert.Contains(buffs, b => b.Icon == BuffIcon.Paralyze && !b.On);
    }

    // ---- Use_Item_Web ----------------------------------------------------------

    [Fact]
    public void AWeakCharOnASurvivingWebIsHeldByATimedStuckItem()
    {
        var world = TestHarness.CreateWorld();
        var spot = new Point3D(100, 100, 0, 0);
        var web = Web(world, 300, spot);
        var ch = Walker(world, 10, spot);

        Assert.True(ch.UseItemWeb(web));

        Assert.Equal(290u, web.More1);
        var hold = ch.GetEquippedItem(Layer.FlagStuck);
        Assert.NotNull(hold);
        Assert.Equal(ItemType.EqStuck, hold!.ItemType);
        Assert.Equal((ushort)0x0EE3, hold.BaseId);
        Assert.True(hold.IsAttr(ObjAttributes.Decay));
        Assert.Equal(web.Uid, hold.Link);
        // 2 + (100-10)*290/10 capped at 10 seconds.
        long left = hold.Timeout - Environment.TickCount64;
        Assert.InRange(left, 9_000, 10_000);
        Assert.True(ch.IsStatFlag(StatFlag.Freeze));
    }

    [Fact]
    public void TheStuckTimerScalesWithWeaknessAndWebStrength()
    {
        var world = TestHarness.CreateWorld();
        var spot = new Point3D(100, 100, 0, 0);
        var web = Web(world, 100, spot);
        var ch = Walker(world, 99, spot);   // 100-99 = 1; 1*(100-99)/10 = 0 -> 2 s

        Assert.True(ch.UseItemWeb(web));

        long left = ch.GetEquippedItem(Layer.FlagStuck)!.Timeout - Environment.TickCount64;
        Assert.InRange(left, 1_000, 2_000);
    }

    [Fact]
    public void WhileTheHoldsTimerRunsTheWebIsNotTornAgain()
    {
        var world = TestHarness.CreateWorld();
        var spot = new Point3D(100, 100, 0, 0);
        var web = Web(world, 300, spot);
        var ch = Walker(world, 10, spot);
        Assert.True(ch.UseItemWeb(web));
        uint after = web.More1;

        Assert.True(ch.UseItemWeb(web));

        Assert.Equal(after, web.More1);
    }

    [Fact]
    public void AStrongCharTearsTheWebAndIsNotHeld()
    {
        var world = TestHarness.CreateWorld();
        var spot = new Point3D(100, 100, 0, 0);
        var web = Web(world, 50, spot);
        var ch = Walker(world, 100, spot);

        Assert.False(ch.UseItemWeb(web));

        Assert.True(web.IsDeleted);
        Assert.Null(ch.GetEquippedItem(Layer.FlagStuck));
        Assert.False(ch.IsStatFlag(StatFlag.Freeze));
    }

    [Fact]
    public void StrugglingFromBesideTheWebDoesNotStick()
    {
        var world = TestHarness.CreateWorld();
        var web = Web(world, 300, new Point3D(101, 100, 0, 0));
        var ch = Walker(world, 10, new Point3D(100, 100, 0, 0));

        Assert.False(ch.UseItemWeb(web));

        Assert.Equal(290u, web.More1);
        Assert.Null(ch.GetEquippedItem(Layer.FlagStuck));
    }

    [Fact]
    public void AMovableWebIsWalkedThrough()
    {
        var world = TestHarness.CreateWorld();
        var spot = new Point3D(100, 100, 0, 0);
        var web = Web(world, 300, spot, movable: true);
        var ch = Walker(world, 10, spot);

        Assert.False(ch.UseItemWeb(web));

        Assert.Equal(300u, web.More1);
        Assert.Null(ch.GetEquippedItem(Layer.FlagStuck));
    }

    [Fact]
    public void GiantSpidersAndGhostsPassThrough()
    {
        var world = TestHarness.CreateWorld();
        var spot = new Point3D(100, 100, 0, 0);
        var web = Web(world, 300, spot);
        var spider = Walker(world, 10, spot);
        spider.BodyId = 0x001C;
        var ghost = Walker(world, 10, spot);
        ghost.SetStatFlag(StatFlag.Dead);

        Assert.False(spider.UseItemWeb(web));
        Assert.False(ghost.UseItemWeb(web));
        Assert.Equal(300u, web.More1);
    }

    [Fact]
    public void AnUntimedHoldIsToldItIsStuckAndTheWebStillTakesTheStruggle()
    {
        var world = TestHarness.CreateWorld();
        var spot = new Point3D(100, 100, 0, 0);
        var web = Web(world, 300, spot);
        var ch = Walker(world, 10, spot);
        var hold = world.CreateItem();
        hold.ItemType = ItemType.EqStuck;
        Assert.True(ch.Equip(hold, Layer.FlagStuck));
        // Frozen chars cannot move items, so the web stays unmovable for them too.

        Assert.True(ch.UseItemWeb(web));

        Assert.Equal(290u, web.More1);
        Assert.Same(hold, ch.GetEquippedItem(Layer.FlagStuck));
    }

    [Fact]
    public void TheHoldExpiresOnItsTimerAndFreesTheChar()
    {
        var world = TestHarness.CreateWorld();
        var spot = new Point3D(100, 100, 0, 0);
        var web = Web(world, 300, spot);
        var ch = Walker(world, 10, spot);
        Assert.True(ch.UseItemWeb(web));
        var hold = ch.GetEquippedItem(Layer.FlagStuck)!;

        hold.SetTimeout(Environment.TickCount64 - 1);
        hold.OnTick();

        Assert.True(hold.IsDeleted);
        Assert.Null(ch.GetEquippedItem(Layer.FlagStuck));
        Assert.False(ch.IsStatFlag(StatFlag.Freeze));
    }

    // ---- Spell_Field direct cast -----------------------------------------------

    private static (GameWorld World, SpellEngine Engine, Character Caster) FieldSetup(SpellDef def)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var registry = new SpellRegistry();
        registry.Register(def);
        var engine = new SpellEngine(world, registry);
        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        return (world, engine, caster);
    }

    private static SpellDef FireField(SpellFlag extra = 0) => new()
    {
        Id = SpellType.FireField,
        Name = "Fire Field",
        Flags = SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Damage | SpellFlag.Field | extra,
        EffectBase = 2, EffectScale = 10,
        DurationBase = 1200,
    };

    private static void Cast(SpellEngine engine, Character caster, SpellType spell)
    {
        Assert.True(engine.CastStart(caster, spell, caster.Uid, new Point3D(105, 100, 0, 0)) > 0);
        Assert.True(engine.CastDone(caster));
    }

    private static Item StuckOn(GameWorld world, Character ch)
    {
        var hold = world.CreateItem();
        hold.ItemType = ItemType.EqStuck;
        Assert.True(ch.Equip(hold, Layer.FlagStuck));
        return hold;
    }

    [Fact]
    public void AFieldLaidOnAStuckCharDeletesTheHold()
    {
        var (world, engine, caster) = FieldSetup(FireField());
        var victim = world.CreateCharacter();
        world.PlaceCharacter(victim, new Point3D(105, 100, 0, 0));
        var hold = StuckOn(world, victim);

        Cast(engine, caster, SpellType.FireField);

        Assert.True(hold.IsDeleted);
        Assert.False(victim.IsStatFlag(StatFlag.Freeze));
    }

    [Fact]
    public void ANoUnparalyzeFieldLeavesTheHold()
    {
        var (world, engine, caster) = FieldSetup(FireField(SpellFlag.NoUnparalyze));
        var victim = world.CreateCharacter();
        world.PlaceCharacter(victim, new Point3D(105, 100, 0, 0));
        var hold = StuckOn(world, victim);

        Cast(engine, caster, SpellType.FireField);

        Assert.False(hold.IsDeleted);
        Assert.True(victim.IsStatFlag(StatFlag.Freeze));
    }

    [Fact]
    public void ACharAboveTheCastersPlevelIsLeftAlone()
    {
        var (world, engine, caster) = FieldSetup(FireField());
        var admin = world.CreateCharacter();
        admin.PrivLevel = PrivLevel.Admin;
        world.PlaceCharacter(admin, new Point3D(105, 100, 0, 0));
        var hold = StuckOn(world, admin);

        Cast(engine, caster, SpellType.FireField);

        Assert.False(hold.IsDeleted);
    }

    [Fact]
    public void AFireFieldBurnsAWebOnItsTile()
    {
        var (world, engine, caster) = FieldSetup(FireField());
        var web = world.CreateItem();
        web.ItemType = ItemType.Web;
        web.More1 = 500;
        world.PlaceItem(web, new Point3D(105, 100, 0, 0));

        Cast(engine, caster, SpellType.FireField);

        // CItem::OnSpellEffect: a HARM spell deals 1 point of DAMAGE_MAGIC|DAMAGE_FIRE,
        // and fire destroys a web whatever its strength (CItem.cpp:5743, 5894).
        Assert.True(web.IsDeleted);
    }

    [Fact]
    public void AFieldRechargesABlankWandOnItsTile()
    {
        var (world, engine, caster) = FieldSetup(FireField());
        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        world.PlaceItem(wand, new Point3D(105, 100, 0, 0));

        Cast(engine, caster, SpellType.FireField);

        Assert.Equal((short)SpellType.FireField, wand.MoreP.X);
        Assert.Equal(1u, wand.More2);
        Assert.True(wand.IsAttr(ObjAttributes.Magic));
    }
}
