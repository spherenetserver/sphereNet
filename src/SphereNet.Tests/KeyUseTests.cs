using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Using a key (Source-X CChar::Use_Key, CCharUse.cpp:1330). A key's lock code is its
/// MORE1 and it fits the lock whose MORE1 is the same (IsKeyLockFit) - the way a
/// script cuts a key for a chest, and the way .LINK joins them - but SphereNet read
/// only LINK, so such a key fitted nothing. A key in a sub-bag was refused as "not in
/// your possession", a blank key could not be copied from a cut one, and locking or
/// unlocking said nothing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class KeyUseTests
{
    private const uint Code = 0x40001234;

    private static (GameClient Client, Character Player, GameWorld World, Item Bag) Setup()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 9611);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        var bag = world.CreateItem();
        bag.ItemType = ItemType.Container;
        pack.AddItem(bag);
        TestHarness.AttachCharacter(client, player);
        return (client, player, world, bag);
    }

    private static Item Key(GameWorld world, Item into, uint code)
    {
        var key = world.CreateItem();
        key.BaseId = 0x100E;
        key.ItemType = ItemType.Key;
        key.More1 = code;
        into.AddItem(key);
        return key;
    }

    private static Item Chest(GameWorld world, uint code)
    {
        var chest = world.CreateItem();
        chest.BaseId = 0x0E43;
        chest.ItemType = ItemType.Container;
        chest.More1 = code;
        world.PlaceItem(chest, new Point3D(101, 100, 0, 0));
        return chest;
    }

    private static string Use(GameClient client, Item key, Item target)
    {
        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleDoubleClick(key.Uid.Value);
        Assert.NotEqual(0u, client.ActiveTargetCursorId);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, target.Uid.Value, target.X, target.Y, target.Z, target.BaseId);
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

    [Fact]
    public void AKeyCutToTheChestsMore1LocksAndUnlocksIt()
    {
        var (client, _, world, bag) = Setup();
        var key = Key(world, bag, Code);
        var chest = Chest(world, Code);

        string said = Use(client, key, chest);
        Assert.Equal(ItemType.ContainerLocked, chest.ItemType);
        Assert.Contains(ServerMessages.Get(Msg.MsgKeyTargContLock), said);

        said = Use(client, key, chest);
        Assert.Equal(ItemType.Container, chest.ItemType);
        Assert.Contains(ServerMessages.Get(Msg.MsgKeyTargContUlock), said);
    }

    [Fact]
    public void AnotherLocksKeyIsTheWrongKey()
    {
        var (client, _, world, bag) = Setup();
        var key = Key(world, bag, Code + 1);
        var chest = Chest(world, Code);

        string said = Use(client, key, chest);

        Assert.Equal(ItemType.Container, chest.ItemType);
        Assert.Contains(ServerMessages.Get(Msg.MsgKeyWronglock), said);
    }

    [Fact]
    public void ABlankKeyIsCutFromAnotherAndThenFits()
    {
        var (client, player, world, bag) = Setup();
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        var cut = Key(world, bag, Code);
        var blank = Key(world, player.Backpack!, 0);
        var chest = Chest(world, Code);

        Assert.Contains(ServerMessages.Get(Msg.MsgKeyIsblank), Use(client, blank, chest));

        Use(client, blank, cut);
        Assert.Equal(Code, blank.More1);

        Use(client, blank, chest);
        Assert.Equal(ItemType.ContainerLocked, chest.ItemType);
    }

    [Fact]
    public void TwoBlanksOrTwoCutKeysCannotBeCopied()
    {
        var (client, _, world, bag) = Setup();
        var a = Key(world, bag, 0);
        var b = Key(world, bag, 0);
        Assert.Contains(ServerMessages.Get(Msg.MsgKeyBlanks), Use(client, a, b));

        a.More1 = Code;
        b.More1 = Code + 1;
        Assert.Contains(ServerMessages.Get(Msg.MsgKeyNotblanks), Use(client, a, b));
        Assert.Equal(Code + 1, b.More1);
    }

    /// <summary>.LINK is granted by the pack's [PLEVEL 4] and is no script's: such a
    /// command still runs as a verb on the speaker (Event_Command -> m_pChar->r_Verb,
    /// whose first stop is the client's verbs). It answered "not a valid command".</summary>
    [Fact]
    public void DotLinkCutsAKeyToAChest()
    {
        var (client, player, world, bag) = Setup();
        player.PrivLevel = PrivLevel.GM;
        var commands = new SphereNet.Game.Speech.CommandHandler();
        commands.RegisterDefaults(world);
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_plevel_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, """
            [PLEVEL 4]
            LINK
            """);
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(
            LoggerFactory.Create(_ => { }).CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? "",
        };
        resources.LoadResourceFile(file);
        commands.LoadScriptCommandPrivileges(resources);
        client.SetEngines(commands: commands);
        SphereNet.Game.Objects.ObjBase.ResolveClientConsole = ch => ch == player ? client : null;
        var key = Key(world, bag, 0);
        var chest = Chest(world, 0);

        client.HandleSpeech(0, 0x03B2, 3, ".link");
        Assert.NotEqual(0u, client.ActiveTargetCursorId);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, key.Uid.Value, 0, 0, 0, 0);
        Assert.NotEqual(0u, client.ActiveTargetCursorId);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, chest.Uid.Value, chest.X, chest.Y, chest.Z, 0);

        Assert.Equal(chest.Uid.Value, key.More1);
        Assert.Equal(chest.Uid.Value, chest.More1);
        Assert.True(Item.KeyFits(key, chest));
    }

    [Fact]
    public void AnEngineHouseKeyStillFitsByItsLink()
    {
        var world = TestHarness.CreateWorld();
        var house = world.CreateItem();
        var key = world.CreateItem();
        key.ItemType = ItemType.Key;
        key.Link = house.Uid;
        var door = world.CreateItem();
        door.ItemType = ItemType.DoorLocked;
        door.Link = house.Uid;

        Assert.True(Item.KeyFits(key, door));
        Assert.True(Item.KeyIsFor(key, house.Uid));
        Assert.False(Item.KeyIsBlank(key));
    }
}
