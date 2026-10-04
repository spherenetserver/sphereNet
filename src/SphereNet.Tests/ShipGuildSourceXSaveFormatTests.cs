using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Ships and guild/town stones are saved in the record shape Source-X writes, so a
/// shard can run on saves Source-X itself produced and the other way round:
/// a ship is CItemMulti::r_Write (CItemMulti.cpp:2558) plus CItemShip::r_Write's
/// HATCH / PLANK (CItemShip.cpp:118), its movement state in MORE2 (m_itShip,
/// CItem.h:438); a stone is CItemStone::r_Write (CItemStone.cpp:124). The old
/// SHIP.* and GUILD.* tags still load.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ShipGuildSourceXSaveFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spn_sgf_{Guid.NewGuid():N}");

    public ShipGuildSourceXSaveFormatTests() => Directory.CreateDirectory(_dir);

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

    private GameWorld SaveAndReload(GameWorld world)
    {
        var lf = LoggerFactory.Create(_ => { });
        new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, _dir);
        Item.ResolveShip = null;
        Item.ResolveHouse = null;
        Item.ResolveGuild = null;
        var reloaded = NewWorld();
        new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, _dir);
        return reloaded;
    }

    private string Saved() => string.Join("\n", Directory.GetFiles(_dir).Select(File.ReadAllText));

    [Fact]
    public void AShipRoundTripsInTheSourceXShape()
    {
        var world = NewWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(90, 90, 0, 0));
        var banned = world.CreateCharacter();
        world.PlaceCharacter(banned, new Point3D(91, 90, 0, 0));

        var hull = world.CreateItem();
        hull.ItemType = ItemType.Ship;
        world.PlaceItem(hull, new Point3D(100, 100, 0, 0));
        var hold = world.CreateItem();
        hold.ItemType = ItemType.ShipHold;
        world.PlaceItem(hold, new Point3D(100, 101, 0, 0));
        var plank = world.CreateItem();
        plank.ItemType = ItemType.ShipPlank;
        world.PlaceItem(plank, new Point3D(101, 100, 0, 0));
        Assert.True(hull.TrySetProperty("OWNER", $"0{owner.Uid.Value:x}"));
        Assert.True(hull.TrySetProperty("HATCH", $"0{hold.Uid.Value:x}"));
        Assert.True(hull.TrySetProperty("PLANK", $"0{plank.Uid.Value:x}"));

        var ships = new ShipEngine(world, new MultiRegistry(), null);
        ships.DeserializeFromWorld();
        var ship = ships.GetShip(hull.Uid)!;
        Assert.Equal(owner.Uid, ship.Owner);
        ship.AddBan(banned.Uid);
        ship.Anchored = true;
        ship.DirFace = Direction.East;
        ship.DirMove = Direction.South;

        ships.SerializeAllToTags();
        var reloaded = SaveAndReload(world);

        string saved = Saved();
        foreach (var line in new[]
        {
            $"OWNER=0{owner.Uid.Value:x}", $"ADDBAN=0{banned.Uid.Value:x}",
            $"ADDCOMP=0{hold.Uid.Value:x}", $"ADDCOMP=0{plank.Uid.Value:x}",
            $"HATCH=0{hold.Uid.Value:x}", $"PLANK=0{plank.Uid.Value:x}",
            "LOCKDOWNSPERCENT=50", "BASEVENDORS=10", "BASESTORAGE=489",
        })
            Assert.Contains(line, saved);
        Assert.DoesNotContain("SHIP.", saved);
        // MORE2 = movement type | anchored << 8 | dirmove << 16 | dirface << 24.
        uint more2 = 0u | 1u << 8 | (uint)Direction.South << 16 | (uint)Direction.East << 24;
        Assert.Contains($"MORE2=0{more2:x}", saved);

        var ships2 = new ShipEngine(reloaded, new MultiRegistry(), null);
        ships2.DeserializeFromWorld();
        var back = ships2.GetShip(hull.Uid)!;
        Assert.Equal(owner.Uid, back.Owner);
        Assert.Contains(banned.Uid, back.Bans);
        Assert.True(back.Anchored);
        Assert.Equal(Direction.East, back.DirFace);
        Assert.Equal(Direction.South, back.DirMove);
        Assert.Equal(hold.Uid, back.GetHold(reloaded)!.Uid);
        Assert.Equal(1, back.GetPlankCount(reloaded));
    }

    [Fact]
    public void AnOldSphereNetShipRecordStillLoads()
    {
        var world = NewWorld();
        var owner = world.CreateCharacter();
        var hull = world.CreateItem();
        hull.ItemType = ItemType.Ship;
        world.PlaceItem(hull, new Point3D(100, 100, 0, 0));
        hull.SetTag("SHIP.OWNER", $"0{owner.Uid.Value:X}");
        hull.SetTag("SHIP.ANCHORED", "1");
        hull.SetTag("SHIP.DIRFACE", ((byte)Direction.West).ToString());
        hull.SetTag("SHIP.BANS", "0123");

        var ships = new ShipEngine(world, new MultiRegistry(), null);
        ships.DeserializeFromWorld();
        var ship = ships.GetShip(hull.Uid)!;
        Assert.Equal(owner.Uid, ship.Owner);
        Assert.True(ship.Anchored);
        Assert.Equal(Direction.West, ship.DirFace);
        Assert.Contains(new Serial(0x123), ship.Bans);

        ships.SerializeAllToTags();
        Assert.False(hull.TryGetTag("SHIP.OWNER", out _));
        Assert.True(hull.TryGetTag("OWNER", out _));
    }

    [Fact]
    public void AGuildStoneRoundTripsInTheSourceXShape()
    {
        var world = NewWorld();
        var master = world.CreateCharacter();
        master.IsPlayer = true;
        world.PlaceCharacter(master, new Point3D(80, 80, 0, 0));
        var member = world.CreateCharacter();
        member.IsPlayer = true;
        world.PlaceCharacter(member, new Point3D(81, 80, 0, 0));

        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, new Point3D(85, 85, 0, 0));
        var enemyStone = world.CreateItem();
        enemyStone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(enemyStone, new Point3D(86, 85, 0, 0));

        var guilds = new GuildManager();
        var guild = guilds.CreateGuild(stone.Uid, "Knights", master.Uid);
        guild.Abbreviation = "KN";
        guild.Align = GuildAlign.Order;
        guild.Charter = "Honour first";
        guild.WebUrl = "http://example.invalid";
        var recruit = guild.AddRecruit(member.Uid);
        recruit.Priv = GuildPriv.Member;
        recruit.Title = "Squire";
        recruit.LoyalTo = master.Uid;
        recruit.AccountGold = -25;
        var enemy = guilds.CreateGuild(enemyStone.Uid, "Raiders", Serial.Invalid);
        Assert.True(guilds.DeclareWar(stone.Uid, enemyStone.Uid));

        guilds.SerializeAllToTags(world);
        var reloaded = SaveAndReload(world);

        string saved = Saved();
        Assert.Contains("NAME=Knights", saved);
        Assert.Contains("ALIGN=1", saved);
        Assert.Contains("ABBREV=KN", saved);
        Assert.Contains("CHARTER0=Honour first", saved);
        Assert.Contains("WEBPAGE=http://example.invalid", saved);
        Assert.Contains($"MEMBER=0{member.Uid.Value:x},Squire,1,0{master.Uid.Value:x},", saved);
        Assert.Contains($",-25", saved);
        // The war is a MEMBER record of the other stone, priv 100, ours in the 6th field.
        Assert.Contains($"MEMBER=0{enemyStone.Uid.Value:x},,100,00,0,1,0", saved);
        Assert.DoesNotContain("GUILD.", saved);
        GC.KeepAlive(enemy);

        var guilds2 = new GuildManager();
        guilds2.DeserializeFromWorld(reloaded);
        var back = guilds2.GetGuild(stone.Uid)!;
        Assert.Equal("Knights", back.Name);
        Assert.Equal("KN", back.Abbreviation);
        Assert.Equal(GuildAlign.Order, back.Align);
        Assert.Equal("Honour first", back.Charter);
        Assert.Equal("http://example.invalid", back.WebUrl);
        Assert.Equal(GuildPriv.Master, back.FindMember(master.Uid)!.Priv);
        var squire = back.FindMember(member.Uid)!;
        Assert.Equal(GuildPriv.Member, squire.Priv);
        Assert.Equal("Squire", squire.Title);
        Assert.Equal(master.Uid, squire.LoyalTo);
        Assert.Equal(-25, squire.AccountGold);
        Assert.True(back.Relations[enemyStone.Uid].WeDeclaredWar);
        Assert.True(guilds2.GetGuild(enemyStone.Uid)!.Relations[stone.Uid].TheyDeclaredWar);
    }

    [Fact]
    public void AStonesHousesAreNotWrittenOnTheStoneButComeBackFromTheHouse()
    {
        // CMultiStorage::r_Write is empty (CItemMulti.cpp:3783): the link lives on
        // the multi as GUILD=, and rebuilds the stone's list when the house loads.
        var world = NewWorld();
        var master = world.CreateCharacter();
        master.IsPlayer = true;
        world.PlaceCharacter(master, new Point3D(80, 80, 0, 0));
        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, new Point3D(85, 85, 0, 0));
        var guilds = new GuildManager();
        guilds.CreateGuild(stone.Uid, "Builders", master.Uid);

        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        world.PlaceItem(multi, new Point3D(150, 150, 0, 0));
        var housing = new HousingEngine(world, new MultiRegistry()) { Guilds = guilds };
        housing.DeserializeFromWorld();
        Assert.True(housing.SetGuild(housing.GetHouse(multi.Uid)!, stone.Uid));

        housing.SerializeAllToTags();
        guilds.SerializeAllToTags(world);
        var reloaded = SaveAndReload(world);
        string saved = Saved();
        Assert.Contains($"GUILD=0{stone.Uid.Value:x}", saved);
        Assert.DoesNotContain("HOUSES", saved);

        var guilds2 = new GuildManager();
        guilds2.DeserializeFromWorld(reloaded);
        var housing2 = new HousingEngine(reloaded, new MultiRegistry()) { Guilds = guilds2 };
        housing2.DeserializeFromWorld();
        Assert.Contains(multi.Uid, guilds2.GetGuild(stone.Uid)!.Houses);
    }

    [Fact]
    public void OldSphereNetGuildTagsStillLoad()
    {
        var world = NewWorld();
        var master = world.CreateCharacter();
        world.PlaceCharacter(master, new Point3D(80, 80, 0, 0));
        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, new Point3D(85, 85, 0, 0));
        stone.SetTag("GUILD.NAME", "Old Guild");
        stone.SetTag("GUILD.ABBREV", "OG");
        stone.SetTag("GUILD.MEMBERS", $"0{master.Uid.Value:X}:2:Boss:0:0:1");

        var guilds = new GuildManager();
        guilds.DeserializeFromWorld(world);
        var guild = guilds.GetGuild(stone.Uid)!;
        Assert.Equal("Old Guild", guild.Name);
        Assert.Equal("OG", guild.Abbreviation);
        Assert.Equal("Boss", guild.FindMember(master.Uid)!.Title);
    }
}
