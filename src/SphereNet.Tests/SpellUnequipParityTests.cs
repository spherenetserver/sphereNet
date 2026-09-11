using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Casting with your hands full (port plan İŞ-22 / PLAN-403).
///
/// With EQUIPPEDCAST off the reference does not refuse the cast - it EMPTIES THE
/// HANDS. Spell_Unequip (CCharSpell.cpp:2827) bounces each held item into the pack
/// and only fails when the item will not go: frozen hands under
/// MAGICF_NOCASTFROZENHANDS or MAGICF_CASTPARALYZED, an item that cannot be moved at
/// all, or nowhere to put it. A spellbook, a wand and anything the pack flagged
/// CAN_I_EQUIPONCAST stay where they are.
///
/// This engine fizzled the spell instead, and that is the live shard's own setting:
/// its EQUIPPEDCAST is 0, so a player holding a weapon simply could not cast.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellUnequipParityTests : IDisposable
{
    private readonly bool _savedEquippedCast = Character.EquippedCastEnabled;
    private readonly int _savedMagicFlags = Character.MagicFlags;
    private readonly bool _savedReagents = Character.ReagentsRequiredEnabled;
    private readonly bool _savedSpellbook = Character.SpellbookRequiredEnabled;

    /// <summary>Called from the TEST BODY (through Setup), not the constructor:
    /// the assembly-wide ResetEngineStatics hook runs between the two and puts
    /// SpellbookRequiredEnabled back to true.</summary>
    private static void PinSpellStatics()
    {
        Character.EquippedCastEnabled = false;   // the live shard's own setting
        Character.MagicFlags = 0;
        // The hands are what is under test; the reagent and spellbook gates are
        // separate refusals that would mask it.
        Character.ReagentsRequiredEnabled = false;
        Character.SpellbookRequiredEnabled = false;
    }

    public void Dispose()
    {
        Character.EquippedCastEnabled = _savedEquippedCast;
        Character.MagicFlags = _savedMagicFlags;
        Character.ReagentsRequiredEnabled = _savedReagents;
        Character.SpellbookRequiredEnabled = _savedSpellbook;
    }

    private static (GameWorld World, SpellEngine Engine, Character Caster) Setup()
    {
        PinSpellStatics();
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Heal,
            Flags = SpellFlag.TargChar | SpellFlag.Heal,
            ManaCost = 0,
            CastTimeBase = 5,
            EffectBase = 20,
            EffectScale = 20,
        });

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.Player;
        // Max first: the pool setter clamps to the maximum, so the chained
        // assignment would have left the mana at zero.
        caster.MaxMana = 100; caster.Mana = 100;
        caster.MaxHits = 100; caster.Hits = 100;
        caster.SetSkill(SkillType.Magery, 2000);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        caster.Backpack = pack;
        caster.Equip(pack, Layer.Pack);

        return (world, new SpellEngine(world, registry), caster);
    }

    private static Item Equip(GameWorld world, Character ch, ItemType type, Layer layer,
        ushort baseId = 0x0F5E)
    {
        var item = world.CreateItem();
        item.ItemType = type;
        item.BaseId = baseId;
        ch.Equip(item, layer);
        return item;
    }

    // ---- the hands are emptied, not the cast refused -----------------------

    [Fact]
    public void AHeldWeaponGoesToThePackAndTheCastProceeds()
    {
        var (world, engine, caster) = Setup();
        var sword = Equip(world, caster, ItemType.WeaponSword, Layer.OneHanded);

        // The refusal message rides along in the assert: a cast that stops for some
        // other reason should say which, not just fail as a bare false.
        string? why = null;
        engine.OnSysMessage = (_, m) => why = m;
        int castMs = engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position);
        Assert.True(castMs > 0, $"cast refused: {why ?? "(no message)"}");

        Assert.Null(caster.GetEquippedItem(Layer.OneHanded));
        Assert.Contains(sword, caster.Backpack!.Contents);
    }

    [Fact]
    public void BothHandsAreEmptied()
    {
        var (world, engine, caster) = Setup();
        var twoHander = Equip(world, caster, ItemType.WeaponSword, Layer.TwoHanded, 0x13B9);

        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);

        Assert.Null(caster.GetEquippedItem(Layer.TwoHanded));
        Assert.Contains(twoHander, caster.Backpack!.Contents);
    }

    // ---- what stays in hand ------------------------------------------------

    [Theory]
    [InlineData(ItemType.Spellbook)]
    [InlineData(ItemType.Wand)]
    public void TheBookYouCastFromAndAWandStayInHand(ItemType type)
    {
        var (world, engine, caster) = Setup();
        var held = Equip(world, caster, type, Layer.OneHanded);

        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);

        Assert.Same(held, caster.GetEquippedItem(Layer.OneHanded));
        Assert.DoesNotContain(held, caster.Backpack!.Contents);
    }

    [Fact]
    public void AShieldIsNotAHandTheCastNeeds()
    {
        var (world, engine, caster) = Setup();
        var shield = Equip(world, caster, ItemType.Shield, Layer.TwoHanded, 0x1B76);

        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);

        Assert.Same(shield, caster.GetEquippedItem(Layer.TwoHanded));
    }

    // ---- when it does refuse ------------------------------------------------

    [Fact]
    public void AnUnmovableItemStopsTheCast()
    {
        // Source-X checks CanMoveItem before the bounce (CCharSpell.cpp:2847): a
        // cursed weapon that will not leave the hand takes the cast with it.
        var (world, engine, caster) = Setup();
        var cursed = Equip(world, caster, ItemType.WeaponSword, Layer.OneHanded);
        cursed.SetAttr(ObjAttributes.Cursed);

        Assert.Equal(-1, engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position));
        Assert.Same(cursed, caster.GetEquippedItem(Layer.OneHanded));
    }

    [Fact]
    public void FrozenHandsStopTheCastWhenTheFlagSaysSo()
    {
        var (world, engine, caster) = Setup();
        Character.MagicFlags = (int)MagicConfigFlags.NoCastFrozenHands;
        Equip(world, caster, ItemType.WeaponSword, Layer.OneHanded);
        caster.SetStatFlag(StatFlag.Freeze);

        Assert.Equal(-1, engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position));
        Assert.NotNull(caster.GetEquippedItem(Layer.OneHanded));
    }

    [Fact]
    public void CastParalyzedKeepsAWandInAFrozenHand()
    {
        // MAGICF_CASTPARALYZED lets a frozen caster keep a wand or book
        // (CCharSpell.cpp:2839) - but not an ordinary weapon (:2841).
        var (world, engine, caster) = Setup();
        Character.MagicFlags = (int)MagicConfigFlags.CastParalyzed;
        var wand = Equip(world, caster, ItemType.Wand, Layer.OneHanded);
        caster.SetStatFlag(StatFlag.Freeze);

        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
        Assert.Same(wand, caster.GetEquippedItem(Layer.OneHanded));
    }

    [Fact]
    public void CastParalyzedStillRefusesAFrozenHandHoldingAWeapon()
    {
        var (world, engine, caster) = Setup();
        Character.MagicFlags = (int)MagicConfigFlags.CastParalyzed;
        Equip(world, caster, ItemType.WeaponSword, Layer.OneHanded);
        caster.SetStatFlag(StatFlag.Freeze);

        Assert.Equal(-1, engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position));
    }

    [Fact]
    public void AnItemFlaggedEquipOnCastStaysInHand()
    {
        // CAN_I_EQUIPONCAST (Source-X CBase.h:61) is the pack's way of saying a
        // held item survives a cast with EQUIPPEDCAST off. Nothing read it before.
        string defFile = Path.Combine(Path.GetTempPath(), $"sphnet_eoc_{Guid.NewGuid():N}.scp");
        File.WriteAllText(defFile, """
            [ITEMDEF 0f5e]
            DEFNAME=i_staff_eoc
            NAME=Channeling staff
            TYPE=t_weapon_mace_staff
            CAN=08000000
            """);
        try
        {
            var resources = new SphereNet.Scripting.Resources.ResourceHolder(
                LoggerFactory.Create(_ => { }).CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
            resources.LoadResourceFile(defFile);
            new SphereNet.Game.Definitions.DefinitionLoader(
                resources, new SpellRegistry()).LoadAll();

            var (world, engine, caster) = Setup();
            var staff = Equip(world, caster, ItemType.WeaponMaceStaff, Layer.OneHanded);

            Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
            Assert.Same(staff, caster.GetEquippedItem(Layer.OneHanded));
        }
        finally { File.Delete(defFile); }
    }

    // ---- the setting still means something ----------------------------------

    [Fact]
    public void WithEquippedCastOnTheWeaponStaysInHand()
    {
        var (world, engine, caster) = Setup();
        Character.EquippedCastEnabled = true;
        var sword = Equip(world, caster, ItemType.WeaponSword, Layer.OneHanded);

        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
        Assert.Same(sword, caster.GetEquippedItem(Layer.OneHanded));
    }
}
