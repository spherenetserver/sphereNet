using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Double-click item uses (CClientUse.cpp Cmd_Use_Item, CClientTarg.cpp
/// OnTarg_Use_Item, CCharUse.cpp Do_Use_Item) and spoken pet commands
/// (CCharNPCPet.cpp NPC_OnHearPetCmd / NPC_OnHearPetCmdTarg / NPC_OnPetCommand)
/// against the reference.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemUseAndPetCommandParityTests
{
    private const string Defs = """
        [ITEMDEF 0f95]
        DEFNAME=i_test_cloth_bolt
        RESOURCES=50 i_test_cloth

        [ITEMDEF 01766]
        DEFNAME=i_test_cloth
        TYPE=t_cloth
        CAN=0x0100

        [ITEMDEF 0bd2]
        DEFNAME=i_test_sign
        TDATA2=064
        """;

    private sealed class Bench
    {
        public required GameWorld World;
        public required GameClient Client;
        public required Character Player;
        public required Item Pack;
        public List<string> Sys = [];
        /// <summary>Everything broadcast to the neighbourhood (speech, sounds, effects).</summary>
        public List<PacketWriter> Nearby = [];
        /// <summary>Packets sent to some other character's client.</summary>
        public List<(Serial To, PacketWriter Packet)> ToOthers = [];
    }

    private static void LoadDefs()
    {
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spherenet_itemuse_{Guid.NewGuid():N}.scp");
        System.IO.File.WriteAllText(file, Defs);
        var resources = new ResourceHolder(NullLoggerFactory.Instance.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = System.IO.Path.GetDirectoryName(file) ?? ""
        };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    private static Bench Setup(PrivLevel priv = PrivLevel.Player)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), Random.Shared.Next(20_000, 30_000));
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Tester";
        player.PrivLevel = priv;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);

        var b = new Bench { World = world, Client = client, Player = player, Pack = pack };
        client.ServerDrivenSysMessage = b.Sys.Add;
        client.BroadcastNearby = (_, _, packet, _) => b.Nearby.Add(packet);
        client.SendToChar = (to, packet) => b.ToOthers.Add((to, packet));
        TestHarness.ClearQueuedPackets(client.NetState);
        return b;
    }

    private static Item InPack(Bench b, ItemType type, ushort baseId = 0)
    {
        var item = b.World.CreateItem();
        if (baseId != 0) item.BaseId = baseId;
        item.ItemType = type;
        b.Pack.AddItem(item);
        return item;
    }

    private static Character OtherPlayer(Bench b, string name, int x = 102)
    {
        var other = b.World.CreateCharacter();
        other.IsPlayer = true;
        other.IsOnline = true;
        other.Name = name;
        b.World.PlaceCharacter(other, new Point3D((short)x, 100, 0, 0));
        return other;
    }

    private static Character Pet(Bench b, string name, bool speaks = false, bool owned = true)
    {
        var pet = b.World.CreateCharacter();
        pet.Name = name;
        pet.BodyId = 0x00C8;
        pet.NpcBrain = NpcBrainType.Animal;
        b.World.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        if (owned)
            Assert.True(pet.TryAssignOwnership(b.Player, b.Player, summoned: false, enforceFollowerCap: false));
        if (speaks)
            pet.DSpeech.Add(new ResourceId(ResType.Speech, 1));
        return pet;
    }

    /// <summary>Whether a packet carries this text as big-endian UTF-16 (unicode speech).</summary>
    private static bool Says(PacketWriter packet, string text)
    {
        var bytes = packet.Build().Span.ToArray();
        var needle = Encoding.BigEndianUnicode.GetBytes(text);
        for (int i = 0; i + needle.Length <= bytes.Length; i++)
            if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        return false;
    }

    private static void Target(Bench b, Serial uid) =>
        b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, uid.Value, 0, 0, 0, 0);

    // ------------------------------------------------------------ A1 cloth bolt

    [Fact]
    public void ScissorsCutAClothBoltIntoItsResources()
    {
        LoadDefs();
        var b = Setup();
        var scissors = InPack(b, ItemType.Scissors);
        var bolt = InPack(b, ItemType.ClothBolt, 0x0F95);
        bolt.Amount = 2;
        bolt.Hue = new Color(0x21);

        b.Client.HandleDoubleClick(scissors.Uid.Value);
        Target(b, bolt.Uid);

        Assert.True(bolt.IsDeleted);
        var cloth = Assert.Single(b.Pack.Contents, i => i.BaseId == 0x1766);
        Assert.Equal(100, cloth.Amount);                 // 2 bolts x 50
        Assert.Equal(0x21, cloth.Hue.Value);             // IT_CLOTH takes the bolt's hue
        Assert.Contains(b.Nearby, p => p is PacketSound);
    }

    [Fact]
    public void ABoltWithNoResourcesIsLeftAlone()
    {
        var b = Setup();
        TestHarness.SeedItemDefs(0x0F96);
        var bolt = InPack(b, ItemType.ClothBolt, 0x0F96);

        ClientItemUseHandler.ConvertBoltToCloth(b.World, bolt);

        Assert.False(bolt.IsDeleted);
    }

    // ------------------------------------------------------- A2 explosion potion

    private static Item ExplosionPotion(Bench b)
    {
        var potion = InPack(b, ItemType.Potion, 0x0F0D);
        potion.More1 = (uint)SpellType.Explosion;
        return potion;
    }

    [Fact]
    public void AnExplosionPotionIsLitAndHeldNotDrunk()
    {
        var b = Setup();
        var potion = ExplosionPotion(b);

        b.Client.HandleDoubleClick(potion.Uid.Value);

        Assert.False(potion.IsDeleted);
        Assert.True(b.Player.TryGetTag("DRAGGING", out string? held));
        Assert.Equal(potion.Uid.Value.ToString(), held);
        Assert.Equal(4, potion.MoreP.X);                 // m_tick default
        Assert.True(potion.IsIgnitedExplosionPotion);
        Assert.Equal(b.Player.Uid, potion.Link);
        Assert.True(potion.Timeout > 0);
        Assert.True(b.Client.HasPendingTarget);
        Assert.Contains(ServerMessages.Get(Msg.SelectPotionTarget), b.Sys);
    }

    [Fact]
    public void AThrownExplosionPotionLandsWhereItWasAimed()
    {
        var b = Setup();
        var potion = ExplosionPotion(b);
        b.Client.HandleDoubleClick(potion.Uid.Value);
        b.Nearby.Clear();

        b.Client.HandleTargetResponse(1, b.Client.ActiveTargetCursorId, 0, 104, 100, 0, 0);

        Assert.False(b.Player.TryGetTag("DRAGGING", out _));
        Assert.False(potion.ContainedIn.IsValid);
        Assert.Equal(new Point3D(104, 100, 0, 0), potion.Position);
        Assert.True(potion.IsAttr(ObjAttributes.Move_Never));
        Assert.Contains(b.Nearby, p => p is PacketEffectHued);
    }

    [Fact]
    public void ALitPotionCountsDownOncePerTick()
    {
        var b = Setup();
        var potion = ExplosionPotion(b);
        b.Client.HandleDoubleClick(potion.Uid.Value);
        var said = new List<PacketWriter>();
        SphereNet.Game.Objects.ObjBase.BroadcastNearby = (_, _, packet, _) => said.Add(packet);

        potion.SetTimeout(1);       // due
        potion.OnTick();

        Assert.Equal(3, potion.MoreP.X);
        Assert.Contains(said, p => Says(p, "3"));
        Assert.True(potion.Timeout > Environment.TickCount64);
    }

    [Fact]
    public void AnUnlitPotionDoesNotCountDown()
    {
        var b = Setup();
        var potion = ExplosionPotion(b);
        potion.MoreP = new Point3D(4, 0, 0, 0);

        potion.SetTimeout(1);
        potion.OnTick();

        Assert.Equal(4, potion.MoreP.X);
    }

    [Fact]
    public void APotionThatMayNotBeMovedIsNotUsed()
    {
        var b = Setup();
        var potion = ExplosionPotion(b);
        potion.SetAttr(ObjAttributes.Move_Never);

        b.Client.HandleDoubleClick(potion.Uid.Value);

        Assert.Contains(ServerMessages.Get(Msg.ItemusePotionFail), b.Sys);
        Assert.False(potion.IsIgnitedExplosionPotion);
    }

    // ------------------------------------------------------------------ A3 rune

    [Fact]
    public void ARuneAsksForItsNameAndTakesItBehindThePrefix()
    {
        var b = Setup();
        var rune = InPack(b, ItemType.Rune, 0x1F14);

        b.Client.HandleDoubleClick(rune.Uid.Value);
        Assert.Contains(ServerMessages.Get(Msg.ItemuseRuneName), b.Sys);

        b.Client.HandlePromptResponse(b.Player.Uid.Value, rune.Uid.Value, 1, "Home");

        Assert.Equal(ServerMessages.Get(Msg.RuneTo) + "Home", rune.Name);
    }

    // ------------------------------------------------------------ A4 sewing kit

    [Fact]
    public void ASewingKitAsksWhatToSewAndRefusesWhatIsNoMaterial()
    {
        var b = Setup();
        var kit = InPack(b, ItemType.SewingKit);
        kit.Name = "sewing kit";
        var rock = InPack(b, ItemType.Normal);

        b.Client.HandleDoubleClick(kit.Uid.Value);
        Assert.True(b.Client.HasPendingTarget);
        Assert.Contains(ServerMessages.GetFormatted(Msg.ItemuseSewkitPromt, kit.GetName()), b.Sys);

        Target(b, rock.Uid);
        Assert.Contains(ServerMessages.Get(Msg.ItemuseSkitUnable), b.Sys);
    }

    [Theory]
    [InlineData(ItemType.Leather)]
    [InlineData(ItemType.Hide)]
    [InlineData(ItemType.Cloth)]
    public void ASewingKitAcceptsLeatherHidesAndCloth(ItemType material)
    {
        var b = Setup();
        var kit = InPack(b, ItemType.SewingKit);
        var stuff = InPack(b, material);

        b.Client.HandleDoubleClick(kit.Uid.Value);
        Target(b, stuff.Uid);

        Assert.DoesNotContain(ServerMessages.Get(Msg.ItemuseSkitUnable), b.Sys);
        Assert.DoesNotContain(ServerMessages.Get(Msg.ItemuseUnable), b.Sys);
    }

    // --------------------------------------------------------------- A6 tracker

    [Fact]
    public void AnUnattunedTrackerAsksForALinkAndKeepsItAsItsLink()
    {
        var b = Setup();
        var tracker = InPack(b, ItemType.Tracker);
        var quarry = OtherPlayer(b, "Quarry");

        b.Client.HandleDoubleClick(tracker.Uid.Value);
        Assert.True(b.Client.HasPendingTarget);
        Assert.Contains(ServerMessages.Get(Msg.ItemuseTrackerAttune), b.Sys);
        Assert.DoesNotContain(ServerMessages.Get(Msg.TrackingUnable), b.Sys);

        Target(b, quarry.Uid);
        Assert.Equal(quarry.Uid, tracker.Link);
        Assert.False(tracker.TryGetTag("LINK", out _));
    }

    [Fact]
    public void AnAttunedTrackerPointsAtItsQuarryWithoutACursor()
    {
        var b = Setup();
        var tracker = InPack(b, ItemType.Tracker);
        var quarry = OtherPlayer(b, "Quarry");
        tracker.Link = quarry.Uid;

        b.Client.HandleDoubleClick(tracker.Uid.Value);

        Assert.False(b.Client.HasPendingTarget);
        Assert.DoesNotContain(ServerMessages.Get(Msg.ItemuseTrackerAttune), b.Sys);
    }

    [Fact]
    public void ATrackerWhoseQuarryIsGoneSaysSoAndAsksAgain()
    {
        var b = Setup();
        var tracker = InPack(b, ItemType.Tracker);
        tracker.Link = new Serial(0x00123456);

        b.Client.HandleDoubleClick(tracker.Uid.Value);

        Assert.Contains(ServerMessages.Get(Msg.TrackingUnable), b.Sys);
        Assert.True(b.Client.HasPendingTarget);
    }

    // --------------------------------------------------------------- A7 keyring

    private static Item Key(Bench b, Item ring, uint lockCode)
    {
        var key = b.World.CreateItem();
        key.ItemType = ItemType.Key;
        key.BaseId = 0x100E;
        key.More1 = lockCode;
        ring.AddItem(key);
        return key;
    }

    [Fact]
    public void AKeyRingShowsHowManyKeysHangOnIt()
    {
        var b = Setup();
        var ring = InPack(b, ItemType.Keyring, 0x1011);

        var k1 = Key(b, ring, 1);
        Assert.Equal(0x1769, ring.BaseId);
        Key(b, ring, 2);
        Assert.Equal(0x1769, ring.BaseId);
        Key(b, ring, 3);
        Assert.Equal(0x176A, ring.BaseId);
        Key(b, ring, 4);
        Key(b, ring, 5);
        Assert.Equal(0x176B, ring.BaseId);

        foreach (var key in ring.Contents.ToArray())
            ring.RemoveItem(key);
        Assert.Equal(0x1011, ring.BaseId);
        Assert.Equal(ItemType.Keyring, ring.ItemType);
        Assert.False(k1.IsDeleted);
    }

    [Fact]
    public void AKeyRingUsedOnItselfGivesItsKeysBack()
    {
        var b = Setup();
        var ring = InPack(b, ItemType.Keyring, 0x1011);
        var key = Key(b, ring, 77);

        b.Client.HandleDoubleClick(ring.Uid.Value);
        Target(b, ring.Uid);

        Assert.Empty(ring.Contents);
        Assert.Equal(b.Pack.Uid, key.ContainedIn);
        Assert.DoesNotContain(ServerMessages.Get(Msg.MsgKeyIsblank), b.Sys);
    }

    [Fact]
    public void AKeyRingOpensALockWithTheKeyThatFits()
    {
        var b = Setup();
        var ring = InPack(b, ItemType.Keyring, 0x1011);
        Key(b, ring, 11);
        Key(b, ring, 77);
        var chest = InPack(b, ItemType.ContainerLocked);
        chest.More1 = 77;

        b.Client.HandleDoubleClick(ring.Uid.Value);
        Target(b, chest.Uid);

        Assert.Equal(ItemType.Container, chest.ItemType);
    }

    [Fact]
    public void AKeyRingWithoutTheKeyOrOnNoLockSaysWhich()
    {
        var b = Setup();
        var ring = InPack(b, ItemType.Keyring, 0x1011);
        Key(b, ring, 11);
        var chest = InPack(b, ItemType.ContainerLocked);
        chest.More1 = 77;
        var rock = InPack(b, ItemType.Normal);

        b.Client.HandleDoubleClick(ring.Uid.Value);
        Target(b, chest.Uid);
        Assert.Contains(ServerMessages.Get(Msg.ItemuseKeyNokey), b.Sys);
        Assert.Equal(ItemType.ContainerLocked, chest.ItemType);

        b.Client.HandleDoubleClick(ring.Uid.Value);
        Target(b, rock.Uid);
        Assert.Contains(ServerMessages.Get(Msg.ItemuseKeyNolock), b.Sys);
    }

    // ------------------------------------------------------------------ A8 sign

    [Fact]
    public void TheSignGumpPacketIsLaidOutAsTheReferenceWritesIt()
    {
        var bytes = new PacketSignGump(0x40000001, 0x64, "ab", null).Build().Span.ToArray();

        Assert.Equal(
            new byte[]
            {
                0x8B, 0x00, 0x10,               // id, total length
                0x40, 0x00, 0x00, 0x01,         // object
                0x00, 0x64,                     // gump
                0x00, 0x03, (byte)'a', (byte)'b', 0x00,   // length counts the NUL
                0x00, 0x00,                     // the absent text: a zero length
            },
            bytes);
    }

    [Fact]
    public void APlainSignShowsItsGumpAndIsNotAHouse()
    {
        LoadDefs();
        var b = Setup();
        var sign = b.World.CreateItem();
        sign.BaseId = 0x0BD2;
        sign.ItemType = ItemType.SignGump;
        sign.Name = "The Rusty Anchor";
        b.World.PlaceItem(sign, new Point3D(100, 101, 0, 0));

        b.Client.HandleDoubleClick(sign.Uid.Value);

        Assert.Contains(TestHarness.GetQueuedPackets(b.Client.NetState), p => p.Span[0] == 0x8B);
        Assert.DoesNotContain(ServerMessages.Get("house_not_house"), b.Sys);
    }

    // --------------------------------------------------------------- A9 dye vat

    [Fact]
    public void ADyeVatUsedOnOneselfDyesHairAndBeard()
    {
        var b = Setup();
        var vat = InPack(b, ItemType.DyeVat);
        vat.Hue = new Color(0x55);
        var hair = b.World.CreateItem();
        b.Player.Equip(hair, Layer.Hair);
        var beard = b.World.CreateItem();
        b.Player.Equip(beard, Layer.FacialHair);

        b.Client.HandleDoubleClick(vat.Uid.Value);
        Target(b, b.Player.Uid);

        Assert.Equal(0x55, hair.Hue.Value);
        Assert.Equal(0x55, beard.Hue.Value);
    }

    [Fact]
    public void ADyeVatSetsItsHueEvenWhenPlainAndSaysNothing()
    {
        var b = Setup();
        var vat = InPack(b, ItemType.DyeVat);         // hue 0
        var shirt = InPack(b, ItemType.Clothing);
        shirt.Hue = new Color(0x21);

        b.Client.HandleDoubleClick(vat.Uid.Value);
        b.Sys.Clear();
        Target(b, shirt.Uid);

        Assert.Equal(0, shirt.Hue.Value);
        Assert.Empty(b.Sys);
    }

    [Fact]
    public void ADyeVatDoesNotReachAnotherCharacter()
    {
        var b = Setup();
        var vat = InPack(b, ItemType.DyeVat);
        vat.Hue = new Color(0x55);
        var other = OtherPlayer(b, "Other", 101);
        var hair = b.World.CreateItem();
        other.Equip(hair, Layer.Hair);

        b.Client.HandleDoubleClick(vat.Uid.Value);
        Target(b, other.Uid);

        Assert.Equal(0, hair.Hue.Value);
        Assert.Contains(ServerMessages.Get(Msg.ItemuseDyeReach), b.Sys);
    }

    // ------------------------------------------------ A10 sextant / locked door

    [Fact]
    public void ASextantReadingIsSpokenOverTheUser()
    {
        var b = Setup();
        var sextant = InPack(b, ItemType.Sextant);

        b.Client.HandleDoubleClick(sextant.Uid.Value);

        string reading = ServerMessages.GetFormatted(Msg.ItemuseSextant, "",
            ClientItemUseHandler.FormatSextant(b.Player.Position));
        Assert.DoesNotContain(b.Sys, s => s.Contains('°'));
        Assert.Contains(TestHarness.GetQueuedPackets(b.Client.NetState),
            p => ContainsUnicode(p.Span.ToArray(), reading[..8]));
    }

    private static bool ContainsUnicode(byte[] bytes, string text)
    {
        var needle = Encoding.BigEndianUnicode.GetBytes(text);
        for (int i = 0; i + needle.Length <= bytes.Length; i++)
            if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        return false;
    }

    [Fact]
    public void ALockedDoorSaysItIsLocked()
    {
        var b = Setup();
        var door = b.World.CreateItem();
        door.BaseId = 0x0675;
        door.ItemType = ItemType.DoorLocked;
        b.World.PlaceItem(door, new Point3D(100, 101, 0, 0));

        b.Client.HandleDoubleClick(door.Uid.Value);

        Assert.Contains(ServerMessages.Get(Msg.MsgKeyDoorlocked), b.Sys);
        Assert.Equal(ItemType.DoorLocked, door.ItemType);
    }

    [Fact]
    public void AMagicallyLockedDoorShowsItsFace()
    {
        var b = Setup();
        var door = b.World.CreateItem();
        door.BaseId = 0x0675;
        door.ItemType = ItemType.DoorLocked;
        door.SetAttr(ObjAttributes.Magic);
        b.World.PlaceItem(door, new Point3D(100, 101, 0, 0));

        b.Client.HandleDoubleClick(door.Uid.Value);

        Assert.Contains(b.World.GetItemsInRange(door.Position, 0),
            i => i.BaseId is 0x1ED9 or 0x1EEC && i.DecayTime > 0);
    }

    // ------------------------------------------------------- B1 acknowledgement

    [Fact]
    public void AnAnimalAcknowledgesWithoutWords()
    {
        var b = Setup();
        Pet(b, "rex");

        Assert.True(b.Client.TryHandlePetCommand("rex stay"));

        Assert.DoesNotContain(b.Nearby, p => Says(p, ServerMessages.Get(Msg.NpcPetSuccess)));
    }

    [Fact]
    public void APetWithSpeechAcknowledgesInWords()
    {
        var b = Setup();
        Pet(b, "rex", speaks: true);

        Assert.True(b.Client.TryHandlePetCommand("rex stay"));

        Assert.Contains(b.Nearby, p => Says(p, ServerMessages.Get(Msg.NpcPetSuccess)));
    }

    [Fact]
    public void APetThatCannotSeeItsMasterDoesNotAcknowledge()
    {
        var b = Setup();
        Pet(b, "rex", speaks: true);
        b.Player.SetStatFlag(StatFlag.Invisible);

        Assert.True(b.Client.TryHandlePetCommand("rex stay"));

        Assert.DoesNotContain(b.Nearby, p => Says(p, ServerMessages.Get(Msg.NpcPetSuccess)));
    }

    // ------------------------------------------------------ B2/B3/B9 vendor verbs

    [Theory]
    [InlineData("cash")]
    [InlineData("bought")]
    [InlineData("samples")]
    [InlineData("stock")]
    [InlineData("price")]
    public void AVendorVerbMeansNothingToAnotherPet(string verb)
    {
        var b = Setup();
        Pet(b, "rex", speaks: true);

        b.Client.TryHandlePetCommand("rex " + verb);

        Assert.Empty(b.Nearby);
        Assert.False(b.Client.HasPendingTarget);
    }

    [Fact]
    public void StatusFromAPetThatIsNoHirelingSaysNothing()
    {
        var b = Setup();
        var pet = Pet(b, "rex", speaks: true);
        pet.SetTag("HIRE_DAYS_LEFT", "9");

        b.Client.TryHandlePetCommand("rex status");

        Assert.Empty(b.Nearby);
    }

    [Fact]
    public void DropWithAnEmptyPackIsAcknowledged()
    {
        var b = Setup();
        var pet = Pet(b, "rex", speaks: true);
        var petPack = b.World.CreateItem();
        petPack.ItemType = ItemType.Container;
        pet.Equip(petPack, Layer.Pack);

        b.Client.TryHandlePetCommand("rex drop");

        Assert.Contains(b.Nearby, p => Says(p, ServerMessages.Get(Msg.NpcPetSuccess)));
        Assert.DoesNotContain(b.Nearby, p => Says(p, ServerMessages.Get(Msg.NpcPetCarrynothing)));
    }

    [Fact]
    public void EquipIsAlwaysAcknowledged()
    {
        var b = Setup();
        Pet(b, "rex", speaks: true);

        b.Client.TryHandlePetCommand("rex equip");

        Assert.Contains(b.Nearby, p => Says(p, ServerMessages.Get(Msg.NpcPetSuccess)));
    }

    // -------------------------------------------------------- B4 friend/unfriend

    [Fact]
    public void FriendNamesBothPartiesAndThePetFollowsItsNewFriend()
    {
        var b = Setup();
        var pet = Pet(b, "rex");
        var friend = OtherPlayer(b, "Ally");

        b.Client.TryHandlePetCommand("rex friend");
        Target(b, friend.Uid);

        Assert.True(pet.IsFriendOf(friend.Uid));
        Assert.Contains(ServerMessages.GetFormatted(Msg.NpcPetTargFriendSuccess1, "rex", "Ally"), b.Sys);
        Assert.Contains(b.ToOthers, m => m.To == friend.Uid &&
            Says(m.Packet, ServerMessages.GetFormatted(Msg.NpcPetTargFriendSuccess2, "Tester", "rex")));
        Assert.Equal(PetAIMode.Follow, pet.PetAIMode);
        Assert.True(pet.TryGetTag("FOLLOW_TARGET", out string? followed));
        Assert.Equal(friend.Uid.Value.ToString(), followed);
    }

    [Fact]
    public void FriendingOneselfOrANonPlayerConfusesThePet()
    {
        var b = Setup();
        var pet = Pet(b, "rex");
        var npc = Pet(b, "bob", owned: false);

        b.Client.TryHandlePetCommand("rex friend");
        Target(b, b.Player.Uid);
        Assert.False(pet.IsFriendOf(b.Player.Uid));
        Assert.Contains(b.Nearby, p => Says(p, ServerMessages.Get(Msg.NpcPetConfused)));

        b.Nearby.Clear();
        b.Client.TryHandlePetCommand("rex friend");
        Target(b, npc.Uid);
        Assert.False(pet.IsFriendOf(npc.Uid));
        Assert.Contains(b.Nearby, p => Says(p, ServerMessages.Get(Msg.NpcPetConfused)));
    }

    [Fact]
    public void UnfriendTellsBothAndThePetFollowsTheSpeaker()
    {
        var b = Setup();
        var pet = Pet(b, "rex");
        var friend = OtherPlayer(b, "Ally");
        pet.AddFriend(friend);

        b.Client.TryHandlePetCommand("rex unfriend");
        Target(b, friend.Uid);

        Assert.False(pet.IsFriendOf(friend.Uid));
        Assert.Contains(ServerMessages.GetFormatted(Msg.NpcPetTargUnfriendSuccess1, "rex", "Ally"), b.Sys);
        Assert.Contains(b.ToOthers, m => m.To == friend.Uid &&
            Says(m.Packet, ServerMessages.GetFormatted(Msg.NpcPetTargUnfriendSuccess2, "Tester", "rex")));
        Assert.True(pet.TryGetTag("FOLLOW_TARGET", out string? followed));
        Assert.Equal(b.Player.Uid.Value.ToString(), followed);
    }

    // ------------------------------------------------------ B7 conjured creatures

    [Theory]
    [InlineData("friend", Msg.NpcPetTargFriendSummoned)]
    [InlineData("transfer", Msg.NpcPetTargTransferSummoned)]
    public void AConjuredCreatureRefusesAtTheCommandAndOpensNoCursor(string verb, string message)
    {
        var b = Setup();
        var pet = Pet(b, "rex", speaks: true);
        pet.SetStatFlag(StatFlag.Conjured);

        b.Client.TryHandlePetCommand("rex " + verb);

        Assert.Contains(ServerMessages.Get(message), b.Sys);
        Assert.False(b.Client.HasPendingTarget);
        Assert.Empty(b.Nearby);
    }

    [Fact]
    public void AConjuredCreatureStillTakesUnfriend()
    {
        var b = Setup();
        var pet = Pet(b, "rex");
        pet.SetStatFlag(StatFlag.Conjured);

        b.Client.TryHandlePetCommand("rex unfriend");

        Assert.True(b.Client.HasPendingTarget);
    }

    // ------------------------------------------------------------- B5 transfer

    [Fact]
    public void TransferNeedsSomeoneWhoIsPlaying()
    {
        var b = Setup();
        var pet = Pet(b, "rex");
        var gone = OtherPlayer(b, "Gone");
        gone.IsOnline = false;

        b.Client.TryHandlePetCommand("rex transfer");
        Target(b, gone.Uid);

        Assert.True(pet.HasOwner(b.Player.Uid));
        Assert.False(pet.HasOwner(gone.Uid));
    }

    [Fact]
    public void ATransferIsAnsweredByThePetAlone()
    {
        var b = Setup();
        var pet = Pet(b, "rex", speaks: true);
        var taker = OtherPlayer(b, "Taker");

        b.Client.TryHandlePetCommand("rex transfer");
        b.Sys.Clear();
        Target(b, taker.Uid);

        Assert.True(pet.HasOwner(taker.Uid));
        Assert.Empty(b.Sys);
    }

    // ---------------------------------------------------------------- B6 guard

    [Fact]
    public void TheGuardedCharacterIsToldNotTheCommander()
    {
        var b = Setup();
        var pet = Pet(b, "rex");
        var ward = OtherPlayer(b, "Ward");

        b.Client.TryHandlePetCommand("rex guard");
        b.Sys.Clear();
        Target(b, ward.Uid);

        string told = ServerMessages.GetFormatted(Msg.NpcPetTargGuardSuccess, "rex");
        Assert.DoesNotContain(told, b.Sys);
        Assert.Contains(b.ToOthers, m => m.To == ward.Uid && Says(m.Packet, told));
        Assert.Equal(PetAIMode.Guard, pet.PetAIMode);
    }

    // --------------------------------------------------------------- B8 attack

    [Fact]
    public void AttackTurnsAwayOnlyNobodyTheSpeakerAndThePet()
    {
        var b = Setup();
        var pet = Pet(b, "rex");

        b.Client.TryHandlePetCommand("rex attack");
        Target(b, b.Player.Uid);
        Assert.NotEqual(PetAIMode.Attack, pet.PetAIMode);

        b.Client.TryHandlePetCommand("rex attack");
        Target(b, pet.Uid);
        Assert.NotEqual(PetAIMode.Attack, pet.PetAIMode);
    }

    [Fact]
    public void AttackOnThePetsOwnMasterIsNotPreRejected()
    {
        // Staff ordering somebody's pet at its owner: the reference turns away only
        // no target, the speaker and the pet itself (CCharNPCPet.cpp:445).
        var b = Setup(PrivLevel.GM);
        var owner = OtherPlayer(b, "Owner");
        var pet = Pet(b, "rex", owned: false);
        Assert.True(pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: false));

        b.Client.TryHandlePetCommand("rex attack");
        Target(b, owner.Uid);

        Assert.Equal(PetAIMode.Attack, pet.PetAIMode);
        Assert.Equal(owner.Uid, pet.FightTarget);
    }

    [Fact]
    public void AnInvulnerableTargetIsRefusedAfterTheOwnerIsRecorded()
    {
        var b = Setup();
        var pet = Pet(b, "rex");
        var victim = Pet(b, "prey", owned: false);
        victim.SetStatFlag(StatFlag.Invul);

        b.Client.TryHandlePetCommand("rex attack");
        Target(b, victim.Uid);

        Assert.NotEqual(PetAIMode.Attack, pet.PetAIMode);
        // OnAttackedBy(owner, fCommandPet) ran first (CCharNPCPet.cpp:447).
        Assert.NotNull(victim.Memory_FindObjTypes(b.Player.Uid, MemoryType.HarmedBy));
    }
}
