using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Where a spell is refused, and the magic flags that were declared but never read
/// (port plan İŞ-24 / PLAN-403).
///
/// The reference asks ONE question of the caster's area - CheckAntiMagic
/// (CRegion.cpp:720) - and its answer covers far more than the two flags this engine
/// consulted. Mark was ungated altogether, so a rune could be marked anywhere,
/// including aboard a ship, which upstream refuses through the same call.
///
/// Alongside it, three MAGICFLAGS bits sat in the enum and in sphere.ini with nothing
/// reading them: SUMMONWALKCHECK, OVERRIDEFIELDS (this file) and CASTPARALYZED (closed
/// in the previous wave). A setting a shard can write and the engine ignores is worse
/// than one that does not exist.
/// </summary>
public sealed class SpellRegionAndFlagParityTests : IDisposable
{
    public void Dispose() => Character.MagicFlags = 0;

    private static SpellDef Def(SpellType id, SpellFlag flags = SpellFlag.None) => new()
    {
        Id = id,
        Flags = flags,
        ManaCost = 0,
        CastTimeBase = 5,
    };

    private static Region RegionWith(RegionFlag flags)
    {
        var region = new Region { Name = "probe" };
        region.Flags = flags;
        return region;
    }

    // ---- CheckAntiMagic, the reference's own table -------------------------

    [Fact]
    public void AnAntiMagicRegionRefusesEverything()
    {
        var region = RegionWith(RegionFlag.NoMagic);
        Assert.True(SpellEngine.RegionBlocksSpell(region, Def(SpellType.Heal)));
        Assert.True(SpellEngine.RegionBlocksSpell(region, Def(SpellType.Mark)));
    }

    [Fact]
    public void APlainRegionRefusesNothing()
    {
        var region = RegionWith(RegionFlag.None);
        Assert.False(SpellEngine.RegionBlocksSpell(region, Def(SpellType.Mark)));
        Assert.False(SpellEngine.RegionBlocksSpell(region, Def(SpellType.GateTravel)));
        Assert.False(SpellEngine.RegionBlocksSpell(
            region, Def(SpellType.Fireball, SpellFlag.Harm)));
    }

    [Theory]
    [InlineData(RegionFlag.Recall)]
    [InlineData(RegionFlag.Ship)]
    public void RecallInAndAShipBothRefuseMarkAndGate(RegionFlag flag)
    {
        // A SHIP region answers the recall-in question too (CRegion.cpp:736):
        // you cannot mark a rune, or gate, from the deck of a boat.
        var region = RegionWith(flag);
        Assert.True(SpellEngine.RegionBlocksSpell(region, Def(SpellType.Mark)));
        Assert.True(SpellEngine.RegionBlocksSpell(region, Def(SpellType.GateTravel)));
        Assert.False(SpellEngine.RegionBlocksSpell(region, Def(SpellType.Recall)));
        Assert.False(SpellEngine.RegionBlocksSpell(region, Def(SpellType.Heal)));
    }

    [Fact]
    public void RecallOutRefusesRecallGateAndMark()
    {
        var region = RegionWith(RegionFlag.RecallOut);
        Assert.True(SpellEngine.RegionBlocksSpell(region, Def(SpellType.Recall)));
        Assert.True(SpellEngine.RegionBlocksSpell(region, Def(SpellType.GateTravel)));
        Assert.True(SpellEngine.RegionBlocksSpell(region, Def(SpellType.Mark)));
        Assert.False(SpellEngine.RegionBlocksSpell(region, Def(SpellType.Teleport)));
    }

    [Fact]
    public void TheNarrowFlagsRefuseOnlyTheirOwnSpell()
    {
        var gate = RegionWith(RegionFlag.Gate);
        Assert.True(SpellEngine.RegionBlocksSpell(gate, Def(SpellType.GateTravel)));
        Assert.False(SpellEngine.RegionBlocksSpell(gate, Def(SpellType.Recall)));

        var tele = RegionWith(RegionFlag.NoTeleport);
        Assert.True(SpellEngine.RegionBlocksSpell(tele, Def(SpellType.Teleport)));
        Assert.False(SpellEngine.RegionBlocksSpell(tele, Def(SpellType.Recall)));
    }

    [Fact]
    public void NoMagicDamageRefusesOnlyHarmfulSpells()
    {
        var region = RegionWith(RegionFlag.NoMagicDamage);
        Assert.True(SpellEngine.RegionBlocksSpell(
            region, Def(SpellType.Fireball, SpellFlag.Harm)));
        Assert.False(SpellEngine.RegionBlocksSpell(region, Def(SpellType.Heal)));
        Assert.False(SpellEngine.RegionBlocksSpell(region, Def(SpellType.Mark)));
    }

    // ---- the flags that were never read -------------------------------------

    /// <summary>ResetEngineStatics runs between the constructor and the test body,
    /// so the gates that are not under test are pinned from here.</summary>
    private static void PinSpellStatics()
    {
        Character.MagicFlags = 0;
        Character.ReagentsRequiredEnabled = false;
        Character.SpellbookRequiredEnabled = false;
        Character.EquippedCastEnabled = true;
    }

    private static (GameWorld World, SpellEngine Engine, Character Caster) Setup(SpellType spell)
    {
        PinSpellStatics();

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = spell,
            Flags = SpellFlag.Summon | SpellFlag.TargXYZ,
            ManaCost = 0,
            CastTimeBase = 5,
            DurationBase = 100,
        });

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.Player;
        caster.MaxMana = 100; caster.Mana = 100;
        caster.MaxHits = 100; caster.Hits = 100;
        caster.SetSkill(SkillType.Magery, 2000);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        return (world, new SpellEngine(world, registry), caster);
    }

    [Fact]
    public void SummonWalkCheckIsInertWithoutMapData()
    {
        // The check needs real map data to say anything; with none loaded (the unit
        // world) it must not refuse, or every summon in a test world would fail.
        var (_, engine, caster) = Setup(SpellType.AirElemental);
        Character.MagicFlags = (int)MagicConfigFlags.SummonWalkCheck;

        Assert.True(engine.CastStart(
            caster, SpellType.AirElemental, Serial.Invalid,
            new Point3D(102, 100, 0, 0)) > 0);
    }

    [Fact]
    public void OverrideFieldsReplacesTheSpellItemsAlreadyOnTheTile()
    {
        PinSpellStatics();
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var tile = new Point3D(120, 120, 0, 0);
        var older = world.CreateItem();
        older.BaseId = 0x3996;
        older.ItemType = ItemType.Fire;
        Assert.True(world.PlaceItem(older, tile));

        var bystander = world.CreateItem();
        bystander.BaseId = 0x0F51;
        bystander.ItemType = ItemType.WeaponSword;   // not a field: never touched
        Assert.True(world.PlaceItem(bystander, tile));

        Character.MagicFlags = (int)MagicConfigFlags.OverrideFields;

        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.FireField,
            Flags = SpellFlag.Field | SpellFlag.Damage | SpellFlag.TargXYZ,
            ManaCost = 0,
            CastTimeBase = 5,
            DurationBase = 100,
            EffectBase = 5,
        });
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.Player;
        caster.MaxMana = 100; caster.Mana = 100;
        caster.SetSkill(SkillType.Magery, 2000);
        world.PlaceCharacter(caster, new Point3D(120, 118, 0, 0));

        Assert.True(engine.CastStart(caster, SpellType.FireField, Serial.Invalid, tile) > 0);
        caster.SetCastTimerEnd(Environment.TickCount64 - 1);
        engine.TickCastTimer(caster);

        Assert.True(older.IsDeleted, "the older field segment should have been replaced");
        Assert.False(bystander.IsDeleted, "an ordinary item on the tile is not a field");
    }

    [Fact]
    public void WithoutTheFlagAnOlderFieldSurvives()
    {
        PinSpellStatics();
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var tile = new Point3D(130, 130, 0, 0);
        var older = world.CreateItem();
        older.BaseId = 0x3996;
        older.ItemType = ItemType.Fire;
        Assert.True(world.PlaceItem(older, tile));

        Character.MagicFlags = 0;

        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.FireField,
            Flags = SpellFlag.Field | SpellFlag.Damage | SpellFlag.TargXYZ,
            ManaCost = 0,
            CastTimeBase = 5,
            DurationBase = 100,
            EffectBase = 5,
        });
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.Player;
        caster.MaxMana = 100; caster.Mana = 100;
        caster.SetSkill(SkillType.Magery, 2000);
        world.PlaceCharacter(caster, new Point3D(130, 128, 0, 0));

        Assert.True(engine.CastStart(caster, SpellType.FireField, Serial.Invalid, tile) > 0);
        caster.SetCastTimerEnd(Environment.TickCount64 - 1);
        engine.TickCastTimer(caster);

        Assert.False(older.IsDeleted);
    }
}
