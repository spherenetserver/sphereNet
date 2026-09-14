using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.MapData;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// DRAGWEIGHTMAX and MOVERATE — the rest of PLAN-302's first packet (port plan İŞ-69).
///
/// These two are the keys that decide what a load stops you doing rather than what it
/// costs you. DRAGWEIGHTMAX is the ceiling BACKPACKOVERLOAD stops at: overload says how
/// far past the carry weight a pack may be STUFFED, this says how far past it a hand may
/// REACH. MOVERATE is the value every CHARDEF starts from when it writes none of its
/// own.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DragWeightAndMoveRateTests
{
    private const ushort RockTile = 0x1779;

    private readonly ITestOutputHelper _out;
    public DragWeightAndMoveRateTests(ITestOutputHelper output) => _out = output;

    private sealed record Bench(GameWorld World, GameClient Client,
        SphereNet.Game.Objects.Characters.Character Me, Item Pack);

    private static Bench Setup(int str = 40)
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 6901);

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.Str = (short)str;            // carry weight = 40 + str*3.5
        me.MaxHits = 100; me.Hits = 100;
        me.Dex = 100; me.Stam = 100; me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);
        return new Bench(world, client, me, pack);
    }

    private static Item Rock(GameWorld world, int stones)
    {
        var it = world.CreateItem();
        it.BaseId = RockTile;
        it.TrySetProperty("BASEWEIGHT", (stones * Item.WeightUnits).ToString());
        return it;
    }

    // ---- DRAGWEIGHTMAX ---------------------------------------------------

    [Fact]
    public void AnItemThatWouldCrossTheLineCannotBeLifted()
    {
        var bench = Setup();                                   // carry weight 180
        Item.DragWeightMax = 300;                              // so: 540 stones
        var boulder = Rock(bench.World, 600);
        bench.World.PlaceItem(boulder, bench.Me.Position);

        bench.Client.HandleItemPickup(boulder.Uid.Value, 1);

        _out.WriteLine($"load if lifted: {bench.Me.GetWeightLoadPercent(boulder.TotalWeightTenths)}%");

        // The measurement is on the load the lift WOULD produce (CCharAct.cpp:2935),
        // not the current one, so the item that crosses the line is the one refused
        // rather than the one after it.
        Assert.False(bench.Me.TryGetTag("DRAGGING", out _));
        Assert.False(boulder.ContainedIn.IsValid);
    }

    [Fact]
    public void RaisingTheLimitLetsTheSameLiftThrough()
    {
        var bench = Setup();
        var boulder = Rock(bench.World, 600);
        bench.World.PlaceItem(boulder, bench.Me.Position);

        Item.DragWeightMax = 5000;
        bench.Client.HandleItemPickup(boulder.Uid.Value, 1);

        // PLAN-302's acceptance criterion again: the same lift, a different setting,
        // a different answer.
        Assert.True(bench.Me.TryGetTag("DRAGGING", out string? held));
        Assert.Equal(boulder.Uid.Value.ToString(), held);
    }

    [Fact]
    public void ZeroMeansNoLimitAtAll()
    {
        var bench = Setup();
        var boulder = Rock(bench.World, 100000);
        bench.World.PlaceItem(boulder, bench.Me.Position);

        Item.DragWeightMax = 0;
        bench.Client.HandleItemPickup(boulder.Uid.Value, 1);

        // Upstream gates the whole check on `> 0` (CCharAct.cpp:2932), so zero and
        // below are "do not check", not "allow nothing".
        Assert.True(bench.Me.TryGetTag("DRAGGING", out _));
    }

    [Fact]
    public void SomethingTooHeavyInYourOwnPackFallsAtYourFeetInsteadOfSticking()
    {
        var bench = Setup();
        Item.DragWeightMax = 300;

        // Got there by some other route - a script, a quest reward, a shrinking STR.
        var boulder = Rock(bench.World, 600);
        Assert.True(bench.Pack.TryAddItem(boulder));

        bench.Client.HandleItemPickup(boulder.Uid.Value, 1);

        _out.WriteLine($"contained={boulder.ContainedIn.IsValid} pos={boulder.Position}");

        // The escape hatch matters as much as the rule (CCharAct.cpp:2939, "we can
        // always drop it out of own pack!"): without it an overloaded player could
        // never put anything down again, and the rule would be a trap rather than a
        // limit.
        Assert.False(bench.Me.TryGetTag("DRAGGING", out _));
        Assert.False(boulder.ContainedIn.IsValid);
        Assert.Equal(bench.Me.X, boulder.X);
        Assert.Equal(bench.Me.Y, boulder.Y);
    }

    [Fact]
    public void AGmIsNotStoppedByIt()
    {
        var bench = Setup();
        Item.DragWeightMax = 1;
        bench.Me.PrivLevel = PrivLevel.GM;
        var boulder = Rock(bench.World, 600);
        bench.World.PlaceItem(boulder, bench.Me.Position);

        bench.Client.HandleItemPickup(boulder.Uid.Value, 1);

        Assert.True(bench.Me.TryGetTag("DRAGGING", out _));
    }

    [Fact]
    public void OnlyThePortionBeingLiftedIsWeighed()
    {
        var bench = Setup();
        Item.DragWeightMax = 300;                              // 540 stones

        var pile = Rock(bench.World, 1);
        pile.Amount = 1000;                                    // 1000 stones on the ground
        bench.World.PlaceItem(pile, bench.Me.Position);

        bench.Client.HandleItemPickup(pile.Uid.Value, 100);     // take a hundred

        _out.WriteLine($"lifted {pile.Amount} of the pile");

        // A partial lift weighs the part, not the pile. Weighing the whole stack would
        // make a large pile untouchable even one coin at a time.
        Assert.True(bench.Me.TryGetTag("DRAGGING", out _));
    }

    // ---- MOVERATE --------------------------------------------------------

    [Fact]
    public void ADefinitionWithoutAMoveRateTakesTheIniValue()
    {
        int saved = CharDef.DefaultMoveRate;
        try
        {
            CharDef.DefaultMoveRate = 250;
            var def = new CharDef(ResourceId.FromString("c_probe", ResType.CharDef));

            _out.WriteLine($"CHARDEF with no MOVERATE line -> {def.MoveRate}");

            // CCharBase's constructor seeds every definition from the ini
            // (CCharBase.cpp:37). Without this a shard that wants every creature
            // slower has to edit every CHARDEF instead of one line.
            Assert.Equal(250, def.MoveRate);
        }
        finally { CharDef.DefaultMoveRate = saved; }
    }

    [Fact]
    public void ADefinitionThatWritesItsOwnMoveRateKeepsIt()
    {
        int saved = CharDef.DefaultMoveRate;
        try
        {
            CharDef.DefaultMoveRate = 250;
            var def = new CharDef(ResourceId.FromString("c_probe", ResType.CharDef));
            def.LoadFromKey("MOVERATE", "50");

            // The default is a starting point, not a cap: a CHARDEF's own line wins,
            // in either direction.
            Assert.Equal(50, def.MoveRate);
        }
        finally { CharDef.DefaultMoveRate = saved; }
    }
}
