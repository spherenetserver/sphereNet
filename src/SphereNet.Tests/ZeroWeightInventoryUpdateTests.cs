using System.Buffers.Binary;
using System.Reflection;
using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Scripting.Execution;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ZeroWeightInventoryUpdateTests : IDisposable
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type Program = typeof(SphereNet.Server.Program);
    private readonly Dictionary<FieldInfo, object?> _saved = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_inventory_" + Guid.NewGuid().ToString("N"));
    private ScriptRuntimeStack _runtime = null!;
    private MapDataManager _map = null!;
    private GameWorld _world = null!;
    private GameClient _client = null!;
    private Item _pack = null!;
    private System.Collections.IDictionary _clients = null!;
    private object? _previousClient;
    private Action<Item, SphereNet.Core.Interfaces.ITextConsole>? _oldOpen;

    private void Initialize()
    {
        _oldOpen = Item.OnScriptOpen;
        _runtime = ScriptTestBootstrap.CreateRuntimeStack();
        Directory.CreateDirectory(_dir);
        string defs = Path.Combine(_dir, "reward.scp");
        File.WriteAllText(defs, """
            [ITEMDEF 0eed]
            DEFNAME=i_gold
            TYPE=t_gold
            WEIGHT=0
            CAN=0100
            [DEFNAME sn_player_settings]
            sn_player_gold_amount 65000
            [FUNCTION f_reward_probe]
            IF !(<ISPLAYER>)
                RETURN 1
            ENDIF
            IF !(<FINDLAYER.21>)
                RETURN 1
            ENDIF
            SERV.NEWITEM i_gold,1
            IF !(<NEW>)
                RETURN 1
            ENDIF
            REF1=<NEW>
            REF1.MAXAMOUNT=<DEF.sn_player_gold_amount>
            REF1.AMOUNT=<DEF.sn_player_gold_amount>
            REF1.CONT=<FINDLAYER.21>
            IF (<REF1.CONT> != <FINDLAYER.21>)
                REF1.REMOVE
                RETURN 1
            ENDIF
            REF1.UPDATE
            FINDLAYER.21.OPEN
            RETURN 1
            """);
        _runtime.Resources.LoadResourceFile(defs);
        // Delivery sequence from wiki/script.txt, with Source-X's weightless
        // i_gold definition. Embedded so CI does not depend on the local file.
        ScriptTestBootstrap.LoadDefinitions(_runtime.Resources);
        _world = TestHarness.CreateWorld();
        _map = new MapDataManager("");
        _map.AddSyntheticMap(0, 256, 256);
        _map.SetSyntheticItemTile(0x0EED, new ItemTileData { Flags = TileFlag.Generic, Weight = 0 });
        _world.MapData = _map;
        ObjBase.ResolveWorld = () => _world;
        Item.ResolveWorld = () => _world;
        SphereNet.Game.Trade.VendorEngine.World = _world;
        SaveField("_world", _world);
        SaveField("_resources", _runtime.Resources);
        var resolver = Program.GetMethod("ResolveServerProperty", Flags)!;
        _runtime.Interpreter.ServerPropertyResolver = p => (string?)resolver.Invoke(null, [p]);
        _runtime.Interpreter.ResolveObjectRef = (obj, head) => obj is ObjBase o ? o.ResolveScriptRefHead(head) : null;
        _runtime.Interpreter.FunctionLookup = _runtime.Runner.HasFunction;
        _client = TestHarness.CreateClient(_runtime.LoggerFactory, _world,
            new AccountManager(_runtime.LoggerFactory), 8970);
        var me = _world.CreateCharacter();
        me.IsPlayer = true;
        me.IsOnline = true;
        me.Str = 100;
        me.SetTag("sn_player.lang", "2");
        _world.PlaceCharacter(me, new(100, 100, 0, 0));
        TestHarness.AttachCharacter(_client, me);
        _pack = _world.CreateItem();
        _pack.BaseId = 0x0E75;
        _pack.ItemType = ItemType.Container;
        me.Backpack = _pack;
        me.Equip(_pack, Layer.Pack);
        _clients = (System.Collections.IDictionary)Program.GetField("_clientsByCharUid", Flags)!.GetValue(null)!;
        _previousClient = _clients[me.Uid];
        _clients[me.Uid] = _client;
        Item.OnScriptOpen = (item, console) =>
        {
            if (console is GameClient client) client.OpenContainerFromScript(item);
        };
        _client.SendCharacterStatus(me);
        _world.ConsumeDirtyObjects();
        TestHarness.ClearQueuedPackets(_client.NetState);
    }

    private void SaveField(string name, object value)
    {
        var field = Program.GetField(name, Flags)!;
        _saved.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private void RunReward()
    {
        Assert.True(_runtime.Runner.TryRunFunction("f_reward_probe", _client.Character!, _client,
            new TriggerArgs { Source = _client.Character }, out _));
    }

    private void FlushDirty()
    {
        var dispatch = Program.GetMethod("MarkClientsNearDirtyObject", Flags)!;
        foreach (var changed in _world.DrainDirtyObjectsSnapshot()) dispatch.Invoke(null, [changed]);
    }

    [Fact]
    public void MenuRewardIsInTheServerPackAndContentPacketBeforeAnyMovement()
    {
        Initialize();
        RunReward();
        var gold = Assert.Single(_pack.Contents, i => i.ItemType == ItemType.Gold);
        Assert.Equal(65000, gold.Amount);
        Assert.Equal(0, gold.Weight);
        Assert.Equal(_pack.Uid, gold.ContainedIn);
        var content = Assert.Single(TestHarness.GetQueuedPackets(_client.NetState), p => p.Span[0] == 0x3C);
        Assert.Equal(gold.Uid.Value, BinaryPrimitives.ReadUInt32BigEndian(content.Span[5..]));
        Assert.Equal(65000, BinaryPrimitives.ReadUInt16BigEndian(content.Span[12..]));
    }

    [Fact]
    public void ZeroWeightMenuRewardRefreshesStatusWithoutMovingAnything()
    {
        Initialize();
        RunReward();
        FlushDirty();
        Assert.Equal(65000, SphereNet.Game.Trade.VendorEngine.CountGold(_client.Character!));
        Assert.True(_client.Character!.LastConsumedDirtyFlags.HasFlag(DirtyFlag.Stats));
        var packet = Assert.Single(TestHarness.GetQueuedPackets(_client.NetState), p => p.Span[0] == 0x11);
        Assert.Equal(65000u, BinaryPrimitives.ReadUInt32BigEndian(packet.Span[58..]));
    }

    [Fact]
    public void ScriptContainerAssignmentSendsAnItemUpdateWithoutReopeningTheBag()
    {
        Initialize();
        var item = _world.CreateItem();
        item.BaseId = 0x0F7A;
        item.ItemType = ItemType.Normal;
        item.TrySetProperty("BASEWEIGHT", "0");
        item.Amount = 10;
        Assert.True(item.TrySetProperty("CONT", $"0{_pack.Uid.Value:X}"));
        Assert.Equal(_pack.Uid, item.ContainedIn);
        FlushDirty();
        Assert.Contains(TestHarness.GetQueuedPackets(_client.NetState), p => p.Span[0] == 0x25 &&
            BinaryPrimitives.ReadUInt32BigEndian(p.Span[1..]) == item.Uid.Value);
    }

    [Fact]
    public void WeightlessStackChangesAndRemovalRefreshStatus()
    {
        Initialize();
        RunReward();
        FlushDirty();
        var gold = Assert.Single(_pack.Contents);
        TestHarness.ClearQueuedPackets(_client.NetState);
        gold.Amount = 64000;
        FlushDirty();
        var status = Assert.Single(TestHarness.GetQueuedPackets(_client.NetState), p => p.Span[0] == 0x11);
        Assert.Equal(64000u, BinaryPrimitives.ReadUInt32BigEndian(status.Span[58..]));
        Assert.Contains(TestHarness.GetQueuedPackets(_client.NetState), p => p.Span[0] == 0x25);
        TestHarness.ClearQueuedPackets(_client.NetState);
        gold.Amount = 64000;
        FlushDirty();
        Assert.Empty(TestHarness.GetQueuedPackets(_client.NetState));
        Assert.True(_pack.RemoveItem(gold));
        FlushDirty();
        status = Assert.Single(TestHarness.GetQueuedPackets(_client.NetState), p => p.Span[0] == 0x11);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(status.Span[58..]));
    }

    public void Dispose()
    {
        if (_previousClient == null) _clients.Remove(_client.Character!.Uid);
        else _clients[_client.Character!.Uid] = _previousClient;
        foreach (var (field, value) in _saved) field.SetValue(null, value);
        Item.OnScriptOpen = _oldOpen;
        _map.Dispose();
        _runtime.LoggerFactory.Dispose();
        Directory.Delete(_dir, true);
    }
}
