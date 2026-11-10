using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Rules of the reference engine that were missing or different here:
/// <list type="bullet">
/// <item>an attacker entry expires on ATTACKERTIMEOUT with or without a fight memory
/// (Attacker_CheckTimeout, CCharAttacker.cpp:392-424);</item>
/// <item>gear that has become too heavy falls into the pack when STR drops
/// (Stat_StrCheckEquip, CCharStat.cpp:793-804);</item>
/// <item>what never rots on the ground (CItem::GetDecayTime, CItem.cpp:1407-1439);</item>
/// <item>a region's script-made changes are written as its modified groups
/// (CRegion::r_WriteModified, CRegion.cpp:650-671);</item>
/// <item>the target gate ahead of every cursor (Event_Target,
/// CClientEvent.cpp:2495-2534);</item>
/// <item>REFUSETRADES / LASTUSED as player keys (CCharPlayer.cpp:243-267).</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ReferenceRuleGapTests
{
    private static Character Player(GameWorld world, short x, short y = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.IsOnline = true;
        ch.BodyId = 0x0190;
        ch.Str = ch.Dex = ch.Int = 50;
        ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(x, y, 0, 0));
        return ch;
    }

    // ---- attacker list ----

    [Fact]
    public void AnAttackerWithNoFightMemory_ExpiresOnTheTimeout()
    {
        int saved = Character.AttackerTimeoutSeconds;
        try
        {
            Character.AttackerTimeoutSeconds = 30;
            var world = TestHarness.CreateWorld();
            var victim = Player(world, 100);
            var attacker = Player(world, 101);
            victim.RecordAttack(attacker.Uid, 10);
            Assert.True(victim.CombatState.GetIndex(attacker.Uid) >= 0);

            long now = Environment.TickCount64;
            victim.CombatState.CheckAttackerTimeout(now + 5_000);
            Assert.True(victim.CombatState.GetIndex(attacker.Uid) >= 0);   // still fresh

            victim.CombatState.CheckAttackerTimeout(now + 40_000);
            Assert.True(victim.CombatState.GetIndex(attacker.Uid) < 0);    // 30 s without a hit
        }
        finally { Character.AttackerTimeoutSeconds = saved; }
    }

    [Fact]
    public void AnAttackerThatNoLongerExists_IsDropped()
    {
        var world = TestHarness.CreateWorld();
        var victim = Player(world, 100);
        var attacker = Player(world, 101);
        victim.RecordAttack(attacker.Uid, 10);
        world.DeleteObject(attacker);

        victim.CombatState.CheckAttackerTimeout(Environment.TickCount64 + 2_000);

        Assert.True(victim.CombatState.GetIndex(attacker.Uid) < 0);
    }

    // ---- strength and worn gear ----

    [Fact]
    public void LosingStrength_BouncesGearThatIsNowTooHeavy()
    {
        var world = TestHarness.CreateWorld();
        var ch = Player(world, 100);
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        var plate = world.CreateItem();
        plate.SetTag("OVERRIDE.REQSTR", "45");
        ch.Equip(plate, Layer.Chest);
        Assert.Same(plate, ch.GetEquippedItem(Layer.Chest));

        ch.ModStr = 10;                    // going up strips nothing
        Assert.Same(plate, ch.GetEquippedItem(Layer.Chest));

        ch.ModStr = -20;                   // 50 - 20 = 30 < 45

        Assert.Null(ch.GetEquippedItem(Layer.Chest));
        Assert.Equal(pack.Uid, plate.ContainedIn);
    }

    [Fact]
    public void AGameMaster_KeepsTheGear()
    {
        var world = TestHarness.CreateWorld();
        var ch = Player(world, 100);
        ch.PrivLevel = PrivLevel.GM;
        var plate = world.CreateItem();
        plate.SetTag("OVERRIDE.REQSTR", "45");
        ch.Equip(plate, Layer.Chest);

        ch.Str = 10;

        Assert.Same(plate, ch.GetEquippedItem(Layer.Chest));
    }

    // ---- decay ----

    [Fact]
    public void WhatCannotBeMoved_NeverRots()
    {
        var world = TestHarness.CreateWorld();
        var loose = world.CreateItem();
        var nailed = world.CreateItem();
        nailed.SetAttr(ObjAttributes.Move_Never);
        var secured = world.CreateItem();
        secured.SetAttr(ObjAttributes.Secure);

        Assert.Equal(GameWorld.DefaultDecayTimeMs, loose.GetDecayTime());
        Assert.Equal(-1, nailed.GetDecayTime());
        Assert.Equal(-1, secured.GetDecayTime());

        world.PlaceItemWithDecay(loose, new Point3D(100, 100, 0, 0));
        world.PlaceItemWithDecay(nailed, new Point3D(101, 100, 0, 0));
        Assert.True(loose.IsAttr(ObjAttributes.Decay));
        Assert.False(nailed.IsAttr(ObjAttributes.Decay));
    }

    // ---- region changes ----

    [Fact]
    public void ARegionWritesOnlyTheGroupsAScriptChanged()
    {
        var region = new Region { Name = "Britain", DefName = "a_britain" };
        region.SetTag("FROMDEF", "1");          // the definition loader: not a change
        Assert.Equal(0, region.ModifiedFlags);
        Assert.Empty(region.ModifiedLines());

        Assert.True(region.TrySetProperty("GUARDED", "1"));
        Assert.True(region.TrySetProperty("TAG.OWNER", "Lord British"));

        var lines = region.ModifiedLines().ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Contains("FLAGS", lines.Keys);
        Assert.Equal("Lord British", lines["TAG.OWNER"]);
        Assert.Equal("1", lines["TAG.FROMDEF"]);     // every tag once tags are modified
        Assert.DoesNotContain("NAME", lines.Keys);
        Assert.DoesNotContain("EVENTS", lines.Keys);

        // Replayed onto a fresh copy of the definition, the changes come back.
        var reloaded = new Region { Name = "Britain", DefName = "a_britain" };
        foreach (var (key, value) in lines)
            Assert.True(reloaded.TrySetProperty(key, value));
        Assert.True(reloaded.IsGuarded);
        Assert.True(reloaded.TryGetTag("OWNER", out string? owner));
        Assert.Equal("Lord British", owner);
        Assert.NotEqual(0, reloaded.ModifiedFlags);  // so the next save keeps them
    }

    // ---- the target gate ----

    [Fact]
    public void APickedObject_ReplacesTheClientCoordinates()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var player = Player(world, 100);
        var other = Player(world, 103);
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 33001);
        TestHarness.AttachCharacter(client, player);
        (uint serial, short x, short y)? seen = null;
        client.SetPendingTarget((s, x, y, _, _) => seen = (s, x, y), 0);

        // A forged far-away point riding on a visible serial.
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, other.Uid.Value, 900, 900, 0, 0);

        Assert.NotNull(seen);
        Assert.Equal(other.Uid.Value, seen!.Value.serial);
        Assert.Equal((other.X, other.Y), (seen.Value.x, seen.Value.y));
    }

    [Fact]
    public void AHiddenCharacterOrAFarPoint_IsNoTarget()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var player = Player(world, 100);
        var hidden = Player(world, 102);
        hidden.SetStatFlag(StatFlag.Hidden);
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 33002);
        TestHarness.AttachCharacter(client, player);
        int calls = 0;

        client.SetPendingTarget((_, _, _, _, _) => calls++, 0);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, hidden.Uid.Value, hidden.X, hidden.Y, 0, 0);
        Assert.Equal(0, calls);

        client.SetPendingTarget((_, _, _, _, _) => calls++, 1);
        client.HandleTargetResponse(1, client.ActiveTargetCursorId, 0, 900, 900, 0, 0);
        Assert.Equal(0, calls);

        client.SetPendingTarget((_, _, _, _, _) => calls++, 1);
        client.HandleTargetResponse(1, client.ActiveTargetCursorId, 0, 104, 101, 0, 0);
        Assert.Equal(1, calls);
    }

    // ---- player keys ----

    [Fact]
    public void RefuseTradesIsAPlayerKey()
    {
        var world = TestHarness.CreateWorld();
        var ch = Player(world, 100);

        Assert.True(ch.TryGetProperty("REFUSETRADES", out string off));
        Assert.Equal("0", off);

        Assert.True(ch.TrySetProperty("REFUSETRADES", "5"));
        Assert.True(ch.TryGetProperty("REFUSETRADES", out string on));
        Assert.Equal("1", on);
        Assert.True(ch.TryGetTag("REFUSETRADES", out string? tag));
        Assert.Equal("1", tag);
    }

    [Fact]
    public void LastUsedReadsAsSecondsSinceTheStamp()
    {
        var world = TestHarness.CreateWorld();
        var ch = Player(world, 100);
        long nowSec = world.GameClockMs / 1000;
        ch.SetTag("LASTUSED", (nowSec - 90).ToString());

        Assert.True(ch.TryGetProperty("LASTUSED", out string value));
        Assert.Equal("90", value);
    }
}
