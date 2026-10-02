using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// World load, persistence and runtime rules checked against Source-X, each through a
/// hand-written save in the classic text form and a save -> load round trip:
/// <list type="bullet">
/// <item>a container keeps every item a save puts in it - capacity is a drop rule
/// (CItemContainer::CanContainerHold), never a load rule (ContentAdd while
/// IsLoadingGeneric);</item>
/// <item>two items a save wears on the same slot both stay worn (LayerAdd skips
/// CanEquipLayer while loading, CCharAct.cpp:266);</item>
/// <item>what an offline player carries sleeps, keeping its deadline, and wakes when
/// they log in (CChar::SetDisconnected -> _GoSleep, CChar::_GoAwake);</item>
/// <item>a decaying item's saved TIMER is its decay - no second default deadline;</item>
/// <item>the vendor boxes save with what they hold, like every worn container.</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class LoadPersistenceRuntimeParityTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); } catch { }
    }

    private string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_lpr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static WorldLoader NewLoader() => new(LoggerFactory.Create(_ => { }));

    private static void Save(GameWorld world, string dir) =>
        new WorldSaver(LoggerFactory.Create(_ => { })).Save(world, dir);

    /// <summary>Write a classic save: the given records as sphereworld.scp.</summary>
    private string WriteSave(string body)
    {
        string dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "sphereworld.scp"),
            body.Replace("\r\n", "\n") + "\n[EOF]\n");
        return dir;
    }

    private static string Attr(ObjAttributes a) => "0" + ((ulong)a).ToString("x");

    // ------------------------------------------------------------ container capacity

    private static string ContainerSave(int count, string? overrideTag)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[WORLDITEM]\nSERIAL=040000001\nID=0E75\nTYPE=t_container\nP=1000,1000,0,0\n");
        if (overrideTag != null)
            sb.Append($"TAG.OVERRIDE.MAXITEMS={overrideTag}\n");
        sb.Append('\n');
        for (int i = 0; i < count; i++)
        {
            sb.Append($"[WORLDITEM]\nSERIAL=0{0x40000100 + i:X8}\nID=0EED\nAMOUNT=60000\n");
            sb.Append($"P={40 + i % 100},{60 + i / 100},0\nCONT=040000001\n\n");
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData(264, "300")] // a pack the shard raised with TAG.OVERRIDE.MAXITEMS
    [InlineData(279, null)]  // a bank a staff member filled past the client limit
    public void AContainerSavedOverTheClientLimit_LoadsWhole_AndSavesWhole(int count, string? tag)
    {
        var world = TestHarness.CreateWorld();
        NewLoader().Load(world, WriteSave(ContainerSave(count, tag)));

        var box = world.FindItem(new Serial(0x40000001))!;
        Assert.Equal(count, box.ContentCount);
        // Nothing went to the ground: every child still names the box.
        for (int i = 0; i < count; i++)
        {
            var child = world.FindItem(new Serial((uint)(0x40000100 + i)))!;
            Assert.Equal(box.Uid, child.ContainedIn);
            Assert.False(child.IsOnGround);
        }

        string dir = NewDir();
        Save(world, dir);
        var again = TestHarness.CreateWorld();
        NewLoader().Load(again, dir);
        Assert.Equal(count, again.FindItem(new Serial(0x40000001))!.ContentCount);
    }

    [Fact]
    public void AnOrdinaryContainer_LoadsAsBefore()
    {
        var world = TestHarness.CreateWorld();
        NewLoader().Load(world, WriteSave(ContainerSave(40, null)));
        Assert.Equal(40, world.FindItem(new Serial(0x40000001))!.ContentCount);
    }

    [Fact]
    public void AtRuntime_TheClientLimitStillHolds_UnlessTheContainerRaisesIt()
    {
        var world = TestHarness.CreateWorld();
        var box = world.CreateItem();
        box.BaseId = 0x0E75;
        for (int i = 0; i < Item.MaxContainerItems; i++)
        {
            var it = world.CreateItem();
            it.BaseId = 0x0EED;
            Assert.True(box.TryAddItem(it));
        }
        var extra = world.CreateItem();
        extra.BaseId = 0x0EED;
        Assert.False(box.TryAddItem(extra));

        // OVERRIDE.MAXITEMS is what CanContainerHold reads (CItemContainer.cpp:899);
        // a drop it accepts is not refused again underneath.
        box.SetTag("OVERRIDE.MAXITEMS", "300");
        Assert.True(box.TryAddItem(extra));
    }

    [Fact]
    public void ACycleIsStillRefusedOnLoad()
    {
        var world = TestHarness.CreateWorld();
        var a = world.CreateItem();
        var b = world.CreateItem();
        Assert.True(a.AddItemOnLoad(b));
        Assert.False(b.AddItemOnLoad(a));
        Assert.True(b.WouldFormCycle(a));
    }

    // ------------------------------------------------------------ two items, one slot

    private const string TwoInOneHand = """
        [WORLDCHAR c_man]
        SERIAL=01000
        NAME=Vendor
        BODY=0190
        P=1000,1000,0

        [WORLDITEM]
        SERIAL=040000201
        ID=01B76
        CONT=01000
        LAYER=2

        [WORLDITEM]
        SERIAL=040000202
        ID=0A25
        CONT=01000
        LAYER=2

        """;

    [Fact]
    public void TwoItemsSavedOnTheSameHand_BothStayWorn_ThroughASaveCycle()
    {
        var world = TestHarness.CreateWorld();
        NewLoader().Load(world, WriteSave(TwoInOneHand));
        AssertBothWorn(world);

        // The garbage pass that heals "equipped but not on the layer" leaves it be.
        world.GarbageCollection();
        AssertBothWorn(world);

        string dir = NewDir();
        Save(world, dir);
        var again = TestHarness.CreateWorld();
        NewLoader().Load(again, dir);
        AssertBothWorn(again);
    }

    [Fact]
    public void TwoItemsResolvedToTheSameHandFromTiledata_BothStayWorn()
    {
        // The 0.56T form: no LAYER line, the slot comes from the art's tiledata.
        string body = TwoInOneHand.Replace("\r\n", "\n").Replace("LAYER=2\n", "");
        Assert.DoesNotContain("LAYER", body);
        var world = TestHarness.CreateWorld();
        var loader = NewLoader();
        loader.ResolveEquipLayerFromTile = _ => 2;
        loader.Load(world, WriteSave(body));
        AssertBothWorn(world);
    }

    private static void AssertBothWorn(GameWorld world)
    {
        var ch = world.FindChar(new Serial(0x1000))!;
        var shield = world.FindItem(new Serial(0x40000201))!;
        var lantern = world.FindItem(new Serial(0x40000202))!;
        var inHand = ch.GetEquippedItem(Layer.TwoHanded);
        Assert.NotNull(inHand);
        var other = ReferenceEquals(inHand, shield) ? lantern : shield;
        Assert.True(other.IsEquipped);
        Assert.Equal(Layer.TwoHanded, other.EquipLayer);
        Assert.Equal(ch.Uid, other.ContainedIn);
        Assert.Contains(other, ch.Memories);
        Assert.True(CharacterMemoryState.IsStackedWorn(other));
        Assert.False(shield.IsOnGround);
        Assert.False(lantern.IsOnGround);
    }

    // ------------------------------------------------------------ offline sleep

    private (GameWorld World, Character Player, Item Pack) OfflinePlayerWithPack()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        ch.Equip(pack, Layer.Pack);
        return (world, ch, pack);
    }

    [Fact]
    public void WhatAnOfflinePlayerCarries_Sleeps_AndRunsWhenTheyLogIn()
    {
        var (world, ch, pack) = OfflinePlayerWithPack();
        var rotting = world.CreateItem();
        rotting.BaseId = 0x0EED;
        rotting.SetAttr(ObjAttributes.Decay);
        Assert.True(pack.TryAddItem(rotting));
        rotting.SetTimeout(Environment.TickCount64 - 1);   // already due
        long deadline = rotting.Timeout;

        world.OnTick();
        Assert.False(rotting.IsDeleted);
        Assert.True(rotting.IsSleeping);
        Assert.Equal(deadline, rotting.Timeout);            // the deadline is kept
        Assert.Equal(1, world.OfflineSleeperCount);

        // A script re-arming it while the player is away does not wake it.
        rotting.SetTimeout(Environment.TickCount64 - 1);
        world.OnTick();
        Assert.False(rotting.IsDeleted);

        ch.IsOnline = true;
        world.AddOnlinePlayer(ch);                          // CChar::_GoAwake
        Assert.False(rotting.IsSleeping);
        world.OnTick();                                     // overdue: runs now
        Assert.True(rotting.IsDeleted);
        Assert.Equal(0, world.OfflineSleeperCount);
    }

    [Fact]
    public void AWornTimer_OfAnOfflinePlayer_Sleeps_LikeTheBackpack()
    {
        var (world, ch, _) = OfflinePlayerWithPack();
        var worn = world.CreateItem();
        worn.BaseId = 0x1F03;
        worn.SetAttr(ObjAttributes.Decay);
        ch.Equip(worn, Layer.Robe);
        worn.SetTimeout(Environment.TickCount64 - 1);

        world.OnTick();
        Assert.False(worn.IsDeleted);
        Assert.True(worn.IsSleeping);
    }

    [Fact]
    public void ALingeringPlayer_IsStillInTheWorld_AndTheirItemsTick()
    {
        var (world, ch, pack) = OfflinePlayerWithPack();
        ch.SetTag("CLIENT_LINGER_UNTIL",
            (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000).ToString());
        var rotting = world.CreateItem();
        rotting.BaseId = 0x0EED;
        rotting.SetAttr(ObjAttributes.Decay);
        Assert.True(pack.TryAddItem(rotting));
        rotting.SetTimeout(Environment.TickCount64 - 1);

        world.OnTick();
        Assert.True(rotting.IsDeleted);
    }

    [Fact]
    public void AnNpcsItems_AreNotPutToSleep()
    {
        var world = TestHarness.CreateWorld();
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(1000, 1000, 0, 0));
        var worn = world.CreateItem();
        worn.BaseId = 0x1F03;
        worn.SetAttr(ObjAttributes.Decay);
        npc.Equip(worn, Layer.Robe);
        worn.SetTimeout(Environment.TickCount64 - 1);

        world.OnTick();
        Assert.True(worn.IsDeleted);
    }

    [Fact]
    public void AnItemTakenOffAnOfflinePlayer_WakesOnItsOwn()
    {
        var (world, _, pack) = OfflinePlayerWithPack();
        var rotting = world.CreateItem();
        rotting.BaseId = 0x0EED;
        rotting.SetAttr(ObjAttributes.Decay);
        Assert.True(pack.TryAddItem(rotting));
        rotting.SetTimeout(Environment.TickCount64 - 1);
        world.OnTick();
        Assert.True(rotting.IsSleeping);

        // Staff moves it out of the offline pack into an NPC's; the next sleeper
        // audit wakes it and its overdue timer runs.
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(1002, 1000, 0, 0));
        var npcPack = world.CreateItem();
        npcPack.BaseId = 0x0E75;
        npc.Equip(npcPack, Layer.Pack);
        Assert.True(npcPack.TryAddItem(rotting));
        typeof(GameWorld).GetField("_lastOfflineSleeperAudit",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(world, long.MinValue / 2);
        world.OnTick();
        Assert.Equal(0, world.OfflineSleeperCount);
        Assert.True(rotting.IsDeleted);
    }

    /// <summary>A disconnected player does not tick at all (CChar::_TickableStateBase,
    /// CCharAct.cpp:5824): its memory clock stops. Once back it runs.</summary>
    [Fact]
    public void ADisconnectedPlayer_DoesNotTick_UntilTheyLogIn()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        var pos = new Point3D(1000, 1000, 0, 0);
        world.PlaceCharacter(ch, pos);
        Assert.True(ch.IsDisconnectedPlayer);
        // A memory whose subject is gone: its tick removes it.
        var mem = ch.Memory_CreateObj(Serial.Invalid, MemoryType.SawCrime);
        mem.SetTimeout(Environment.TickCount64 - 1);

        world.GetSector(pos)!.OnTick(Environment.TickCount64);
        Assert.Contains(mem, ch.Memories);

        ch.IsOnline = true;
        Assert.False(ch.IsDisconnectedPlayer);
        world.GetSector(pos)!.OnTick(Environment.TickCount64);
        Assert.DoesNotContain(mem, ch.Memories);
    }

    /// <summary>A Source-X save: an offline player whose worn script item came due
    /// while the save was being written (TIMERMS=0). It waits for its player instead
    /// of running at boot.</summary>
    [Fact]
    public void ASourceXSave_OfflinePlayersDueItem_WaitsForLogin()
    {
        string body = $"""
            [WORLDCHAR c_man]
            SERIAL=02000
            NAME=Sleeper
            BODY=0190
            ACCOUNT=sleeper
            P=1000,1000,0

            [WORLDITEM]
            SERIAL=040000301
            ID=0EED
            ATTR={Attr(ObjAttributes.Decay)}
            TIMERMS=0
            CONT=02000
            LAYER=30
            TYPE=t_eq_script

            """;
        var lf = LoggerFactory.Create(_ => { });
        var accounts = new AccountManager(lf);
        accounts.CreateAccount("sleeper", "pw");
        var world = TestHarness.CreateWorld();
        NewLoader().Load(world, WriteSave(body), accounts);

        var ch = world.FindChar(new Serial(0x2000))!;
        var item = world.FindItem(new Serial(0x40000301))!;
        Assert.True(ch.IsPlayer);
        Assert.True(item.Timeout > 0);

        world.OnTick();
        Assert.False(item.IsDeleted);
        Assert.True(item.IsSleeping);

        // A save taken now still carries the elapsed timer (TIMERMS=0), as upstream
        // writes a sleeping object's due timer.
        string dir = NewDir();
        Save(world, dir);
        string text = string.Join("\n", Directory.GetFiles(dir, "*.scp").Select(File.ReadAllText));
        Assert.Contains("TIMERMS=0", text);

        ch.IsOnline = true;
        world.AddOnlinePlayer(ch);
        world.OnTick();
        Assert.True(item.IsDeleted);
    }

    // ------------------------------------------------------------ decay at boot

    [Fact]
    public void ADecayingItemsSavedTimer_IsItsDecay_NoSecondDeadlineAtBoot()
    {
        string body = $"""
            [WORLDITEM]
            SERIAL=040000401
            ID=0EE3
            ATTR={Attr(ObjAttributes.Decay)}
            TIMERMS=7200000
            P=1000,1000,0

            [WORLDITEM]
            SERIAL=040000402
            ID=0EED
            ATTR={Attr(ObjAttributes.Decay)}
            P=1001,1000,0

            """;
        var world = TestHarness.CreateWorld();
        NewLoader().Load(world, WriteSave(body));
        var web = world.FindItem(new Serial(0x40000401))!;
        var bare = world.FindItem(new Serial(0x40000402))!;
        long timer = web.Timeout;

        world.GarbageCollection();

        // Two hours of saved timer stay two hours: no 30-minute default beside it.
        Assert.Equal(0, web.DecayTime);
        Assert.Equal(timer, web.Timeout);
        // A decaying item with no timer at all still gets one.
        Assert.True(bare.DecayTime > 0);
    }

    [Fact]
    public void ACorpsesSavedTimer_BecomesItsDecay()
    {
        string body = $"""
            [WORLDITEM]
            SERIAL=040000501
            ID=02006
            TYPE=t_corpse
            ATTR={Attr(ObjAttributes.Decay)}
            TIMERMS=5400000
            P=1000,1000,0

            """;
        var world = TestHarness.CreateWorld();
        NewLoader().Load(world, WriteSave(body));
        var corpse = world.FindItem(new Serial(0x40000501))!;
        long timer = corpse.Timeout;
        Assert.Equal(ItemType.Corpse, corpse.ItemType);

        world.GarbageCollection();

        Assert.Equal(timer, corpse.DecayTime);
        Assert.Equal(0, corpse.Timeout);
    }

    // ------------------------------------------------------------ vendor boxes

    [Fact]
    public void TheVendorStockBox_SavesWithItsStock()
    {
        var world = TestHarness.CreateWorld();
        var vendor = world.CreateCharacter();
        vendor.Name = "Shopkeeper";
        world.PlaceCharacter(vendor, new Point3D(1000, 1000, 0, 0));
        var box = world.CreateItem();
        box.BaseId = 0x408D;
        vendor.Equip(box, Layer.VendorStock);
        var stock = world.CreateItem();
        stock.BaseId = 0x0F0E;
        stock.Amount = 7;            // part-sold: a rebuild would not know
        Assert.True(box.TryAddItem(stock));

        string dir = NewDir();
        Save(world, dir);
        string text = string.Join("\n", Directory.GetFiles(dir, "*.scp").Select(File.ReadAllText));
        Assert.Contains("LAYER=26", text);

        var again = TestHarness.CreateWorld();
        NewLoader().Load(again, dir);
        var v2 = again.FindChar(vendor.Uid)!;
        var box2 = v2.GetEquippedItem(Layer.VendorStock);
        Assert.NotNull(box2);
        var stock2 = again.FindItem(stock.Uid)!;
        Assert.Equal(box2!.Uid, stock2.ContainedIn);
        Assert.Equal(7, stock2.Amount);
    }

    /// <summary>A Source-X save writes the vendor box as an ordinary worn container
    /// (LAYER=26, CItem::r_Write writes LAYER from LAYER_HORSE up).</summary>
    [Fact]
    public void ASourceXVendorBox_Loads()
    {
        const string body = """
            [WORLDCHAR c_h_vendor]
            SERIAL=03000
            NAME=Vendor
            BODY=0190
            P=1000,1000,0

            [WORLDITEM i_vendor_box]
            SERIAL=040000601
            ID=0408D
            LAYER=26
            CONT=03000

            [WORLDITEM i_bottle]
            SERIAL=040000602
            ID=0F0E
            AMOUNT=5
            P=50,60,0
            CONT=040000601

            """;
        var world = TestHarness.CreateWorld();
        NewLoader().Load(world, WriteSave(body));
        var vendor = world.FindChar(new Serial(0x3000))!;
        var box = vendor.GetEquippedItem(Layer.VendorStock);
        Assert.NotNull(box);
        Assert.Equal(1, box!.ContentCount);
    }

    // ------------------------------------------------------------ real saves

    /// <summary>The Sphere 56T custom-version save: a pack raised with
    /// TAG.OVERRIDE.MAXITEMS (264 items) and a bank filled to 279 come back whole.
    /// Before, everything past 255 went to the ground at its in-container
    /// coordinates and started to decay.</summary>
    [Fact]
    public void The56TSave_ContainersPastTheClientLimit_LoadWhole()
    {
        string? save = Find56TSaveHolding("SERIAL=040021ed4");
        if (Gate.Missing(null, "56T save", save == null)) return;

        var world = TestHarness.CreateWorld();
        world.InitMap(1, 7168, 4096);
        NewLoader().Load(world, save!);

        var pack = world.FindItem(new Serial(0x40021ED4));
        var bank = world.FindItem(new Serial(0x400025D3));
        Assert.NotNull(pack);
        Assert.NotNull(bank);
        Assert.Equal(264, pack!.ContentCount);
        Assert.Equal(279, bank!.ContentCount);
    }

    /// <summary>The Sphere 56T custom-version save under the untracked oldSphere/ tree
    /// whose character file holds <paramref name="marker"/>; null when there is none.</summary>
    private static string? Find56TSaveHolding(string marker)
    {
        string? root = TestRepo.Optional("oldSphere");
        if (root == null)
            return null;
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            string save = Path.Combine(dir, "save");
            string chars = Path.Combine(save, "spherechars.scp");
            if (!File.Exists(chars))
                continue;
            foreach (string line in File.ReadLines(chars))
                if (line.Equals(marker, StringComparison.OrdinalIgnoreCase))
                    return save;
        }
        return null;
    }

    /// <summary>A Source-X save (VERSION=110) survives a SphereNet save and reload
    /// with the same objects, the same containment and the same worn items.</summary>
    [Fact]
    public void ASourceXSave_RoundTripsWithEveryObjectInPlace()
    {
        string? save = TestRepo.Optional("oldSphere/sphere-x/save");
        if (Gate.Missing(null, "Source-X save",
                save == null || !File.Exists(Path.Combine(save, "sphereworld.scp")))) return;

        var world = TestHarness.CreateWorld();
        for (byte m = 1; m <= 5; m++) world.InitMap(m, 7168, 4096);
        var first = NewLoader().Load(world, save!);

        // The statics file (and what static containers hold) is written by
        // SAVESTATICS, not by a world save, so it is left out of the comparison.
        static bool UnderStatic(GameWorld w, Item i)
        {
            for (int depth = 0; depth < 32 && i != null; depth++)
            {
                if (i.IsAttr(ObjAttributes.Static)) return true;
                if (w.FindObject(i.ContainedIn) is not Item parent) return false;
                i = parent;
            }
            return false;
        }
        static Dictionary<uint, uint> Containment(GameWorld w) =>
            w.GetAllObjects().OfType<Item>()
                .Where(i => !i.IsDeleted && i.ContainedIn.IsValid && !UnderStatic(w, i))
                .ToDictionary(i => i.Uid.Value, i => i.ContainedIn.Value);
        var before = Containment(world);

        string dir = NewDir();
        Save(world, dir);
        var again = TestHarness.CreateWorld();
        for (byte m = 1; m <= 5; m++) again.InitMap(m, 7168, 4096);
        var second = NewLoader().Load(again, dir);

        // The statics file is written by SAVESTATICS, not by a world save.
        Assert.Equal(first.Chars, second.Chars);
        foreach (var item in world.GetAllObjects().OfType<Item>())
            if (!item.IsDeleted && !UnderStatic(world, item))
                Assert.True(again.FindItem(item.Uid) != null, $"0x{item.Uid.Value:X8} was not saved");
        var after = Containment(again);
        foreach (var (uid, cont) in before)
        {
            Assert.True(after.TryGetValue(uid, out uint c2), $"0x{uid:X8} lost its container");
            Assert.Equal(cont, c2);
        }
    }

    // ------------------------------------------------------------ bot accounts

    [Fact]
    public void TheBotAccountPrefix_StaysReservedForPlayers()
    {
        var accounts = new AccountManager(LoggerFactory.Create(_ => { })) { AutoCreateAccounts = true };
        Assert.Null(accounts.CreateAccount("spherenetBot0001", "x"));
        Assert.Null(accounts.Authenticate("spherenetBot0001", "x"));
    }

    [Fact]
    public void TheBotEngine_GetsItsAccountsThroughTheInternalDoor_AndTheyAreNotSaved()
    {
        var accounts = new AccountManager(LoggerFactory.Create(_ => { }));
        accounts.InternalAccountGate = name => name.Equals("spherenetBot0001", StringComparison.OrdinalIgnoreCase);

        var acc = accounts.Authenticate("spherenetBot0001", "secret");
        Assert.NotNull(acc);
        Assert.True(acc!.IsEngineInternal);
        // The password is the one it was made with.
        Assert.Null(accounts.Authenticate("spherenetBot0001", "guess"));
        Assert.NotNull(accounts.Authenticate("spherenetBot0001", "secret"));
        // Another bot-shaped name the gate does not vouch for is still refused.
        Assert.Null(accounts.Authenticate("spherenetBot0002", "secret"));

        accounts.CreateAccount("player", "pw");
        string dir = NewDir();
        SphereNet.Persistence.Accounts.AccountPersistence.Save(accounts, dir,
            SphereNet.Core.Configuration.SaveFormat.Text);
        string text = string.Join("\n", Directory.GetFiles(dir).Select(File.ReadAllText));
        Assert.Contains("[player]", text);
        Assert.DoesNotContain("spherenetBot0001", text);
    }
}
