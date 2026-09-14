using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Pets and vendors — PLAN-302's third packet (port plan İŞ-71).
///
/// The pet half turned up a gap that was not about a setting at all: the gate on taking
/// something off somebody else was written as "not another PLAYER", so every NPC in the
/// world was open to everyone. CANUNDRESSPETS is the narrow half of a rule that is
/// mostly about ownership, and the ownership half was missing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PetVendorConfigTests
{
    private readonly ITestOutputHelper _out;
    public PetVendorConfigTests(ITestOutputHelper output) => _out = output;

    private sealed record Bench(GameWorld World, GameClient Client, Character Me,
        SphereNet.Network.State.NetState State);

    private static Bench Stage()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, 7101);
        var client = new GameClient(state, world, new AccountManager(lf),
            lf.CreateLogger<GameClient>());

        // A potion conveys a SPELL, so the drinking half of this packet needs a spell
        // engine to have anything to convey.
        var registry = new SphereNet.Game.Magic.SpellRegistry();
        registry.Register(new SphereNet.Game.Magic.SpellDef
        {
            Id = SpellType.Heal,
            Name = "Heal",
            Flags = SpellFlag.TargChar | SpellFlag.Good | SpellFlag.Heal,
            EffectBase = 20,
            EffectScale = 20,
        });
        client.SetEngines(spellEngine: new SphereNet.Game.Magic.SpellEngine(world, registry));
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.Str = 80; me.Dex = 80; me.Int = 80;
        me.MaxHits = 100; me.Hits = 100; me.MaxStam = 100; me.Stam = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);
        return new Bench(world, client, me, state);
    }

    /// <summary>A creature standing next to the player, wearing a hat and carrying a bag.</summary>
    private static (Character Npc, Item Worn, Item InPack) Creature(Bench b, Serial? owner = null)
    {
        var npc = b.World.CreateCharacter();
        npc.BaseId = 0x00D0;
        npc.MaxHits = 60; npc.Hits = 30;
        if (owner.HasValue) npc.NpcMaster = owner.Value;
        b.World.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));

        var worn = b.World.CreateItem();
        worn.BaseId = 0x1717;
        npc.Equip(worn, Layer.Helm);

        var npcPack = b.World.CreateItem();
        npcPack.ItemType = ItemType.Container;
        npc.Backpack = npcPack;
        npc.Equip(npcPack, Layer.Pack);

        var inPack = b.World.CreateItem();
        inPack.BaseId = 0x0F0E;
        npcPack.AddItem(inPack);

        return (npc, worn, inPack);
    }

    private static bool Lifted(Bench b, Item item)
    {
        // Source-X refuses a pickup out of a container this client has never been
        // shown (CCharAct.cpp:2895), so the pack is opened first - the way a player
        // reaches into a pet's bag.
        var parent = item.ContainedIn.IsValid ? b.World.FindItem(item.ContainedIn) : null;
        if (parent != null)
            b.Client.OpenContainerFromScript(parent);
        b.Client.HandleItemPickup(item.Uid.Value, 1);
        return b.Me.TryGetTag("DRAGGING", out string? held) && held == item.Uid.Value.ToString();
    }

    // ---- who may take things off a creature ------------------------------

    [Fact]
    public void AStrangersCreatureCannotBeStripped()
    {
        var b = Stage();
        var someoneElse = b.World.CreateCharacter();
        var (_, worn, inPack) = Creature(b, someoneElse.Uid);

        _out.WriteLine($"worn lifted={Lifted(b, worn)}");

        // This is the defect the packet found. The gate read "not another PLAYER", so a
        // tamed creature belonging to somebody else was open to anyone who walked past.
        Assert.False(Lifted(b, worn));
        Assert.False(Lifted(b, inPack));
    }

    [Fact]
    public void AnOwnerMayAlwaysReachIntoTheirPetsPack()
    {
        var b = Stage();
        var (_, _, inPack) = Creature(b, b.Me.Uid);

        // Reaching into the pack is an ownership question and is allowed whatever
        // CANUNDRESSPETS says (CCharAct.cpp:2957 only gates the equipped case).
        Character.CanUndressPets = false;
        Assert.True(Lifted(b, inPack));
    }

    [Fact]
    public void UndressingThePetIsTheHalfTheKeyGoverns()
    {
        var b = Stage();
        var (_, worn, _) = Creature(b, b.Me.Uid);

        Character.CanUndressPets = false;
        bool refused = Lifted(b, worn);
        b.Me.SetTag("DRAGGING", "");

        Character.CanUndressPets = true;
        bool allowed = Lifted(b, worn);

        _out.WriteLine($"undress with key off={refused}, on={allowed}");
        Assert.False(refused);
        Assert.True(allowed);
    }

    [Fact]
    public void AGmOutranksTheWholeRule()
    {
        var b = Stage();
        var someoneElse = b.World.CreateCharacter();
        var (_, worn, _) = Creature(b, someoneElse.Uid);

        Character.CanUndressPets = false;
        b.Me.PrivLevel = PrivLevel.GM;

        // Staff take things off anyone below them, which is how a stuck item gets
        // retrieved at all.
        Assert.True(Lifted(b, worn));
    }

    // ---- CANPETSDRINKPOTION ----------------------------------------------

    [Fact]
    public void APotionDroppedOnAPetIsDrunkRatherThanPocketed()
    {
        var b = Stage();
        var (npc, _, _) = Creature(b, b.Me.Uid);
        var potion = b.World.CreateItem();
        potion.ItemType = ItemType.Potion;
        potion.BaseId = 0x0F0C;
        potion.More1 = (uint)SpellType.Heal;
        potion.More2 = 500;
        b.Me.Backpack!.AddItem(potion);
        Character.CanPetsDrinkPotion = true;

        b.Client.HandleItemPickup(potion.Uid.Value, 1);   // the cursor holds it first
        b.Client.HandleItemDrop(potion.Uid.Value, 0, 0, 0, npc.Uid.Value);

        _out.WriteLine($"pet hits {npc.Hits}/{npc.MaxHits}, potion deleted={potion.IsDeleted}");

        // Healing a wounded animal mid-fight is the point of the setting
        // (CCharNPCAct.cpp:2145); pocketing the bottle instead helps nobody.
        Assert.True(potion.IsDeleted);
        Assert.DoesNotContain(npc.Backpack!.Contents, i => i.ItemType == ItemType.Potion);
    }

    [Fact]
    public void WithTheKeyOffTheBottleGoesIntoThePack()
    {
        var b = Stage();
        var (npc, _, _) = Creature(b, b.Me.Uid);
        var potion = b.World.CreateItem();
        potion.ItemType = ItemType.Potion;
        potion.BaseId = 0x0F0C;
        potion.More1 = (uint)SpellType.Heal;
        b.Me.Backpack!.AddItem(potion);
        Character.CanPetsDrinkPotion = false;

        b.Client.HandleItemPickup(potion.Uid.Value, 1);   // the cursor holds it first
        b.Client.HandleItemDrop(potion.Uid.Value, 0, 0, 0, npc.Uid.Value);

        _out.WriteLine($"potion deleted={potion.IsDeleted} in pet pack={npc.Backpack!.Contents.Contains(potion)}");
        Assert.False(potion.IsDeleted);
        Assert.Contains(potion, npc.Backpack!.Contents);
    }

    // ---- NPCSHOVENPC ------------------------------------------------------

    [Fact]
    public void OneCreatureDoesNotWalkThroughAnother()
    {
        var b = Stage();
        var engine = new MovementEngine(b.World);
        var walker = b.World.CreateCharacter();
        walker.BaseId = 0x00D0;
        walker.MaxStam = 100; walker.Stam = 100; walker.MaxHits = 50; walker.Hits = 50;
        b.World.PlaceCharacter(walker, new Point3D(120, 120, 0, 0));

        var blocker = b.World.CreateCharacter();
        blocker.BaseId = 0x00D0;
        blocker.MaxHits = 50; blocker.Hits = 50;
        b.World.PlaceCharacter(blocker, new Point3D(120, 119, 0, 0));

        MovementEngine.NpcShoveNpc = false;
        bool blocked = !engine.TryMove(walker, Direction.North, running: false, sequence: 0);

        MovementEngine.NpcShoveNpc = true;
        bool allowed = engine.TryMove(walker, Direction.North, running: false, sequence: 0);

        _out.WriteLine($"blocked={blocked} allowed={allowed}");

        // Players shove creatures; creatures hold each other up (CCharAct.cpp:4624).
        // Without the rule a guard walks straight through the crowd it is meant to be
        // stuck behind — and a full-stamina NPC always had full stamina.
        Assert.True(blocked);
        Assert.True(allowed);
    }

    [Fact]
    public void AnIndividualCanBeExcusedWithItsOwnTag()
    {
        var b = Stage();
        var engine = new MovementEngine(b.World);
        var walker = b.World.CreateCharacter();
        walker.BaseId = 0x00D0;
        walker.MaxStam = 100; walker.Stam = 100; walker.MaxHits = 50; walker.Hits = 50;
        walker.SetTag("OVERRIDE.SHOVE", "1");
        b.World.PlaceCharacter(walker, new Point3D(130, 130, 0, 0));

        var blocker = b.World.CreateCharacter();
        blocker.BaseId = 0x00D0;
        blocker.MaxHits = 50; blocker.Hits = 50;
        b.World.PlaceCharacter(blocker, new Point3D(130, 129, 0, 0));

        MovementEngine.NpcShoveNpc = false;

        // The global is a default, not a ceiling: one creature may be excused without
        // opening it for every creature on the shard.
        Assert.True(engine.TryMove(walker, Direction.North, running: false, sequence: 0));
    }

    [Fact]
    public void APlayerStillShovesCreatures()
    {
        var b = Stage();
        var engine = new MovementEngine(b.World);
        var blocker = b.World.CreateCharacter();
        blocker.BaseId = 0x00D0;
        blocker.MaxHits = 50; blocker.Hits = 50;
        b.World.PlaceCharacter(blocker, new Point3D(100, 99, 0, 0));

        MovementEngine.NpcShoveNpc = false;

        // The rule is creature-versus-creature. Applying it to players would wall
        // someone in behind their own pet.
        Assert.True(engine.TryMove(b.Me, Direction.North, running: false, sequence: 0));
    }

    // ---- LOSTNPCTELEPORT --------------------------------------------------

    [Fact]
    public void ACreatureThatHasWanderedAbsurdlyFarIsPutBack()
    {
        var b = Stage();
        b.Me.IsOnline = true;
        b.World.AddOnlinePlayer(b.Me);
        b.World.OnTick();
        var ai = new SphereNet.Game.AI.NpcAI(b.World, new SphereNet.Core.Configuration.SphereConfig());
        var npc = b.World.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Animal;
        npc.Hits = npc.MaxHits = 50;
        npc.NextNpcActionTime = 0;
        npc.Home = new Point3D(20, 100, 0, 0);
        npc.HomeDist = 5;
        // Next to the player: an NPC with nobody in view is parked before it acts at
        // all, which would make this test measure the idle gate instead.
        b.World.PlaceCharacter(npc, new Point3D(100, 101, 0, 0));  // 80 tiles from home

        SphereNet.Game.AI.NpcAI.LostNpcTeleport = 50;
        TickUntilItMoves(ai, npc);

        _out.WriteLine($"ended at {npc.Position}");

        // Walking home from 80 tiles away is not a plan (CCharNPCAct.cpp:1547): the
        // creature is simply put back. It is a backstop, not the leash - HOMEDIST is
        // what keeps it near home in the ordinary case.
        Assert.Equal(20, npc.X);
        Assert.Equal(100, npc.Y);
    }

    [Fact]
    public void ACreatureJustOutsideItsLeashWalksBackInstead()
    {
        var b = Stage();
        b.Me.IsOnline = true;
        b.World.AddOnlinePlayer(b.Me);
        b.World.OnTick();
        var ai = new SphereNet.Game.AI.NpcAI(b.World, new SphereNet.Core.Configuration.SphereConfig());
        var npc = b.World.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Animal;
        npc.Hits = npc.MaxHits = 50;
        npc.NextNpcActionTime = 0;
        npc.Home = new Point3D(92, 100, 0, 0);
        npc.HomeDist = 5;
        b.World.PlaceCharacter(npc, new Point3D(102, 100, 0, 0));  // 10 tiles from home

        SphereNet.Game.AI.NpcAI.LostNpcTeleport = 50;
        TickUntilItMoves(ai, npc);

        _out.WriteLine($"ended at {npc.Position}");

        // Past HOMEDIST but nowhere near the backstop, so it walks - one step, not a
        // jump. Teleporting here would make every slightly-strayed creature blink.
        Assert.NotEqual(new Point3D(92, 100, 0, 0), npc.Position);
        Assert.True(Math.Abs(npc.X - 102) <= 1 && Math.Abs(npc.Y - 100) <= 1,
            $"expected a single step from 102,100 but it moved to {npc.Position}");
    }

    // ---- vendor settings --------------------------------------------------

    [Fact]
    public void TheMarkupDefaultIsWhatTheVendorFallsBackTo()
    {
        var b = Stage();
        var vendor = b.World.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        b.World.PlaceCharacter(vendor, new Point3D(105, 100, 0, 0));

        VendorEngine.DefaultVendorMarkup = 40;
        int fallback = VendorEngine.GetVendorMarkup(vendor);

        vendor.SetTag("VENDORMARKUP", "5");
        int own = VendorEngine.GetVendorMarkup(vendor);

        _out.WriteLine($"fallback={fallback} own tag={own}");

        // Vendor tag, then region, then the setting (CCharNPCStatus.cpp:366). The
        // setting had a consumer and no feeder until this packet wired the ini to it.
        Assert.Equal(40, fallback);
        Assert.Equal(5, own);
    }

    [Fact]
    public void TheSellCapIsAppliedWhenTheListIsBuilt()
    {
        var b = Stage();
        var vendor = b.World.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        b.World.PlaceCharacter(vendor, new Point3D(102, 100, 0, 0));

        var stock = b.World.CreateItem();
        stock.ItemType = ItemType.Container;
        vendor.Equip(stock, Layer.VendorStock);
        var pile = b.World.CreateItem();
        pile.BaseId = 0x0F0E;
        pile.Amount = 900;
        pile.SetTag("PRICE", "10");
        stock.AddItem(pile);

        VendorEngine.VendorMaxSell = 100;
        b.Client.ItemUse.OpenVendorBuy(vendor);
        ushort capped = OfferedAmount(b, pile.Uid.Value);

        VendorEngine.VendorMaxSell = 0;                 // no cap
        b.Client.ItemUse.OpenVendorBuy(vendor);
        ushort uncapped = OfferedAmount(b, pile.Uid.Value);

        _out.WriteLine($"offered capped={capped} uncapped={uncapped}; stock still {pile.Amount}");

        // The cap shapes the OFFER, not the stock: the shop never advertises more than
        // the shard allows (send.cpp:1249) and the pile keeps its 900. Trimming the
        // stock instead would destroy goods the vendor owns.
        Assert.Equal(100, capped);
        Assert.Equal(900, uncapped);
        Assert.Equal(900, pile.Amount);
    }

    /// <summary>Tick an idle creature until it does something. An animal only wanders
    /// on some ticks, so a single tick would be measuring the cadence rather than the
    /// destination.</summary>
    private static void TickUntilItMoves(SphereNet.Game.AI.NpcAI ai, Character npc)
    {
        var start = npc.Position;
        for (int i = 0; i < 200 && npc.Position == start; i++)
        {
            npc.NextNpcActionTime = 0;
            ai.OnTickAction(npc);
        }
    }

    /// <summary>The amount the shop listing advertises for one stock line, read out of
    /// the 0x3C container-contents packet the client actually receives.</summary>
    private static ushort OfferedAmount(Bench b, uint serial)
    {
        ushort found = 0;
        foreach (var pkt in TestHarness.GetQueuedPackets(b.State))
        {
            var span = pkt.Span;
            if (span.Length < 5 || span[0] != 0x3C) continue;
            int count = (span[3] << 8) | span[4];
            if (count == 0) continue;
            // 20 bytes per entry for a 7.0.9+ client (grid index), 19 without it. The
            // packet says which it used by its own length rather than us guessing.
            int perItem = (span.Length - 5) / count;
            for (int i = 0; i < count; i++)
            {
                int o = 5 + i * perItem;
                if (o + perItem > span.Length) break;
                uint s = (uint)((span[o] << 24) | (span[o + 1] << 16) | (span[o + 2] << 8) | span[o + 3]);
                if (s != serial) continue;
                found = (ushort)((span[o + 7] << 8) | span[o + 8]);
            }
        }
        return found;
    }
}

