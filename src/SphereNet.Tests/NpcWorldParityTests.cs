using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// NPC and world behaviours checked against Source-X:
/// the player-vendor BOUGHT / SAMPLES / STOCK boxes (CCharNPCPet.cpp:328-354), the
/// body-guessed default brain (GetNPCBrainAuto, CCharStatus.cpp:563), the snoop
/// witness loop that never reaches the mark (CheckCrimeSeen, CCharFight.cpp:117),
/// NPC loot memory (NPC_LootMemory, CCharNPCAct.cpp:1574), the guard and townsfolk
/// DEFMSG lines (CCharNPCAct.cpp:753/824) and the whole-word guard call
/// (FindStrWord, sstring.cpp:751).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcWorldParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    // ================================================================ 1. vendor boxes

    private sealed record VendorBench(GameWorld World, Network.State.NetState State, GameClient Client,
        Character Owner, Character Vendor);

    private static VendorBench OwnedVendor()
    {
        var world = CreateWorld();
        var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, 7001);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.Name = "owner";
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, owner);

        var vendor = world.CreateCharacter();
        vendor.Name = "bob";
        vendor.BodyId = 0x0190;
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(101, 100, 0, 0));
        Assert.True(vendor.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: false));
        return new VendorBench(world, state, client, owner, vendor);
    }

    private static bool PetCommand(GameClient client, string text) =>
        (bool)typeof(GameClient)
            .GetMethod("TryHandlePetCommand", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [text])!;

    [Theory]
    [InlineData("stock", Layer.VendorStock)]
    [InlineData("bought", Layer.VendorExtra)]
    [InlineData("samples", Layer.VendorBuy)]
    public void TheOwnerOpensTheVendorBoxTheCommandNames(string verb, Layer layer)
    {
        var b = OwnedVendor();
        var old = VendorEngine.World;
        VendorEngine.World = b.World;
        try
        {
            Assert.True(PetCommand(b.Client, $"bob {verb}"));

            var box = b.Vendor.GetEquippedItem(layer);
            Assert.NotNull(box);
            Assert.Equal(ItemType.EqVendorBox, box!.ItemType);
            Assert.Equal((ushort)0x408D, box.BaseId); // ITEMID_VENDOR_BOX

            var open = TestHarness.GetQueuedPackets(b.State).Where(p => p.Span[0] == 0x24).ToList();
            Assert.Contains(open, p =>
                (uint)((p.Span[1] << 24) | (p.Span[2] << 16) | (p.Span[3] << 8) | p.Span[4]) == box.Uid.Value);
        }
        finally { VendorEngine.World = old; }
    }

    [Fact]
    public void ANonVendorPetHasNoVendorBoxes()
    {
        var b = OwnedVendor();
        b.Vendor.NpcBrain = NpcBrainType.Animal;
        var old = VendorEngine.World;
        VendorEngine.World = b.World;
        try
        {
            PetCommand(b.Client, "bob stock");
            Assert.Null(b.Vendor.GetEquippedItem(Layer.VendorStock));
        }
        finally { VendorEngine.World = old; }
    }

    [Fact]
    public void PriceIsAVendorVerb_ANonVendorPetDoesNotOfferATarget()
    {
        var b = OwnedVendor();
        b.Vendor.NpcBrain = NpcBrainType.Animal;

        PetCommand(b.Client, "bob price");

        Assert.Equal(0u, b.Client.ActiveTargetCursorId);
    }

    [Fact]
    public void PriceOnlyTakesAnItemTheVendorHoldsForSale()
    {
        // Source-X NPC_SetVendorPrice (CCharNPCPet.cpp:823): the item must sit in one
        // of the vendor's boxes. Pricing an item in the owner's own pack was the
        // first half of a gold-minting sale.
        var b = OwnedVendor();
        var old = VendorEngine.World;
        VendorEngine.World = b.World;
        try
        {
            var pack = b.World.CreateItem();
            pack.BaseId = 0x0E75;
            pack.ItemType = ItemType.Container;
            b.Owner.Equip(pack, Layer.Pack);
            var mine = b.World.CreateItem();
            mine.BaseId = 0x0F0E;
            pack.AddItem(mine);

            PetCommand(b.Client, "bob price");
            b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, mine.Uid.Value, 0, 0, 0, 0);
            Assert.Empty(b.Client.Dialogs.PendingInputDlg);

            var stock = VendorEngine.GetVendorBox(b.Vendor, Layer.VendorStock)!;
            var forSale = b.World.CreateItem();
            forSale.BaseId = 0x0F0E;
            stock.AddItem(forSale);

            PetCommand(b.Client, "bob price");
            b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, forSale.Uid.Value, 0, 0, 0, 0);
            Assert.Contains(b.Client.Dialogs.PendingInputDlg.Keys, k => k.Item1 == forSale.Uid.Value);
        }
        finally { VendorEngine.World = old; }
    }

    [Fact]
    public void APlayerVendorsStockSurvivesASaveWhileATemplateVendorsDoesNot()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_pvstock_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var old = VendorEngine.World;
        try
        {
            var b = OwnedVendor();
            VendorEngine.World = b.World;
            var stock = VendorEngine.GetVendorBox(b.Vendor, Layer.VendorStock)!;
            var goods = b.World.CreateItem();
            goods.BaseId = 0x0F0E;
            Assert.True(stock.TryAddItem(goods));

            var shop = b.World.CreateCharacter();
            shop.BodyId = 0x0190;
            shop.NpcBrain = NpcBrainType.Vendor;
            b.World.PlaceCharacter(shop, new Point3D(110, 100, 0, 0));
            var virtualStock = VendorEngine.GetVendorBox(shop, Layer.VendorStock)!;
            var template = b.World.CreateItem();
            template.BaseId = 0x0F0E;
            Assert.True(virtualStock.TryAddItem(template));

            var saver = new WorldSaver(LoggerFactory.Create(_ => { })) { Format = SaveFormat.Text, ShardCount = 0 };
            Assert.True(saver.Save(b.World, dir));
            var dst = CreateWorld();
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(dst, dir);

            Assert.NotNull(dst.FindItem(stock.Uid));
            Assert.NotNull(dst.FindItem(goods.Uid));
            Assert.Null(dst.FindItem(virtualStock.Uid));
            Assert.Null(dst.FindItem(template.Uid));
        }
        finally
        {
            VendorEngine.World = old;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void APlayerVendorIsNeverRestockedFromATemplate()
    {
        var b = OwnedVendor();
        var old = VendorEngine.World;
        VendorEngine.World = b.World;
        try
        {
            b.Vendor.SetTag("VENDORINV", "0x0F0E:10");
            VendorEngine.RestockVendor(b.Vendor);
            var stock = b.Vendor.GetEquippedItem(Layer.VendorStock);
            Assert.True(stock == null || b.World.GetContainerContents(stock.Uid).All(i => i.IsDeleted));
        }
        finally { VendorEngine.World = old; }
    }

    [Fact]
    public void ReleasingAPlayerVendorReturnsItsStockToTheOwnersBank()
    {
        var b = OwnedVendor();
        var old = VendorEngine.World;
        VendorEngine.World = b.World;
        try
        {
            var bank = b.World.CreateItem();
            bank.ItemType = ItemType.EqBankBox;
            b.Owner.Equip(bank, Layer.BankBox);
            var goods = b.World.CreateItem();
            goods.BaseId = 0x0F0E;
            Assert.True(VendorEngine.GetVendorBox(b.Vendor, Layer.VendorStock)!.TryAddItem(goods));

            VendorEngine.ReturnHoldingsToOwner(b.Vendor, b.Owner);

            Assert.Equal(bank.Uid, goods.ContainedIn);
        }
        finally { VendorEngine.World = old; }
    }

    // ================================================================ 2. brain auto

    [Theory]
    [InlineData(0x67, NpcBrainType.Dragon)]   // serpentine dragon
    [InlineData(0x31E, NpcBrainType.Dragon)]  // ancient wyrm
    [InlineData(0xA4, NpcBrainType.Berserk)]  // energy vortex
    [InlineData(0x23E, NpcBrainType.Berserk)] // blade spirit
    [InlineData(0x06, NpcBrainType.Animal)]   // bird
    [InlineData(0x97, NpcBrainType.Animal)]   // dolphin
    [InlineData(0xC8, NpcBrainType.Animal)]   // horse
    [InlineData(0x190, NpcBrainType.Human)]   // man
    [InlineData(0x2F0, NpcBrainType.Monster)] // iron golem
    [InlineData(0x01, NpcBrainType.Monster)]  // ogre
    [InlineData(0x3B, NpcBrainType.Monster)]  // dragon (not in the dragon list)
    public void TheDefaultBrainIsGuessedFromTheBody(int body, NpcBrainType expected)
    {
        Assert.Equal(expected, Character.GetNpcBrainAuto((ushort)body));
    }

    [Fact]
    public void ASpawnedCreatureWithoutNpcKeyGetsItsBodysBrain()
    {
        var world = CreateWorld();
        var npc = world.CreateCharacter();
        npc.BodyId = 0x190;
        Assert.Equal(NpcBrainType.Human, npc.GetNpcBrainAuto());
    }

    // ================================================================ 3. snoop

    [Fact]
    public void TheSnoopedMarkIsNeverAWitness()
    {
        // CheckCrimeSeen skips the mark (CCharFight.cpp:117), so NPC_OnNoticeSnoop -
        // which acts only when the witness IS the mark - never speaks in Source-X.
        var world = CreateWorld();
        var thief = world.CreateCharacter();
        thief.IsPlayer = true;
        world.PlaceCharacter(thief, new Point3D(100, 100, 0, 0));
        var mark = world.CreateCharacter();
        mark.NpcBrain = NpcBrainType.Human;
        world.PlaceCharacter(mark, new Point3D(101, 100, 0, 0));
        var bystander = world.CreateCharacter();
        bystander.NpcBrain = NpcBrainType.Human;
        world.PlaceCharacter(bystander, new Point3D(102, 100, 0, 0));

        var witnesses = new List<Character>();
        CrimeWitnessService.OnSeeSnoop = (w, _, _) => { witnesses.Add(w); return false; };
        CrimeWitnessService.CheckCrimeSeen(world, thief, mark, null, new Random(1), isSnoop: true);

        Assert.Contains(bystander, witnesses);
        Assert.DoesNotContain(mark, witnesses);
    }

    // ================================================================ 4. loot memory

    private static (NpcAI Ai, Character Npc, Item Corpse, Item Loot) LootBench(GameWorld world)
    {
        var ai = new NpcAI(world, new SphereConfig { NpcAi = (int)NpcAIFlags.Looting });
        var npc = world.CreateCharacter();
        npc.BodyId = 0x190; // human hands
        npc.NpcBrain = NpcBrainType.Monster;
        npc.Str = 100;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        npc.Equip(pack, Layer.Pack);

        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.BaseId = 0x2006;
        world.PlaceItem(corpse, new Point3D(101, 100, 0, 0));
        var loot = world.CreateItem();
        loot.BaseId = 0x0F0E;
        Assert.True(corpse.TryAddItem(loot));
        return (ai, npc, corpse, loot);
    }

    // The engine's own corpse-looting shortcut was removed (it was not Source-X);
    // looting now runs only through the look-around's item half and
    // NPC_Act_Looting (CCharNPCAct.cpp:1188-1215, :1593), so the bench drives that.
    private static bool TryLoot(NpcAI ai, Character npc)
    {
        npc.Int = 50; // NPC_LookAround looks at items only above 10 INT
        return (bool)typeof(NpcAI).GetMethod("LookAtNearbyItems", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, [npc])!;
    }

    [Fact]
    public void AnUntakeablePieceIsRememberedForAsLongAsItLasts()
    {
        var world = CreateWorld();
        var (ai, npc, _, loot) = LootBench(world);
        loot.SetAttr(ObjAttributes.Move_Never);
        long decayAt = Environment.TickCount64 + 60_000;
        loot.SetTimeout(decayAt);

        Assert.True(TryLoot(ai, npc));

        var mem = npc.Memory_FindObjTypes(loot.Uid, MemoryType.Speak);
        Assert.NotNull(mem);
        Assert.Equal(decayAt, mem!.Timeout);
        Assert.Equal(loot.Uid, mem.Link);
    }

    [Fact]
    public void ARememberedCorpseIsNotLootedAgain()
    {
        var world = CreateWorld();
        var (ai, npc, corpse, loot) = LootBench(world);
        ItemMoveRulesProbe(npc, loot);

        NpcAI.NpcLootMemory(npc, corpse);

        Assert.False(TryLoot(ai, npc));
        Assert.Equal(corpse.Uid, loot.ContainedIn);
    }

    [Fact]
    public void AnUnrememberedCorpseIsLooted()
    {
        var world = CreateWorld();
        var (ai, npc, corpse, loot) = LootBench(world);
        ItemMoveRulesProbe(npc, loot);

        Assert.True(TryLoot(ai, npc));
        Assert.NotEqual(corpse.Uid, loot.ContainedIn);
    }

    /// <summary>The bench NPC can lift the piece: the loot path is not blocked for a
    /// reason the test did not set up.</summary>
    private static void ItemMoveRulesProbe(Character npc, Item loot)
    {
        Assert.True(ItemMoveRules.CanMove(npc, loot, out _));
        Assert.True(npc.CanCarry(loot));
    }

    // ================================================================ 5. DEFMSG shouts

    [Fact]
    public void AGuardShoutsOneOfTheStrikeLines()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig { GuardsInstantKill = false });
        var said = new List<string>();
        ai.OnNpcSay = (_, text) => said.Add(text);

        // The strike line belongs to the guard's look at a criminal on guarded
        // ground (NPC_LookAtCharGuard, CCharNPCAct.cpp:751-754).
        var region = new Region { Name = "town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        region.AddRect(0, 0, 6000, 4000);
        world.AddRegion(region);
        var guard = world.CreateCharacter();
        guard.NpcBrain = NpcBrainType.Guard;
        world.PlaceCharacter(guard, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.SetStatFlag(StatFlag.Criminal);
        world.PlaceCharacter(target, new Point3D(105, 100, 0, 0));

        Assert.True(ai.GuardLookAtChar(guard, target, fromTrigger: false));

        string[] strike =
        [
            ServerMessages.Get(Msg.NpcGuardStrike1), ServerMessages.Get(Msg.NpcGuardStrike2),
            ServerMessages.Get(Msg.NpcGuardStrike3), ServerMessages.Get(Msg.NpcGuardStrike4),
            ServerMessages.Get(Msg.NpcGuardStrike5),
        ];
        Assert.Contains(Assert.Single(said), strike);
    }

    [Theory]
    [InlineData(true, Msg.NpcGenericSeecrim)]
    [InlineData(false, Msg.NpcGenericSeemons)]
    public void ATownsmanYellsTheSeeCrimeOrSeeMonsterLine(bool criminal, string key)
    {
        var world = CreateWorld();
        var region = new Region { Name = "town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        region.AddRect(0, 0, 6000, 4000);
        world.AddRegion(region);
        var ai = new NpcAI(world, new SphereConfig());
        var said = new List<string>();
        ai.OnNpcSay = (_, text) => said.Add(text);

        // The line is spoken only by an NPC that can speak (NPC_CanSpeak: it has a
        // SPEECH list) and only when a guard was actually called
        // (CCharNPCAct.cpp:819-826).
        ai.OnWitnessCrime = (_, _) => true;
        var townsman = world.CreateCharacter();
        townsman.NpcBrain = NpcBrainType.Human;
        townsman.DSpeech.Add(new ResourceId(ResType.Speech, 1));
        world.PlaceCharacter(townsman, new Point3D(100, 100, 0, 0));
        var villain = world.CreateCharacter();
        villain.IsPlayer = true;
        world.PlaceCharacter(villain, new Point3D(102, 100, 0, 0));
        if (criminal) villain.SetStatFlag(StatFlag.Criminal);
        else villain.Kills = 1000; // a murderer

        var witness = typeof(NpcAI).GetMethod("LookAroundTown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < 300 && said.Count == 0; i++)
            witness.Invoke(ai, [townsman, false]); // a one-in-three look

        Assert.Equal(ServerMessages.Get(key), Assert.Single(said));
    }

    // ================================================================ 6. guard call words

    [Theory]
    [InlineData("guards", true)]
    [InlineData("GUARDS", true)]
    [InlineData("guard", true)]
    [InlineData("help guards", true)]
    [InlineData("guards help me", true)]
    [InlineData("guardsman", false)]
    [InlineData("bodyguards", false)]
    [InlineData("help", false)]
    [InlineData("guards!", false)] // FindStrWord ends a word only at whitespace or the end
    [InlineData("", false)]
    public void TheGuardCallIsAWholeWord(string text, bool expected)
    {
        Assert.Equal(expected, SpeechWords.IsGuardCall(text));
    }

    [Fact]
    public void FindStrWordTriesEveryKeywordInTheList()
    {
        Assert.True(SpeechWords.FindStrWord("i want to buy", "SELL,BUY") > 0);
        Assert.Equal(0, SpeechWords.FindStrWord("i want to buyur", "SELL,BUY"));
    }
}
