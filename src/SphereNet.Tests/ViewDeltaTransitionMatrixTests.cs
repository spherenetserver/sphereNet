using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.State;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Where the client thinks an object is, as the object moves (review work item D05).
///
/// Counting packets does not answer that question. A delta can send the right number
/// of packets and still leave a uid in two places at once — on the ground and inside
/// a bag — or in none, which is the same bug seen from the other side: the object is
/// on screen and cannot be reached, or it is reachable and invisible. So these tests
/// keep a small model of what each client has been TOLD (ground / inside a container /
/// worn / gone) and assert the uid is in exactly one of them after every transition.
///
/// Three observers watch the same world, because the view gates differ per observer
/// and a rule that reads right for one of them can leak for another: an ordinary
/// player, a GM with AllShow, and a modern client that receives 0xF3 where the others
/// receive 0x1A.
/// </summary>
public sealed class ViewDeltaTransitionMatrixTests
{
    private readonly ITestOutputHelper _out;
    public ViewDeltaTransitionMatrixTests(ITestOutputHelper output) => _out = output;

    // ---- a model of what one client has been told ------------------------

    private enum Where { Nowhere, Ground, Container, Worn }

    /// <summary>Replays a client's outbound packets into "where does this client
    /// think each item is". Only the four packets that move an item between the
    /// client's three homes are interpreted; everything else is ignored.</summary>
    private sealed class ClientModel
    {
        private readonly NetState _state;
        private readonly Dictionary<uint, Where> _where = [];
        public readonly List<string> Log = [];

        public ClientModel(NetState state) => _state = state;

        public Where this[Item item] => _where.GetValueOrDefault(item.Uid.Value, Where.Nowhere);

        /// <summary>Replay the client's whole outbound history in flush order. The
        /// harness snapshot does not drain, so replaying all of it - rather than
        /// consuming a tail - is both simpler and the more faithful model: it answers
        /// "given everything this client has been sent, where does it think the item
        /// is now".</summary>
        public void Pump()
        {
            _where.Clear();
            Log.Clear();
            foreach (var packet in TestHarness.GetQueuedPackets(_state))
            {
                var span = packet.Span;
                if (span.Length == 0) continue;
                switch (span[0])
                {
                    case 0x1A:                       // world item (classic)
                        // The top bit of the serial is a FLAG here - the writer sets
                        // it when an amount field follows - so it has to come off
                        // before the value is a uid.
                        Record(ReadUInt(span, 3) & 0x7FFFFFFFu, Where.Ground, "0x1A");
                        break;
                    case 0xF3:                       // world item (Stygian Abyss+)
                        Record(ReadUInt(span, 4), Where.Ground, "0xF3");
                        break;
                    case 0x25:                       // added to a container
                        Record(ReadUInt(span, 1), Where.Container, "0x25");
                        break;
                    case 0x2E:                       // worn on a mobile
                        Record(ReadUInt(span, 1), Where.Worn, "0x2E");
                        break;
                    case 0x1D:                       // delete object
                        Record(ReadUInt(span, 1), Where.Nowhere, "0x1D");
                        break;
                }
            }
        }

        private void Record(uint uid, Where where, string opcode)
        {
            _where[uid] = where;
            Log.Add($"{opcode}->{where}");
        }

        private static uint ReadUInt(ReadOnlySpan<byte> span, int offset) =>
            span.Length < offset + 4 ? 0u
            : (uint)((span[offset] << 24) | (span[offset + 1] << 16) |
                     (span[offset + 2] << 8) | span[offset + 3]);
    }

    // ---- the world the observers watch -----------------------------------

    private sealed class Stage
    {
        public GameWorld World = null!;
        public Character Actor = null!;
        public GameClient Plain = null!, Gm = null!, Modern = null!;
        public Character PlainChar = null!;
        public NetState PlainState = null!, GmState = null!, ModernState = null!;
        public ClientModel PlainView = null!, GmView = null!, ModernView = null!;

        public IEnumerable<NetState> States()
        {
            yield return PlainState; yield return GmState; yield return ModernState;
        }

        public IEnumerable<(string Name, GameClient Client, ClientModel Model)> Observers()
        {
            yield return ("plain", Plain, PlainView);
            yield return ("gm", Gm, GmView);
            yield return ("modern", Modern, ModernView);
        }

        /// <summary>One tick of the view pipeline for every observer, then read what
        /// each of them was told.</summary>
        public void Tick()
        {
            foreach (var (_, client, model) in Observers())
            {
                client.ViewNeedsRefresh = true;
                var delta = client.BuildViewDelta();
                if (delta != null)
                    client.ApplyViewDelta(delta);
                model.Pump();
            }
        }
    }

    private Stage NewStage()
    {
        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);

        var world = new GameWorld(lf);
        world.InitMap(0, 512, 512);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var accounts = new AccountManager(lf);
        var stage = new Stage { World = world };

        var actor = world.CreateCharacter();
        actor.IsPlayer = true;
        actor.MaxHits = 100; actor.Hits = 100;
        world.PlaceCharacter(actor, new Point3D(100, 100, 0, 0));
        stage.Actor = actor;

        GameClient Observer(int id, Action<Character, NetState> shape, out ClientModel model,
            out NetState netState, out Character observerChar)
        {
            var state = TestHarness.CreateActiveNetState(lf, id);
            var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.MaxHits = 100; ch.Hits = 100;
            world.PlaceCharacter(ch, new Point3D(102, 100, 0, 0));
            shape(ch, state);
            TestHarness.AttachCharacter(client, ch);
            observerChar = ch;
            model = new ClientModel(state);
            netState = state;
            return client;
        }

        stage.Plain = Observer(1, (_, _) => { }, out var plainModel, out var plainState, out var plainChar);
        stage.Gm = Observer(2, (ch, _) =>
        {
            ch.PrivLevel = PrivLevel.GM;
            ch.AllShow = true;
        }, out var gmModel, out var gmState, out _);
        // 7.0.20 - the version the packet-compatibility work targets. It receives
        // 0xF3 where the other two receive 0x1A, which is the whole point of having
        // it watch: one view rule, two wire formats.
        stage.Modern = Observer(3, (_, state) => state.ClientVersionNumber = 70_020_000,
            out var modernModel, out var modernState, out _);
        stage.PlainView = plainModel; stage.PlainState = plainState; stage.PlainChar = plainChar;
        stage.GmView = gmModel; stage.GmState = gmState;
        stage.ModernView = modernModel; stage.ModernState = modernState;

        // Drain the login-time traffic so each test starts from a known screen.
        stage.Tick();
        return stage;
    }

    private Item GroundItem(Stage stage, short x = 101, short y = 100)
    {
        var item = stage.World.CreateItem();
        item.BaseId = 0x0EED;
        item.Amount = 1;
        stage.World.PlaceItem(item, new Point3D(x, y, 0, 0));
        return item;
    }

    private void AssertExactlyOneHome(Stage stage, Item item, Where expected)
    {
        foreach (var (name, _, model) in stage.Observers())
            Assert.True(model[item] == expected,
                $"{name}: expected the item to be {expected} on the client, it is {model[item]}; " +
                $"it saw [{string.Join(",", model.Log)}]");
    }

    // ---- the journey -----------------------------------------------------

    [Fact]
    public void AnItemPickedUpOffTheGroundStopsBeingOnTheGround()
    {
        var stage = NewStage();
        var item = GroundItem(stage);
        stage.Tick();
        AssertExactlyOneHome(stage, item, Where.Ground);

        // Off the ground, held by nobody the observers can see: the client has to be
        // told, or the item stays on their screen where it is not.
        stage.World.RemoveItem(item);
        stage.Tick();

        _out.WriteLine($"plain saw [{string.Join(",", stage.PlainView.Log)}]");
        AssertExactlyOneHome(stage, item, Where.Nowhere);
    }

    [Fact]
    public void AnItemThatBecomesWornIsNotAlsoDeletedFromTheClient()
    {
        var stage = NewStage();
        var item = GroundItem(stage);
        stage.Tick();
        AssertExactlyOneHome(stage, item, Where.Ground);

        // Equipping re-homes the item client-side with 0x2E. A 0x1D after that would
        // delete the item the client has just put on the mobile - the "equip it and it
        // vanishes until you teleport" report - so the ground view has to let go of
        // the uid without deleting it.
        stage.World.HideFromSector(item);
        Assert.True(stage.Actor.Equip(item, Layer.Shirt));
        foreach (var state in stage.States())
            state.Send(new SphereNet.Network.Packets.Outgoing.PacketWornItem(
                item.Uid.Value, item.DispIdFull, (byte)Layer.Shirt, stage.Actor.Uid.Value, 0));
        stage.Tick();

        _out.WriteLine($"plain saw [{string.Join(",", stage.PlainView.Log)}]");
        AssertExactlyOneHome(stage, item, Where.Worn);
    }

    [Fact]
    public void AnItemPutIntoAContainerEndsUpInExactlyOnePlace()
    {
        var stage = NewStage();
        var bag = stage.World.CreateItem();
        bag.BaseId = 0x0E75;
        bag.ItemType = ItemType.Container;
        stage.World.PlaceItem(bag, new Point3D(101, 101, 0, 0));

        var item = GroundItem(stage);
        stage.Tick();
        AssertExactlyOneHome(stage, item, Where.Ground);

        // The order a drop takes: the item leaves the ground, the client is told it is
        // in the bag, and only then does the view delta run. Whatever it decides, the
        // uid must not end up in two homes at once.
        stage.World.HideFromSector(item);
        Assert.True(bag.AddItem(item));
        foreach (var (_, client, _) in stage.Observers())
            client.SendContainerItem(new SphereNet.Network.Packets.Outgoing.PacketContainerItem(
                item.Uid.Value, item.DispIdFull, 0, item.Amount, 20, 20, bag.Uid.Value, 0));
        stage.Tick();

        _out.WriteLine($"plain saw [{string.Join(",", stage.PlainView.Log)}]");
        AssertExactlyOneHome(stage, item, Where.Container);
    }

    [Fact]
    public void AnItemTakenBackOutOfTheContainerIsOnTheGroundAgain()
    {
        var stage = NewStage();
        var bag = stage.World.CreateItem();
        bag.BaseId = 0x0E75;
        bag.ItemType = ItemType.Container;
        stage.World.PlaceItem(bag, new Point3D(101, 101, 0, 0));

        var item = GroundItem(stage);
        stage.Tick();
        stage.World.HideFromSector(item);
        Assert.True(bag.AddItem(item));
        foreach (var (_, client, _) in stage.Observers())
            client.SendContainerItem(new SphereNet.Network.Packets.Outgoing.PacketContainerItem(
                item.Uid.Value, item.DispIdFull, 0, item.Amount, 20, 20, bag.Uid.Value, 0));
        stage.Tick();
        AssertExactlyOneHome(stage, item, Where.Container);

        // Back to the ground: the delta has never seen this uid before as far as its
        // known-set is concerned, so it must announce it rather than assume the client
        // still has it somewhere.
        bag.RemoveItem(item);
        Assert.True(stage.World.PlaceItem(item, new Point3D(103, 100, 0, 0)));
        stage.Tick();

        _out.WriteLine($"plain saw [{string.Join(",", stage.PlainView.Log)}]");
        AssertExactlyOneHome(stage, item, Where.Ground);
    }

    // ---- change in place -------------------------------------------------

    [Fact]
    public void ARecolouredOrRestackedItemIsResentWithoutADelete()
    {
        var stage = NewStage();
        var item = GroundItem(stage);
        item.Amount = 5;
        stage.Tick();
        AssertExactlyOneHome(stage, item, Where.Ground);

        item.Hue = new Color(0x0026);
        item.Amount = 3;
        stage.Tick();

        // Still exactly one home, and the client was told the new state rather than
        // being left with the old one.
        AssertExactlyOneHome(stage, item, Where.Ground);
        Assert.DoesNotContain(stage.PlainView.Log, entry => entry.StartsWith("0x1D"));
        _out.WriteLine($"plain saw [{string.Join(",", stage.PlainView.Log)}]");
    }

    [Fact]
    public void AQuiescentWorldSendsNothingAtAll()
    {
        var stage = NewStage();
        var item = GroundItem(stage);
        stage.Tick();
        int sentSoFar = stage.PlainView.Log.Count;

        // Nothing changed, so nothing may be sent. A delta that re-announces what the
        // client already has costs bandwidth on every tick of an idle shard, and it is
        // the kind of waste that only shows up under load.
        for (int i = 0; i < 5; i++) stage.Tick();

        _out.WriteLine($"after five idle ticks: {sentSoFar} -> {stage.PlainView.Log.Count} packets " +
                       $"[{string.Join(",", stage.PlainView.Log)}]");
        Assert.Equal(sentSoFar, stage.PlainView.Log.Count);
        Assert.Equal(Where.Ground, stage.PlainView[item]);
    }

    // ---- who may see what ------------------------------------------------

    [Fact]
    public void AnInvisibleItemReachesStaffAndNobodyElse()
    {
        var stage = NewStage();
        var item = GroundItem(stage);
        item.SetAttr(ObjAttributes.Invis);
        stage.Tick();

        // The spawn gems and trigger tiles a pack is built from: a GM auditing them on
        // sight is the point, an ordinary player seeing them is a leak.
        _out.WriteLine($"invisible item: plain={stage.PlainView[item]} gm={stage.GmView[item]} modern={stage.ModernView[item]}");
        Assert.Equal(Where.Nowhere, stage.PlainView[item]);
        Assert.Equal(Where.Nowhere, stage.ModernView[item]);
        Assert.Equal(Where.Ground, stage.GmView[item]);
    }

    [Fact]
    public void AnItemThatLeavesViewRangeIsRemovedAndComesBackWhenItReturns()
    {
        var stage = NewStage();
        var item = GroundItem(stage);
        stage.Tick();
        AssertExactlyOneHome(stage, item, Where.Ground);

        Assert.True(stage.World.PlaceItem(item, new Point3D(400, 400, 0, 0)));
        stage.Tick();
        AssertExactlyOneHome(stage, item, Where.Nowhere);

        Assert.True(stage.World.PlaceItem(item, new Point3D(101, 100, 0, 0)));
        stage.Tick();

        // Coming back has to work without a resync: the known-set forgot it on the way
        // out, so the return is an ordinary new object.
        _out.WriteLine($"plain saw [{string.Join(",", stage.PlainView.Log)}]");
        AssertExactlyOneHome(stage, item, Where.Ground);
    }

    [Fact]
    public void MoreItemsThanOneTileCanShowStillLeavesTheRestReachable()
    {
        var stage = NewStage();
        var items = new List<Item>();
        for (int i = 0; i < 100; i++)
            items.Add(GroundItem(stage));       // all on the same tile

        stage.Tick();

        int shown = items.Count(i => stage.PlainView[i] == Where.Ground);
        _out.WriteLine($"100 items on one tile: {shown} sent to the client");

        // The per-tile cap exists so one heap cannot flood a client. What matters for
        // correctness is that the cap is a CAP and not a lottery: the same items are
        // chosen every tick, so nothing flickers in and out.
        Assert.Equal(80, shown);
        var firstPass = items.Where(i => stage.PlainView[i] == Where.Ground)
                             .Select(i => i.Uid.Value).ToHashSet();
        stage.Tick();
        var secondPass = items.Where(i => stage.PlainView[i] == Where.Ground)
                              .Select(i => i.Uid.Value).ToHashSet();
        Assert.Equal(firstPass, secondPass);
    }
    // ---- the path a player actually takes ---------------------------------

    [Fact]
    public void PickingAnItemUpAndDroppingItInYourPackDoesNotDeleteIt()
    {
        // The whole sequence through the real handlers, because this is where the
        // defect lived: pick an item off the floor, drop it in your own backpack, and
        // the next view delta told the client to delete the item it had just been
        // told was in the pack. The item stayed in the pack on the server, so the
        // report is "it disappeared from my bag until I closed and reopened it".
        var stage = NewStage();
        var me = stage.Plain;
        var meChar = stage.PlainChar;
        meChar.Str = 80;

        var pack = stage.World.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        Assert.True(meChar.Equip(pack, Layer.Pack));

        var item = GroundItem(stage, (short)(meChar.X + 1), meChar.Y);
        stage.Tick();
        Assert.Equal(Where.Ground, stage.PlainView[item]);

        me.HandleItemPickup(item.Uid.Value, 1);
        me.HandleItemDrop(item.Uid.Value, 20, 20, 0, pack.Uid.Value);
        stage.Tick();

        _out.WriteLine($"plain saw [{string.Join(",", stage.PlainView.Log)}]");
        Assert.Equal(pack.Uid, item.ContainedIn);
        Assert.Equal(Where.Container, stage.PlainView[item]);
    }

    [Fact]
    public void AnItemThatLeavesTheGroundWithNothingReHomingItIsStillDeleted()
    {
        // The other half of the same rule, and the reason it cannot simply be "never
        // delete an item that left the ground": when nothing has told the client where
        // the item went, the ground copy is a ghost the player can see and cannot
        // touch. The delete has to keep happening there.
        var stage = NewStage();
        var item = GroundItem(stage);
        stage.Tick();
        Assert.Equal(Where.Ground, stage.PlainView[item]);

        // A script moving an item into a container nobody has open: no 0x25 is sent to
        // anyone, so the ground view still owns the uid.
        var crate = stage.World.CreateItem();
        crate.BaseId = 0x0E3C;
        crate.ItemType = ItemType.Container;
        stage.World.PlaceItem(crate, new Point3D(150, 150, 0, 0));
        stage.World.HideFromSector(item);
        Assert.True(crate.AddItem(item));
        stage.Tick();

        _out.WriteLine($"plain saw [{string.Join(",", stage.PlainView.Log)}]");
        AssertExactlyOneHome(stage, item, Where.Nowhere);
    }
}
