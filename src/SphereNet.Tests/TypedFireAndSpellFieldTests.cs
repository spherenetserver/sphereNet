using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>An item that is a fire or a spell by TYPE hurts whoever steps into it,
/// the way upstream's location check reads it (CCharAct.cpp:4974): IT_FIRE at its
/// heat (MOREY) through the Fire Field effect curve, IT_SPELL casting MOREX at level
/// MOREY. Only spell-made fields carrying tags did anything, so a fire pit, lava or a
/// scripted field was harmless.</summary>
[Collection("VendorStateSerial")]
public sealed class TypedFireAndSpellFieldTests
{
    private static (GameWorld World, SpellEngine Engine) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.FireField, Name = "Fire Field",
            Flags = SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Damage | SpellFlag.Field,
            EffectBase = 4, EffectScale = 14,
        });
        var engine = new SpellEngine(world, registry);
        Character.FieldTouchHook = engine.ApplyFieldTouch;
        return (world, engine);
    }

    private static Character Victim(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        return ch;
    }

    private static Item Place(GameWorld world, ItemType type, Point3D more)
    {
        var it = world.CreateItem();
        it.BaseId = 0x0FAC;
        it.ItemType = type;
        it.MoreP = more;
        world.PlaceItem(it, new Point3D(1000, 1000, 0, 0));
        return it;
    }

    [Fact]
    public void AFirePitBurnsAtTheBottomOfTheCurve()
    {
        var (world, engine) = Setup();
        var ch = Victim(world);
        var pit = Place(world, ItemType.Fire, Point3D.Zero);

        var result = engine.ApplyFieldTouch(ch, pit);

        Assert.Equal(FieldTouchResult.Handled, result); // not a spell hit: it does not use up the cap
        Assert.Equal(96, ch.Hits);
    }

    [Fact]
    public void HeatRaisesTheBurn()
    {
        var (world, engine) = Setup();
        var ch = Victim(world);
        var hot = Place(world, ItemType.Fire, new Point3D(0, 1000, 0, 0));

        engine.ApplyFieldTouch(ch, hot);

        // Rolled between half and full heat: 9..14 on a 4..14 curve.
        Assert.InRange(100 - ch.Hits, 9, 14);
    }

    [Fact]
    public void AScriptedSpellItemCastsItsSpellAtItsLevel()
    {
        var (world, engine) = Setup();
        var ch = Victim(world);
        var field = Place(world, ItemType.Spell, new Point3D((short)SpellType.FireField, 1000, 0, 0));

        var result = engine.ApplyFieldTouch(ch, field);

        Assert.Equal(FieldTouchResult.SpellHit, result);
        // OnSpellEffect randomizes the level to 500..999 first (CCharSpell.cpp:3631),
        // so the 4..14 curve burns 9..13.
        Assert.InRange(100 - ch.Hits, 9, 13);
    }

    [Fact]
    public void AnInvulnerableCharacterIsNotBurned()
    {
        var (world, engine) = Setup();
        var ch = Victim(world);
        ch.SetStatFlag(StatFlag.Invul);
        engine.ApplyFieldTouch(ch, Place(world, ItemType.Fire, new Point3D(0, 1000, 0, 0)));
        Assert.Equal(100, ch.Hits);
    }
}
