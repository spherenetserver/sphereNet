using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// House ownership, redeed, classic restore and guild links against Source-X
/// CItemMulti: SetOwner (CItemMulti.cpp:651) behind the OWNER key, @Redeed on the
/// multi (:1195), the classic record (r_Write :2558 / r_LoadVal :2998, custom
/// COMP/REVISION CItemMultiCustom.cpp:1704/1840) and the guild storage links
/// (SetGuild :711, CMultiStorage destructor :3520).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class HouseOwnershipRedeedRestoreParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spn_hor_{Guid.NewGuid():N}");

    public HouseOwnershipRedeedRestoreParityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Player(GameWorld world, short x = 50, short y = 50)
    {
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(x, y, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        var bank = world.CreateItem();
        bank.ItemType = ItemType.EqBankBox;
        bank.BaseId = 0x09AB;
        player.Equip(bank, Layer.BankBox);
        return player;
    }

    private static MultiRegistry Registry(int baseStorage = 0)
    {
        var registry = new MultiRegistry();
        var def = new MultiDef { Id = 0x0064, Name = "test house", BaseStorage = baseStorage };
        def.Components.Add(new MultiComponent { TileId = 0x0064, DeltaX = -2, DeltaY = -2, Visible = true });
        def.Components.Add(new MultiComponent { TileId = 0x0064, DeltaX = 2, DeltaY = 2, Visible = true });
        def.RecalcBounds();
        registry.Register(def);
        return registry;
    }

    private static (HousingEngine Engine, House House) PlacedHouse(GameWorld world, Character owner,
        MultiRegistry? registry = null)
    {
        var engine = new HousingEngine(world, registry ?? Registry()) { MaxHousesPerPlayer = -1, MaxHousesPerAccount = -1 };
        var house = engine.PlaceHouse(owner, 0x0064, new Point3D(200, 200, 0, 0), magic: true)!;
        Assert.NotNull(house);
        Item.ResolveHouse = uid => engine.GetHouse(uid);
        return (engine, house);
    }

    private static bool HasKey(Character ch, Serial multi) =>
        (ch.Backpack?.Contents ?? []).Concat(ch.GetEquippedItem(Layer.BankBox)?.Contents ?? [])
            .Any(i => i.ItemType == ItemType.Key && i.Link == multi);

    private static bool HasGuardMemory(Character ch, Serial multi) =>
        ch.Memory_FindObjTypes(multi, MemoryType.Guard) != null;

    // ================================================================
    // H10 - the OWNER key on a live house is SetOwner

    [Fact]
    public void WritingOwnerOnALiveHouseMovesTheHouseToTheNewOwner()
    {
        var world = NewWorld();
        var oldOwner = Player(world);
        var newOwner = Player(world, 60, 60);
        var (engine, house) = PlacedHouse(world, oldOwner);
        var multi = house.MultiItem;
        house.AddCoOwner(newOwner.Uid);
        Assert.True(HasKey(oldOwner, multi.Uid));
        Assert.True(HasGuardMemory(oldOwner, multi.Uid));

        var delMulti = new List<Serial>();
        var addMulti = new List<Serial>();
        HousingEngine.OnDelMulti = (ch, m) => delMulti.Add(ch.Uid);
        engine.OnAddMulti = (ch, m, priv) => addMulti.Add(ch.Uid);

        Assert.True(multi.TrySetProperty("OWNER", $"0{newOwner.Uid.Value:X}"));

        Assert.Equal(newOwner.Uid, house.Owner);
        Assert.Equal(HousePriv.Owner, house.GetPriv(newOwner.Uid));
        Assert.DoesNotContain(newOwner.Uid, house.CoOwners);     // RevokePrivs on the new owner
        Assert.Equal(HousePriv.None, house.GetPriv(oldOwner.Uid));
        Assert.False(HasKey(oldOwner, multi.Uid));                // RemoveKeys(old owner)
        Assert.False(HasGuardMemory(oldOwner, multi.Uid));
        Assert.True(HasGuardMemory(newOwner, multi.Uid));
        Assert.Equal(new[] { oldOwner.Uid }, delMulti);
        Assert.Equal(new[] { newOwner.Uid }, addMulti);
        // Capacity follows the registry: the house now counts for the new owner.
        Assert.Empty(engine.GetHousesByOwner(oldOwner.Uid));
        Assert.Single(engine.GetHousesByOwner(newOwner.Uid));
        // The getter reads the same live owner, and no stale tag was left behind.
        Assert.True(multi.TryGetProperty("OWNER", out string read));
        Assert.Equal(newOwner.Uid.Value, ObjBase.ParseHexOrDecUInt(read));
        Assert.False(multi.TryGetTag("OWNER", out _));
    }

    [Fact]
    public void ClearingTheOwnerLeavesAnOwnerlessHouseThatSurvivesTheRestore()
    {
        var world = NewWorld();
        var owner = Player(world);
        var (engine, house) = PlacedHouse(world, owner);

        Assert.True(house.MultiItem.TrySetProperty("OWNER", "0"));
        Assert.False(house.Owner.IsValid);
        Assert.False(HasKey(owner, house.MultiItem.Uid));

        engine.SerializeAllToTags();
        engine.DeserializeFromWorld();
        var back = engine.GetHouse(house.MultiItem.Uid);
        Assert.NotNull(back);
        Assert.False(back!.Owner.IsValid);
    }

    [Fact]
    public void TransferHouseUsesTheSameOperationAndStillMintsKeys()
    {
        var world = NewWorld();
        var owner = Player(world);
        var heir = Player(world, 60, 60);
        var (engine, house) = PlacedHouse(world, owner);
        var dels = new List<Serial>();
        HousingEngine.OnDelMulti = (ch, _) => dels.Add(ch.Uid);

        Assert.True(engine.TransferHouse(house, owner, heir));

        Assert.Equal(heir.Uid, house.Owner);
        Assert.True(HasKey(heir, house.MultiItem.Uid));
        Assert.False(HasKey(owner, house.MultiItem.Uid));
        Assert.Equal(new[] { owner.Uid }, dels);
    }

    // ================================================================
    // H11 - @Redeed on the multi, with the transfer contract

    private static Item LockedRug(GameWorld world, House house, Character owner)
    {
        var rug = world.CreateItem();
        rug.BaseId = 0x176F;
        world.PlaceItem(rug, new Point3D(200, 200, 0, 0));
        Assert.True(house.Lockdown(rug.Uid, owner.Uid));
        return rug;
    }

    [Fact]
    public void RedeedFiresOnTheMultiWithSrcDeedAndArguments()
    {
        var world = NewWorld();
        var owner = Player(world);
        var gm = Player(world, 70, 70);
        var (engine, house) = PlacedHouse(world, owner);
        var multi = house.MultiItem;

        Item? firedOn = null;
        TriggerArgs? seen = null;
        bool registeredDuringTrigger = false;
        House.OnRedeed = (m, args) =>
        {
            firedOn = m;
            seen = args;
            registeredDuringTrigger = engine.GetHouse(m.Uid) != null && !m.IsDeleted;
            return TriggerResult.Default;
        };

        var deed = engine.RedeedFromScript(multi.Uid, false, false, gm);

        Assert.NotNull(deed);
        Assert.Same(multi, firedOn);
        Assert.True(registeredDuringTrigger);       // the multi is still standing
        Assert.Same(gm, seen!.CharSrc);
        Assert.Same(deed, seen.O1);
        Assert.Equal(deed!.BaseId, (ushort)seen.N1);
        Assert.Equal(1, seen.N2);
        Assert.Equal(0, seen.N3);
        Assert.Contains(deed, owner.Backpack!.Contents);   // the owner gets the deed
        Assert.True(multi.IsDeleted);
    }

    [Fact]
    public void ReturnOneSuppressesTheDeedButNotTheTeardown()
    {
        var world = NewWorld();
        var owner = Player(world);
        var (engine, house) = PlacedHouse(world, owner);
        var multi = house.MultiItem;
        Item? offered = null;
        House.OnRedeed = (_, args) => { offered = args.O1 as Item; return TriggerResult.True; };

        var deed = engine.RedeedFromScript(multi.Uid);

        Assert.Null(deed);
        Assert.NotNull(offered);
        Assert.True(offered!.IsDeleted);
        Assert.True(multi.IsDeleted);
        Assert.Null(engine.GetHouse(multi.Uid));
        Assert.DoesNotContain(owner.Backpack!.Contents, i => i.ItemType == ItemType.Deed);
    }

    [Fact]
    public void ArgN2ZeroKeepsTheGoodsOutOfTheCrateAndUnlocksThemInPlace()
    {
        var world = NewWorld();
        var owner = Player(world);
        var (engine, house) = PlacedHouse(world, owner);
        var rug = LockedRug(world, house, owner);
        House.OnRedeed = (_, args) => { args.N2 = 0; return TriggerResult.Default; };

        Assert.NotNull(engine.RedeedFromScript(house.MultiItem.Uid));

        Assert.False(rug.IsDeleted);
        Assert.False(rug.ContainedIn.IsValid);                // not moved into a crate
        Assert.False(rug.IsAttr(ObjAttributes.LockedDown));   // UnlockAllItems
        Assert.False(rug.Link.IsValid);
        Assert.DoesNotContain(owner.GetEquippedItem(Layer.BankBox)!.Contents,
            i => i.BaseId == House.MovingCrateId);
    }

    [Fact]
    public void WithNoScriptOnRedeedNothingIsCratedAndLockdownsAreLetGoInPlace()
    {
        // Upstream: fTransferAll stays false unless @Redeed is used (CItemMulti.cpp:1230).
        var world = NewWorld();
        var owner = Player(world);
        var (engine, house) = PlacedHouse(world, owner);
        var rug = LockedRug(world, house, owner);
        var standing = house.GetMovingCrate(create: true)!;
        var goods = world.CreateItem();
        Assert.True(standing.TryAddItem(goods));

        var deed = engine.RedeedFromScript(house.MultiItem.Uid);

        Assert.Contains(deed!, owner.Backpack!.Contents);
        Assert.False(rug.ContainedIn.IsValid);
        Assert.False(rug.IsAttr(ObjAttributes.LockedDown));
        // The destructor only forgets the crate; it stays where it stood.
        Assert.False(standing.IsDeleted);
        Assert.False(standing.ContainedIn.IsValid);
        Assert.Contains(goods, standing.Contents);
        Assert.DoesNotContain(owner.GetEquippedItem(Layer.BankBox)!.Contents, i => i == standing);
    }

    [Fact]
    public void WithNoPlayerToRedeedToTheMultiStaysStanding()
    {
        // CItemMulti.cpp:1254 returns before the deed and before Delete().
        var world = NewWorld();
        var owner = Player(world);
        var (engine, house) = PlacedHouse(world, owner);
        Assert.True(engine.SetOwner(house, Serial.Invalid));

        Assert.Null(engine.RedeedFromScript(house.MultiItem.Uid));

        Assert.False(house.MultiItem.IsDeleted);
        Assert.NotNull(engine.GetHouse(house.MultiItem.Uid));
        Assert.Empty(house.Components);
    }

    [Fact]
    public void TheRedeedVerbWithNoSrcAndNoOwnerDeletesTheMulti()
    {
        // SHV_REDEED (CItemMulti.cpp:2403): no redeed target -> Delete().
        var world = NewWorld();
        var owner = Player(world);
        var (engine, house) = PlacedHouse(world, owner);
        Assert.True(engine.SetOwner(house, Serial.Invalid));
        Item.RedeedHouse = (uid, show, bank, src) => engine.RedeedFromScript(uid, show, bank, src);

        Assert.True(house.MultiItem.TryExecuteCommand("REDEED", "", null!));

        Assert.True(house.MultiItem.IsDeleted);
        Assert.Null(engine.GetHouse(house.MultiItem.Uid));
    }

    [Fact]
    public void ArgN3SendsTheCrateAndTheDeedToTheBank()
    {
        var world = NewWorld();
        var owner = Player(world);
        var (engine, house) = PlacedHouse(world, owner);
        var rug = LockedRug(world, house, owner);
        House.OnRedeed = (_, args) => { args.N3 = 1; return TriggerResult.Default; };

        var deed = engine.RedeedFromScript(house.MultiItem.Uid);

        var bank = owner.GetEquippedItem(Layer.BankBox)!;
        Assert.Contains(deed!, bank.Contents);
        var crate = Assert.Single(bank.Contents, i => i.BaseId == House.MovingCrateId);
        Assert.Contains(rug, crate.Contents);
    }

    [Fact]
    public void WithN3ZeroTheCrateStaysWhereTheHouseStood()
    {
        var world = NewWorld();
        var owner = Player(world);
        var (engine, house) = PlacedHouse(world, owner);
        var spot = house.MultiItem.Position;
        var rug = LockedRug(world, house, owner);
        House.OnRedeed = (_, args) => TriggerResult.Default;   // used, ARGN3 left 0

        var deed = engine.RedeedFromScript(house.MultiItem.Uid);

        Assert.Contains(deed!, owner.Backpack!.Contents);
        var crate = world.FindItem(rug.ContainedIn);
        Assert.NotNull(crate);
        Assert.False(crate!.ContainedIn.IsValid);
        Assert.Equal(spot.X, crate.X);
        Assert.Equal(spot.Y, crate.Y);
    }

    [Fact]
    public void TheRedeedVerbPassesItsFlagsAndSrcToTheEngine()
    {
        var world = NewWorld();
        var owner = Player(world);
        var (engine, house) = PlacedHouse(world, owner);
        Item.RedeedHouse = (uid, show, bank, src) => engine.RedeedFromScript(uid, show, bank, src);
        long bankArg = -1;
        House.OnRedeed = (_, args) => { bankArg = args.N3; return TriggerResult.Default; };

        Assert.True(house.MultiItem.TryExecuteCommand("REDEED", "0,1", null!));

        Assert.Equal(1, bankArg);
        Assert.Contains(owner.GetEquippedItem(Layer.BankBox)!.Contents, i => i.ItemType == ItemType.Deed);
        Assert.Null(engine.GetHouse(house.MultiItem.Uid));
    }

    // ================================================================
    // H13 - storage budgets follow Source-X: written only when set (r_Write
    // CItemMulti.cpp:2683-2696), a missing value comes from the definition.

    [Fact]
    public void StorageBudgetsRoundTripTheSourceXWay()
    {
        var world = NewWorld();
        var owner = Player(world);
        var (engine, house) = PlacedHouse(world, owner);
        Assert.True(house.MultiItem.TrySetProperty("BASESTORAGE", "650"));
        house.BaseVendors = 0;          // 0 is not written...

        SaveAndLoad(world, engine, out var engine2);
        string saved = string.Join("\n", Directory.GetFiles(_dir).Select(File.ReadAllText));
        Assert.Contains("BASESTORAGE=650", saved);
        Assert.DoesNotContain("BASEVENDORS=", saved);
        var back = engine2.GetHouse(house.MultiItem.Uid)!;
        Assert.Equal(650, back.BaseStorage);
        Assert.Equal(10, back.BaseVendors);   // ...so it comes back as the default
    }

    [Fact]
    public void AMissingStorageBudgetFallsBackToTheDefinition()
    {
        var world = NewWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x0064;
        multi.ItemType = ItemType.Multi;
        multi.SetTag("OWNER", "01234");
        world.PlaceItem(multi, new Point3D(100, 100, 0, 0));
        var engine = new HousingEngine(world, Registry(baseStorage: 777));

        engine.DeserializeFromWorld();

        Assert.Equal(777, engine.GetHouse(multi.Uid)!.BaseStorage);
    }

    /// <summary>A save written before ObjAttributes took Source-X's bit numbers marked a
    /// lockdown with 0x100000 (ATTR_INSURED) and a secure container with 0x40000
    /// (ATTR_IMBUED). Restoring the house list stamps the real bits and drops those
    /// strays; an item that already carries the real bit keeps its insured flag.</summary>
    [Fact]
    public void RestoringAHouseMigratesTheOldLockdownAndSecureBits()
    {
        var world = NewWorld();
        var oldLocked = world.CreateItem();
        oldLocked.Attributes = ObjAttributes.Insured;           // old LockedDown bit
        world.PlaceItem(oldLocked, new Point3D(100, 101, 0, 0));
        var oldSecure = world.CreateItem();
        oldSecure.ItemType = ItemType.Container;
        oldSecure.Attributes = ObjAttributes.Imbued;            // old Secure bit
        world.PlaceItem(oldSecure, new Point3D(100, 102, 0, 0));
        var insuredLocked = world.CreateItem();
        insuredLocked.Attributes = ObjAttributes.LockedDown | ObjAttributes.Insured;
        world.PlaceItem(insuredLocked, new Point3D(100, 103, 0, 0));

        var multi = world.CreateItem();
        multi.BaseId = 0x0064;
        multi.ItemType = ItemType.Multi;
        multi.SetTag("OWNER", "01234");
        multi.SetTag("LOCKITEM", $"0{oldLocked.Uid.Value:x},0{insuredLocked.Uid.Value:x}");
        multi.SetTag("SECURE", $"0{oldSecure.Uid.Value:x}");
        world.PlaceItem(multi, new Point3D(100, 100, 0, 0));

        new HousingEngine(world, Registry()).DeserializeFromWorld();

        Assert.Equal(ObjAttributes.LockedDown, oldLocked.Attributes);
        Assert.Equal(ObjAttributes.Secure, oldSecure.Attributes);
        Assert.Equal(ObjAttributes.LockedDown | ObjAttributes.Insured, insuredLocked.Attributes);
    }

    private GameWorld SaveAndLoad(GameWorld world, HousingEngine engine, out HousingEngine reloadedEngine,
        GuildManager? guilds = null, GuildManager? reloadedGuilds = null)
    {
        engine.SerializeAllToTags();
        guilds?.SerializeAllToTags(world);
        var lf = LoggerFactory.Create(_ => { });
        new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, _dir);

        // A fresh process has no live house to route the record's keys to.
        Item.ResolveHouse = null;
        var reloaded = NewWorld();
        new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, _dir);
        reloadedGuilds?.DeserializeFromWorld(reloaded);
        reloadedEngine = new HousingEngine(reloaded, Registry()) { Guilds = reloadedGuilds };
        reloadedEngine.DeserializeFromWorld();
        var captured = reloadedEngine;
        Item.ResolveHouse = uid => captured.GetHouse(uid);
        return reloaded;
    }

    // ================================================================
    // H12 - a classic Source-X house record restores into live state

    private const string ClassicSave = """
        [WORLDITEM 0edd]
        SERIAL=040000050
        TYPE=t_stone_guild
        P=150,150,0

        [WORLDITEM 0eed]
        SERIAL=040000060
        P=200,201,0

        [WORLDITEM 0e3c]
        SERIAL=040000061
        TYPE=t_container
        P=200,202,0

        [WORLDITEM 0bd2]
        SERIAL=040000062
        P=200,203,0

        [WORLDITEM 06a5]
        SERIAL=040000063
        TAG.FIXTURE=1073741888
        P=201,200,7

        [WORLDITEM 064]
        SERIAL=040000040
        ID=064
        TYPE=t_multi_custom
        P=200,200,0
        GUILD=040000050
        OWNER=01001
        HOUSETYPE=01
        ADDCOOWNER=01002
        ADDCOOWNER=01003
        ADDFRIEND=01004
        ADDACCESS=01005
        ADDBAN=01006
        ADDCOMP=040000062
        ADDCOMP=040000063
        SECURE=040000061
        LOCKITEM=040000060
        BASEVENDORS=12
        BASESTORAGE=600
        INCREASEDSTORAGE=10
        COMP=100,1,2,7,0
        COMP=1701,1,0,7,0
        COMP=1957,-2,3,27,1
        COMP=999,0,0,7,0
        REVISION=4
        """;

    /// <summary>Source-X drops a COMP piece whose id has no scripted ITEMDEF
    /// (AddItem -> FindItemBase, CItemMultiCustom.cpp:451), so the pieces the record
    /// keeps need definitions.</summary>
    private void LoadDesignItemDefs()
    {
        string script = Path.Combine(_dir, "defs.txt");
        File.WriteAllText(script, """
            [ITEMDEF 064]
            NAME=wall

            [ITEMDEF 06a5]
            NAME=door
            TYPE=t_door

            [ITEMDEF 07a5]
            NAME=stairs
            """);
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(
            LoggerFactory.Create(_ => { }).CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        resources.LoadResourceFile(script);
        new SphereNet.Game.Definitions.DefinitionLoader(resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        File.Delete(script);
    }

    [Fact]
    public void AClassicCustomHouseRecordRestoresAndSurvivesARoundTrip()
    {
        // The fixture item's tag names the multi 0x40000040 in decimal.
        Assert.Equal(1073741888u, 0x40000040u);
        LoadDesignItemDefs();
        File.WriteAllText(Path.Combine(_dir, "sphereworld.scp"), ClassicSave);
        var world = NewWorld();
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { })).Load(world, _dir);

        var guilds = new GuildManager();
        var stone = new Serial(0x40000050);
        guilds.CreateGuild(stone, "Classic Guild", Serial.Invalid);
        var engine = new HousingEngine(world, Registry()) { Guilds = guilds };
        engine.DeserializeFromWorld();

        var multiUid = new Serial(0x40000040);
        AssertClassicState(world, engine, guilds, multiUid);

        // SphereNet save -> load keeps the same state, and the design goes out in the
        // Source-X shape.
        foreach (var f in Directory.GetFiles(_dir)) File.Delete(f);
        var guilds2 = new GuildManager();
        var reloadedWorld = SaveAndLoad(world, engine, out var engine2, guilds, guilds2);
        string saved = string.Join("\n", Directory.GetFiles(_dir).Select(File.ReadAllText));
        Assert.Contains("COMP=100,1,2,7,0", saved);
        Assert.Contains("COMP=1957,-2,3,27,1", saved);
        Assert.Contains("REVISION=4", saved);
        Assert.DoesNotContain("TAG.DESIGN_", saved);
        Assert.DoesNotContain("COMP=999", saved);   // the unscripted piece was dropped on load
        // The house itself goes out as CItemMulti::r_Write writes it.
        foreach (var line in new[]
        {
            "GUILD=040000050", "OWNER=01001", "HOUSETYPE=01", "ADDCOOWNER=01002", "ADDCOOWNER=01003",
            "ADDFRIEND=01004", "ADDACCESS=01005", "ADDBAN=01006", "ADDCOMP=040000062",
            "SECURE=040000061", "LOCKITEM=040000060", "LOCKDOWNSPERCENT=50", "BASEVENDORS=12",
            "BASESTORAGE=600", "INCREASEDSTORAGE=10",
        })
            Assert.Contains(line, saved);
        Assert.DoesNotContain("HOUSE.", saved);

        AssertClassicState(reloadedWorld, engine2, guilds2, multiUid);
    }

    private static void AssertClassicState(GameWorld world, HousingEngine engine, GuildManager guilds, Serial multiUid)
    {
        var house = engine.GetHouse(multiUid);
        Assert.NotNull(house);
        Assert.Equal(new Serial(0x1001), house!.Owner);
        Assert.Equal(HouseType.Public, house.Type);
        Assert.Equal(new[] { new Serial(0x1002), new Serial(0x1003) }, house.CoOwners.OrderBy(s => s.Value));
        Assert.Contains(new Serial(0x1004), house.Friends);
        Assert.Contains(new Serial(0x1005), house.AccessList);
        Assert.Contains(new Serial(0x1006), house.Bans);
        Assert.Equal(600, house.BaseStorage);
        Assert.Equal(12, house.BaseVendors);
        Assert.Equal(10, house.IncreasedStorage);
        Assert.True(house.IsLockedDown(new Serial(0x40000060)));
        Assert.True(world.FindItem(new Serial(0x40000060))!.IsAttr(ObjAttributes.LockedDown));
        Assert.True(house.IsSecured(new Serial(0x40000061)));
        Assert.Contains(new Serial(0x40000062), house.Components);
        Assert.Contains(new Serial(0x40000063), house.Components);
        Assert.Equal(new Serial(0x40000050), house.GuildStone);
        Assert.Contains(multiUid, guilds.GetGuild(new Serial(0x40000050))!.Houses);

        var multi = world.FindItem(multiUid)!;
        Assert.Equal(ItemType.MultiCustom, multi.ItemType);
        var design = HouseDesign.LoadFromTags(multi);
        Assert.Equal(4u, design.Revision);
        Assert.Equal(3, design.Tiles.Count);
        Assert.Equal((ushort)100, design.Tiles[0].TileId);
        Assert.Equal((sbyte)-2, design.Tiles[2].X);
        Assert.Equal((sbyte)27, design.Tiles[2].Z);
        Assert.Equal((ushort)1, design.Tiles[2].StairId);
        // The classic fixture component is adopted as a commit fixture.
        Assert.True(multi.TryGetTag("COMMIT_FIXTURES", out string? fixtures));
        Assert.Equal(new Serial(0x40000063).Value.ToString(), fixtures);
        Assert.False(multi.TryGetTag("SAVE.ADDCOOWNER", out _));
        Assert.False(multi.TryGetTag("SAVE.COMP", out _));
    }

    [Fact]
    public void ALegacySphereNetDesignTagRecordStillLoads()
    {
        File.WriteAllText(Path.Combine(_dir, "sphereworld.scp"), """
            [WORLDITEM 064]
            SERIAL=040000070
            TYPE=t_multi_custom
            P=100,100,0
            TAG.HOUSE.OWNER=01001
            TAG.DESIGN_0=0x64,1,2,7,0
            TAG.DESIGN_1=0x6A5,1,0,7,0
            TAG.DESIGN_REVISION=3
            """);
        var world = NewWorld();
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { })).Load(world, _dir);
        var multi = world.FindItem(new Serial(0x40000070))!;
        var design = HouseDesign.LoadFromTags(multi);
        Assert.Equal(3u, design.Revision);
        Assert.Equal(2, design.Tiles.Count);
        Assert.Equal((ushort)0x6A5, design.Tiles[1].TileId);
    }

    // ================================================================
    // G04 - guild <-> structure links are cleaned from both ends

    private static (GuildManager Guilds, GuildDef Guild, Item Stone) Guild(GameWorld world)
    {
        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, new Point3D(150, 150, 0, 0));
        var guilds = new GuildManager();
        var guild = guilds.CreateGuild(stone.Uid, "Test Guild", Serial.Invalid);
        return (guilds, guild, stone);
    }

    [Fact]
    public void TheGuildKeyLinksAHouseFromBothEnds()
    {
        var world = NewWorld();
        var owner = Player(world);
        var (guilds, guild, stone) = Guild(world);
        var (engine, house) = PlacedHouse(world, owner);
        engine.Guilds = guilds;

        Assert.True(house.MultiItem.TrySetProperty("GUILD", $"0{stone.Uid.Value:X}"));
        Assert.Equal(stone.Uid, house.GuildStone);
        Assert.Equal(1, guild.HouseCount);

        Assert.True(house.MultiItem.TrySetProperty("GUILD", "0"));
        Assert.False(house.GuildStone.IsValid);
        Assert.Equal(0, guild.HouseCount);
    }

    [Fact]
    public void DeletingTheMultiTakesItOffTheGuildsList()
    {
        var world = NewWorld();
        var owner = Player(world);
        var (guilds, guild, stone) = Guild(world);
        var (engine, house) = PlacedHouse(world, owner);
        engine.Guilds = guilds;
        Assert.True(engine.SetGuild(house, stone.Uid));

        world.RemoveItem(house.MultiItem);

        Assert.Equal(0, guild.HouseCount);
    }

    [Fact]
    public void RedeedAndCollapseTakeItOffTheGuildsList()
    {
        var world = NewWorld();
        var owner = Player(world);
        var (guilds, guild, stone) = Guild(world);
        var (engine, house) = PlacedHouse(world, owner);
        engine.Guilds = guilds;
        Assert.True(engine.SetGuild(house, stone.Uid));
        Assert.NotNull(engine.RedeedFromScript(house.MultiItem.Uid));
        Assert.Equal(0, guild.HouseCount);

        var second = engine.PlaceHouse(owner, 0x0064, new Point3D(300, 300, 0, 0), magic: true)!;
        Assert.True(engine.SetGuild(second, stone.Uid));
        engine.DecayStageIntervalMs = 1;
        second.LastRefreshTick = Environment.TickCount64 - 100;
        Assert.Contains(second, engine.OnTickDecay());
        Assert.Equal(0, guild.HouseCount);
    }

    [Fact]
    public void AShipsGuildKeyLinksItAndItsDeletionUnlinksIt()
    {
        var world = NewWorld();
        var (guilds, guild, stone) = Guild(world);
        var hull = world.CreateItem();
        hull.ItemType = ItemType.Ship;
        world.PlaceItem(hull, new Point3D(400, 400, 0, 0));
        // What a Source-X save carries on the ship (CItemMulti::r_Write).
        Assert.True(hull.TrySetProperty("GUILD", $"0{stone.Uid.Value:x}"));
        var ships = new SphereNet.Game.Ships.ShipEngine(world, new MultiRegistry(), null) { Guilds = guilds };
        ships.DeserializeFromWorld();

        var ship = ships.GetShip(hull.Uid)!;
        Assert.Equal(stone.Uid, ship.GuildStone);
        Assert.Contains(hull.Uid, guild.Ships);

        ships.SerializeAllToTags();
        Assert.True(hull.TryGetTag("GUILD", out string? written));
        Assert.Equal($"0{stone.Uid.Value:x}", written);

        world.RemoveItem(hull);
        Assert.Equal(0, guild.ShipCount);
    }

    [Fact]
    public void AHouseListedOnlyByTheStonesAddHouseStaysListedWhenDeleted()
    {
        // Source-X: the multi destructor runs SetGuild(0) for its OWN guild only; a
        // stone's ADDHOUSE lists the multi without linking it back.
        var world = NewWorld();
        var owner = Player(world);
        var (guilds, guild, _) = Guild(world);
        var (engine, house) = PlacedHouse(world, owner);
        engine.Guilds = guilds;
        Assert.True(guild.AddHouse(house.MultiItem.Uid));

        world.RemoveItem(house.MultiItem);

        Assert.Equal(1, guild.HouseCount);
    }

    [Fact]
    public void RemovingTheStoneOrDisbandingUnlinksItsHouses()
    {
        var world = NewWorld();
        var owner = Player(world);
        var (guilds, _, stone) = Guild(world);
        var (engine, house) = PlacedHouse(world, owner);
        engine.Guilds = guilds;
        Assert.True(engine.SetGuild(house, stone.Uid));

        guilds.RemoveGuild(stone.Uid, world);              // disband, stone survives
        Assert.False(house.GuildStone.IsValid);

        var (guilds2, _, stone2) = Guild(world);
        engine.Guilds = guilds2;
        Assert.True(engine.SetGuild(house, stone2.Uid));
        world.RemoveItem(stone2);                          // the stone itself is deleted
        Assert.False(house.GuildStone.IsValid);
    }
}
