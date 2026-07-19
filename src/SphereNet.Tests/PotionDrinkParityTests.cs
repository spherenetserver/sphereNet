using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Audit findings (wiki/test.txt #3): potions parity with Source-X Use_Drink.
/// A potion conveys the SPELL stored in MORE1 at strength MORE2 through
/// OnSpellEffect — stat potions become TIMED effects (the old code did a
/// permanent Str += 10), and a liquid with no resolvable effect is just a
/// drink (the old default made any tagless liquid — including a full water
/// pitcher — a free heal potion).
/// </summary>
[Collection("VendorStateSerial")]
public sealed class PotionDrinkParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (SphereNet.Game.Clients.GameClient Client, Character Player, SpellEngine Spells)
        CreateDrinker(GameWorld world, params SpellDef[] spells)
    {
        var registry = new SpellRegistry();
        foreach (var def in spells)
            registry.Register(def);
        var engine = new SpellEngine(world, registry);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);

        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2610);
        client.SetEngines(spellEngine: engine);
        TestHarness.AttachCharacter(client, player);
        return (client, player, engine);
    }

    [Fact]
    public void StrengthPotion_AppliesTimedSpellEffect_NotPermanentStat()
    {
        var world = CreateWorld();
        var (client, player, _) = CreateDrinker(world, new SpellDef
        {
            Id = SpellType.Strength,
            Name = "Strength",
            Flags = SpellFlag.TargChar | SpellFlag.Good | SpellFlag.Bless,
            EffectBase = 50,
            EffectScale = 50,
            DurationBase = 60,
        });

        var potion = world.CreateItem();
        potion.BaseId = 0x0F09;
        potion.ItemType = ItemType.Potion;
        potion.More1 = (uint)SpellType.Strength; // Source-X m_itPotion.m_Type
        potion.More2 = 500;                      // MORE2=50.0 alchemy quality
        player.Backpack!.AddItem(potion);

        short strBefore = player.Str;
        client.HandleDoubleClick(potion.Uid.Value);

        Assert.True(player.Str > strBefore, "strength potion applied no effect");
        Assert.True(potion.IsDeleted, "potion was not consumed");
    }

    [Fact]
    public void TaglessLiquid_IsJustADrink_NoDefaultHeal()
    {
        var world = CreateWorld();
        var (client, player, _) = CreateDrinker(world);
        player.MaxHits = 100;
        player.Hits = 40;

        // A full water pitcher: no MORE1 spell, no POTION_TYPE — under the old
        // code this healed 10 hits as the "default" potion family.
        var pitcher = world.CreateItem();
        pitcher.BaseId = 0x0FF8;
        pitcher.ItemType = ItemType.Pitcher;
        player.Backpack!.AddItem(pitcher);

        client.HandleDoubleClick(pitcher.Uid.Value);

        Assert.Equal(40, player.Hits);
        Assert.True(pitcher.IsDeleted, "drink was not consumed");
    }

    [Fact]
    public void More2_ParsesSphereFixedPointDecimals()
    {
        var world = CreateWorld();
        var potion = world.CreateItem();
        Assert.True(potion.TrySetProperty("MORE2", "50.0"));
        Assert.Equal(500u, potion.More2);
        Assert.True(potion.TrySetProperty("MORE2", "130.0"));
        Assert.Equal(1300u, potion.More2);
        Assert.True(potion.TrySetProperty("MORE2", "700"));
        Assert.Equal(700u, potion.More2);
    }
}
