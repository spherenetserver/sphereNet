using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class SourceXObjBaseVerbWave218Tests
{
    private sealed class Console : ITextConsole
    {
        public string GetName() => "test";
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public void SysMessage(string text) { }
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void MoveTo_UsesWorldPlacementForCharactersAndItems()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        var item = world.CreateItem();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        world.PlaceItem(item, new Point3D(101, 100, 0, 0));

        Assert.True(ch.TryExecuteCommand("MOVETO", "700,800,5,0", new Console()));
        Assert.True(item.TryExecuteCommand("MOVETO", "702,800,6,0", new Console()));

        Assert.Equal(new Point3D(700, 800, 5, 0), ch.Position);
        Assert.Equal(new Point3D(702, 800, 6, 0), item.Position);
        Assert.Contains(ch, world.GetCharsInRange(new Point3D(700, 800, 0, 0), 2));
        Assert.Contains(item, world.GetItemsInRange(new Point3D(700, 800, 0, 0), 2));
        Assert.DoesNotContain(ch, world.GetCharsInRange(new Point3D(100, 100, 0, 0), 2));
    }

    [Fact]
    public void MoveNear_ResolvesSourceXUidAndHonorsDistance()
    {
        var world = CreateWorld();
        var mover = world.CreateCharacter();
        var beacon = world.CreateItem();
        world.PlaceCharacter(mover, new Point3D(100, 100, 0, 0));
        world.PlaceItem(beacon, new Point3D(900, 750, 4, 0));

        string uid = "0" + beacon.Uid.Value.ToString("X");
        Assert.True(mover.TryExecuteCommand("MOVENEAR", $"{uid},3,1", new Console()));

        Assert.Equal(new Point3D(903, 750, 4, 0), mover.Position);
    }

    [Fact]
    public void Click_RoutesThroughHostSingleClickBridge()
    {
        var world = CreateWorld();
        var invoker = world.CreateCharacter();
        var target = world.CreateItem();
        world.PlaceCharacter(invoker, new Point3D(100, 100, 0, 0));
        world.PlaceItem(target, new Point3D(101, 100, 0, 0));
        ObjBase? clicked = null;
        ObjBase.OnScriptSingleClick = (obj, _) => { clicked = obj; return true; };
        try
        {
            Assert.True(invoker.TryExecuteCommand(
                "CLICK", "0" + target.Uid.Value.ToString("X"), new Console()));
            Assert.Same(target, clicked);
        }
        finally
        {
            ObjBase.OnScriptSingleClick = null;
        }
    }

    [Fact]
    public void UseItem_DelegatesToExistingItemUsePath()
    {
        var world = CreateWorld();
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        Item? used = null;
        Item.OnScriptDClick = (candidate, _) => used = candidate;
        try
        {
            Assert.True(item.TryExecuteCommand("USEITEM", "", new Console()));
            Assert.Same(item, used);
        }
        finally
        {
            Item.OnScriptDClick = null;
        }
    }

    [Fact]
    public void Drop_DetachesEquippedItemAtOwnersFeet()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(300, 400, 7, 0));
        var item = world.CreateItem();
        Assert.True(owner.Equip(item, Layer.OneHanded));

        Assert.True(item.TryExecuteCommand("DROP", "", new Console()));

        Assert.False(item.IsEquipped);
        Assert.False(item.ContainedIn.IsValid);
        Assert.Null(owner.GetEquippedItem(Layer.OneHanded));
        Assert.Equal(owner.Position, item.Position);
    }

    [Fact]
    public void Unequip_BouncesItemToInvokingClientsBackpack()
    {
        var world = CreateWorld();
        var source = world.CreateCharacter();
        var wearer = world.CreateCharacter();
        world.PlaceCharacter(source, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(wearer, new Point3D(200, 200, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        Assert.True(source.Equip(pack, Layer.Pack));
        var item = world.CreateItem();
        Assert.True(wearer.Equip(item, Layer.OneHanded));

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(
            loggerFactory, world, new AccountManager(loggerFactory), id: 218);
        TestHarness.AttachCharacter(client, source);

        Assert.True(item.TryExecuteCommand("UNEQUIP", "", client));

        Assert.Null(wearer.GetEquippedItem(Layer.OneHanded));
        Assert.False(item.IsEquipped);
        Assert.Contains(item, pack.Contents);
    }

    [Fact]
    public void UseDoor_BypassesLockedTypeAndTogglesBothWays()
    {
        var world = CreateWorld();
        var door = world.CreateItem();
        door.ItemType = ItemType.DoorLocked;
        door.BaseId = 0x0675;
        world.PlaceItem(door, new Point3D(500, 500, 0, 0));

        Assert.True(door.TryExecuteCommand("USEDOOR", "", new Console()));
        Assert.Equal((ushort)0x0676, door.BaseId);
        Assert.Equal(new Point3D(499, 501, 0, 0), door.Position);

        Assert.True(door.TryExecuteCommand("USEDOOR", "", new Console()));
        Assert.Equal((ushort)0x0675, door.BaseId);
        Assert.Equal(new Point3D(500, 500, 0, 0), door.Position);
    }

    [Fact]
    public void CharacterDupe_CopiesCoreStateAndSkills()
    {
        var world = CreateWorld();
        var original = world.CreateCharacter();
        original.Name = "a wolf";
        original.BodyId = 0x00E1;
        original.Hue = new Color(0x0455);
        original.Str = 77;
        original.SetSkill(SkillType.Wrestling, 654);
        original.SetTag("PACK", "timber");
        world.PlaceCharacter(original, new Point3D(600, 700, 2, 0));

        Assert.True(original.TryExecuteCommand("DUPE", "", new Console()));

        var clone = world.FindChar(world.LastNewChar);
        Assert.NotNull(clone);
        Assert.NotSame(original, clone);
        Assert.Equal(original.Position, clone!.Position);
        Assert.Equal(original.Name, clone.Name);
        Assert.Equal(original.BodyId, clone.BodyId);
        Assert.Equal((ushort)654, clone.GetSkill(SkillType.Wrestling));
        Assert.True(clone.TryGetTag("PACK", out string? tag));
        Assert.Equal("timber", tag);
    }

    [Fact]
    public void Skill_RoutesResolvedSkillToHostHandler()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        SkillType routed = SkillType.None;
        Character.OnScriptSkillUse = (_, skill) => { routed = skill; return true; };
        try
        {
            Assert.True(ch.TryExecuteCommand("SKILL", "AnimalLore", new Console()));
            Assert.Equal(SkillType.AnimalLore, routed);
            Assert.Equal(SkillType.AnimalLore, ch.Action);
        }
        finally
        {
            Character.OnScriptSkillUse = null;
        }
    }

    [Fact]
    public void SysMessageLocEx_SendsAffixClilocPacketToOwner()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        SphereNet.Network.Packets.PacketWriter? sent = null;
        Character.SendPacketToOwner = (owner, packet) => sent = packet;
        try
        {
            Assert.True(ch.TryExecuteCommand(
                "SYSMESSAGELOCEX", "0x03B2,1042971,1,[prefix],arg1,arg2", new Console()));
            Assert.NotNull(sent);
            Assert.Equal((byte)0xCC, sent!.Build().Span[0]);
        }
        finally
        {
            Character.SendPacketToOwner = null;
        }
    }
}
