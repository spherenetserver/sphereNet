using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.MapData.Tiles;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Item use paths the reference runs natively and SphereNet did not: step-activated
/// switches (CheckLocationEffects, CCharAct.cpp:5026), raw food and garbage through
/// Use_Eat (CCharUse.cpp:1846), the spyglass (CCharUse.cpp:1914 / CItem::Use_SpyGlass),
/// the silent talisman equip (CCharUse.cpp:1897), Telekinesis as a remote use
/// (CCharSpell.cpp:3126) and Magic Trap's lack of any hard-coded effect.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemUseStepSpellParityTests
{
    // ------------------------------------------------------------ step switch

    private static (GameWorld World, Character Ch) MovementSetup()
    {
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Dex = 50; ch.MaxStam = 50; ch.Stam = 50;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (world, ch);
    }

    private static Item Switch(GameWorld world, short step)
    {
        var lever = world.CreateItem();
        lever.BaseId = 0x108F;
        lever.More1 = 0x1090;                       // the other state
        lever.ItemType = ItemType.Switch;
        lever.MoreP = new Point3D(step, 0, 0, 0);   // MOREX = m_itSwitch.m_wStep
        world.PlaceItem(lever, new Point3D(101, 100, 0, 0));
        return lever;
    }

    [Fact]
    public void StepSwitch_WalkingOntoItFlipsIt()
    {
        var (world, ch) = MovementSetup();
        var lever = Switch(world, step: 1);

        Assert.True(new MovementEngine(world).TryMove(ch, Direction.East, running: false, sequence: 1));

        Assert.Equal(0x1090, lever.BaseId);
        Assert.Equal(0x108Fu, lever.More1);
    }

    [Fact]
    public void Switch_WithoutStepFlag_IsNotWorkedByWalking()
    {
        var (world, ch) = MovementSetup();
        var lever = Switch(world, step: 0);

        Assert.True(new MovementEngine(world).TryMove(ch, Direction.East, running: false, sequence: 1));

        Assert.Equal(0x108F, lever.BaseId);
    }

    [Fact]
    public void StepSwitch_GoesThroughTheUseHookWhenWired()
    {
        var (world, ch) = MovementSetup();
        var lever = Switch(world, step: 1);
        var used = new List<(Character, Item)>();
        var engine = new MovementEngine(world)
        {
            OnStepUseItem = (who, what) => { used.Add((who, what)); return true; },
        };

        Assert.True(engine.TryMove(ch, Direction.East, running: false, sequence: 1));

        Assert.Single(used);
        Assert.Same(ch, used[0].Item1);
        Assert.Same(lever, used[0].Item2);
        Assert.Equal(0x108F, lever.BaseId);   // the hook owned the use; no second flip
    }

    [Fact]
    public void StepSwitch_ClientUseFlipsAndFollowsTheLink()
    {
        var (client, world, player) = ClientSetup();
        var lever = world.CreateItem();
        lever.BaseId = 0x108F;
        lever.More1 = 0x1090;
        lever.ItemType = ItemType.Switch;
        world.PlaceItem(lever, player.Position);
        var linked = world.CreateItem();
        linked.BaseId = 0x1091;
        linked.More1 = 0x1092;
        linked.ItemType = ItemType.Switch;
        world.PlaceItem(linked, new Point3D(105, 100, 0, 0));
        lever.Link = linked.Uid;

        client.UseSteppedSwitch(lever);

        Assert.Equal(0x1090, lever.BaseId);
        Assert.Equal(0x1092, linked.BaseId);
    }

    // ------------------------------------------------------ raw food / garbage

    [Theory]
    [InlineData(ItemType.MeatRaw)]
    [InlineData(ItemType.FoodRaw)]
    [InlineData(ItemType.Garbage)]
    public void RawFoodAndGarbage_OutsideTheDiet_AreRefusedAndKept(ItemType type)
    {
        var (client, world, player) = ClientSetup();
        var food = PackItem(world, player, type, amount: 3);
        player.Food = 0;
        TestHarness.ClearQueuedPackets(client.NetState);

        client.HandleDoubleClick(food.Uid.Value);

        Assert.Equal(3, food.Amount);
        Assert.Equal(0, player.Food);
        var text = SentText(client);
        Assert.Contains(ServerMessages.Get(Msg.FoodRcanteat), text);
        Assert.DoesNotContain("must be cooked", text);
    }

    [Theory]
    [InlineData(ItemType.MeatRaw)]
    [InlineData(ItemType.Garbage)]
    public void RawFoodAndGarbage_InTheDiet_AreEaten(ItemType type)
    {
        var (client, world, player) = ClientSetup();
        Character.NpcCanEatFood = (_, item) => item.ItemType == type;   // e.g. a wolf / a goat
        var food = PackItem(world, player, type, amount: 3);
        player.Food = 0;

        client.HandleDoubleClick(food.Uid.Value);

        Assert.Equal(2, food.Amount);
        Assert.True(player.Food > 0);
    }

    [Fact]
    public void CookedFood_OutsideTheDiet_IsRefused()
    {
        // Food_CanEat governs every Use_Eat, cooked food included (CCharUse.cpp:940).
        var (client, world, player) = ClientSetup();
        Character.NpcCanEatFood = (_, item) => item.ItemType == ItemType.MeatRaw;
        var bread = PackItem(world, player, ItemType.Food, amount: 2);
        player.Food = 0;

        client.HandleDoubleClick(bread.Uid.Value);

        Assert.Equal(2, bread.Amount);
        Assert.Contains(ServerMessages.Get(Msg.FoodRcanteat), SentText(client));
    }

    // ---------------------------------------------------------------- spyglass

    [Fact]
    public void Spyglass_ReportsBothMoonPhases()
    {
        var (client, world, player) = ClientSetup();
        world.SetWorldClockMinutes(53);   // Trammel 53%105*8/105 = 4, Felucca 53*8/840 = 0
        var glass = PackItem(world, player, ItemType.SpyGlass, amount: 1);
        TestHarness.ClearQueuedPackets(client.NetState);

        client.HandleDoubleClick(glass.Uid.Value);

        var text = SentText(client);
        Assert.Contains("Trammel is in the full moon phase.", text);
        Assert.Contains("Felucca is in the new moon phase.", text);
        Assert.DoesNotContain(ServerMessages.Get(Msg.ItemuseTelescope), text);
        Assert.DoesNotContain("see any land", text);   // not aboard a ship
    }

    [Fact]
    public void Spyglass_OnAShip_LooksForLandShipsAndCreatures()
    {
        var (client, world, player) = ClientSetup();
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: -5, landTile: 0xA8);   // open sea
        world.MapData = map;
        var deck = new Region { Name = "deck", Flags = RegionFlag.Ship, MapIndex = 0 };
        deck.AddRect(95, 95, 105, 105);
        world.AddRegion(deck);

        var other = world.CreateItem();
        other.ItemType = ItemType.Ship;
        other.Name = "galleon";
        world.PlaceItem(other, new Point3D(112, 100, 0, 0));      // 12 tiles east
        var swimmer = world.CreateCharacter();
        world.PlaceCharacter(swimmer, new Point3D(100, 115, 0, 0)); // off the ship, south

        var glass = PackItem(world, player, ItemType.SpyGlass, amount: 1);
        string view = ClientItemUseHandler.DescribeSpyglassView(world, player, glass);

        Assert.Contains(ServerMessages.Get(Msg.UseSpyglassNoLand), view);
        Assert.Contains("You can see a galleon to the East.", view);
        Assert.Contains("You see a creature to the South", view);

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleDoubleClick(glass.Uid.Value);
        Assert.Contains(ServerMessages.Get(Msg.UseSpyglassNoLand), SentText(client));
    }

    [Fact]
    public void Spyglass_AtSea_PointsAtTheNearestLand()
    {
        var (_, world, player) = ClientSetup();
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: -5, landTile: 0xA8);
        world.MapData = map;
        var glass = PackItem(world, player, ItemType.SpyGlass, amount: 1);

        // Every tile is the same synthetic water, so no land: the report says so.
        string view = ClientItemUseHandler.DescribeSpyglassView(world, player, glass);
        Assert.StartsWith(ServerMessages.Get(Msg.UseSpyglassNoLand), view);
    }

    // ---------------------------------------------------------------- talisman

    [Fact]
    public void Talisman_DoubleClick_IsSilent()
    {
        var (client, world, player) = ClientSetup();
        var talisman = PackItem(world, player, ItemType.Talisman, amount: 1);
        TestHarness.ClearQueuedPackets(client.NetState);

        client.HandleDoubleClick(talisman.Uid.Value);

        Assert.DoesNotContain(ServerMessages.Get(Msg.ItemuseCantthink), SentText(client));
    }

    // ------------------------------------------------------------- telekinesis

    private static (SpellEngine Engine, Character Caster) TelekinesisSetup(GameWorld world)
    {
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Telekinesis,
            Flags = SpellFlag.TargObj,
            ManaCost = 0,
            CastTimeBase = 1,
        });
        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.MaxMana = 100;
        caster.Mana = 100;
        caster.SetSkill(SkillType.Magery, 2000);
        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        book.More1 = 1u << ((int)SpellType.Telekinesis - 1);
        caster.Equip(book, Layer.OneHanded);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        return (new SpellEngine(world, registry), caster);
    }

    [Fact]
    public void Telekinesis_UsesTheTargetRemotely()
    {
        var world = TestHarness.CreateWorld();
        var (engine, caster) = TelekinesisSetup(world);
        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        world.PlaceItem(chest, new Point3D(104, 100, 0, 0));
        var used = new List<Item>();
        var crimes = new List<Item>();
        engine.OnUseObject = (_, item) => used.Add(item);
        engine.OnCorpseCrimeCheck = (_, corpse) => crimes.Add(corpse);

        Assert.True(engine.CastStart(caster, SpellType.Telekinesis, chest.Uid, chest.Position) > 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal([chest], used);
        Assert.Empty(crimes);
    }

    [Fact]
    public void Telekinesis_IntoAnotherPlayersCorpse_ChecksTheCrimeAndReveals()
    {
        var world = TestHarness.CreateWorld();
        var (engine, caster) = TelekinesisSetup(world);
        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        world.PlaceCharacter(victim, new Point3D(110, 110, 0, 0));
        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.SetTag("OWNER_UID", victim.Uid.Value.ToString());
        world.PlaceItem(corpse, new Point3D(103, 100, 0, 0));
        var used = new List<Item>();
        var crimes = new List<Item>();
        engine.OnUseObject = (_, item) => used.Add(item);
        engine.OnCorpseCrimeCheck = (_, c) => crimes.Add(c);

        Assert.True(engine.CastStart(caster, SpellType.Telekinesis, corpse.Uid, corpse.Position) > 0);
        caster.SetStatFlag(StatFlag.Hidden);
        Assert.True(engine.CastDone(caster));

        Assert.Equal([corpse], crimes);
        Assert.False(caster.IsStatFlag(StatFlag.Hidden));
        Assert.Equal([corpse], used);
    }

    [Fact]
    public void Telekinesis_IntoOwnCorpse_IsNoCrime()
    {
        var world = TestHarness.CreateWorld();
        var (engine, caster) = TelekinesisSetup(world);
        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.SetTag("OWNER_UID", caster.Uid.Value.ToString());
        world.PlaceItem(corpse, new Point3D(103, 100, 0, 0));
        var crimes = new List<Item>();
        engine.OnCorpseCrimeCheck = (_, c) => crimes.Add(c);

        Assert.True(engine.CastStart(caster, SpellType.Telekinesis, corpse.Uid, corpse.Position) > 0);
        Assert.True(engine.CastDone(caster));

        Assert.Empty(crimes);
    }

    [Fact]
    public void UseObject_WithoutTouchTest_ReachesAFarContainer()
    {
        var (client, world, player) = ClientSetup();
        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        world.PlaceItem(chest, new Point3D(106, 100, 0, 0));   // out of reach
        TestHarness.ClearQueuedPackets(client.NetState);

        client.HandleDoubleClick(chest.Uid.Value);
        Assert.DoesNotContain(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] == 0x24);

        TestHarness.ClearQueuedPackets(client.NetState);
        client.UseObject(chest.Uid.Value, testTouch: false);
        Assert.Contains(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] == 0x24);
    }

    // -------------------------------------------------------------- magic trap

    [Fact]
    public void MagicTrap_HasNoHardCodedEffect_AndOpeningSpringsNothing()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.MagicTrap,
            Flags = SpellFlag.TargObj,
            ManaCost = 0,
            CastTimeBase = 1,
        });
        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.MaxMana = 100;
        caster.Mana = 100;
        caster.SetSkill(SkillType.Magery, 2000);
        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        book.More1 = 1u << ((int)SpellType.MagicTrap - 1);
        caster.Equip(book, Layer.OneHanded);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        world.PlaceItem(chest, new Point3D(101, 100, 0, 0));
        var engine = new SpellEngine(world, registry);

        Assert.True(engine.CastStart(caster, SpellType.MagicTrap, chest.Uid, chest.Position) > 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(ItemType.Container, chest.ItemType);
        Assert.False(chest.TryGetTag("TRAPPED", out _));

        // Opening a container runs no native trap either (CClientUse.cpp:213).
        var (client, world2, player) = ClientSetup();
        player.MaxHits = 50;
        player.Hits = 50;
        var box = world2.CreateItem();
        box.ItemType = ItemType.Container;
        box.SetTag("TRAP_DAMAGE", "20");
        world2.PlaceItem(box, player.Position);

        client.HandleDoubleClick(box.Uid.Value);

        Assert.Equal(50, player.Hits);
    }

    // ----------------------------------------------------------------- helpers

    private static (GameClient Client, GameWorld World, Character Player) ClientSetup()
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(10_000, 20_000));
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Tester";
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        return (client, world, player);
    }

    private static Item PackItem(GameWorld world, Character player, ItemType type, ushort amount)
    {
        var item = world.CreateItem();
        item.ItemType = type;
        item.Amount = amount;
        player.Backpack!.AddItem(item);
        return item;
    }

    /// <summary>Every speech line queued for the client, unicode (0xAE) and ASCII (0x1C).</summary>
    private static string SentText(GameClient client)
    {
        var sb = new StringBuilder();
        foreach (var pkt in TestHarness.GetQueuedPackets(client.NetState))
        {
            var span = pkt.Span;
            if (span.Length > 48 && span[0] == 0xAE)
                sb.Append(Encoding.BigEndianUnicode.GetString(span[48..])).Append('\n');
            else if (span.Length > 44 && span[0] == 0x1C)
                sb.Append(Encoding.ASCII.GetString(span[44..])).Append('\n');
        }
        return sb.ToString().Replace("\0", "");
    }
}
