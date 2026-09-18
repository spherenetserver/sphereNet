using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A weapon carries its own damage, and a piece of armour its own rating.
///
/// Upstream keeps them on the object (CObjBase::m_attackBase/m_attackRange and the
/// defense pair), copies the definition's values onto the instance when it is made
/// (CBase.cpp:416-419), lets a script change them there (CObjBase.cpp:1855), reads the
/// instance in combat (CCharFight.cpp:1220) and writes them with the item
/// (CItem.cpp:2468).
///
/// Here they were read live off the ITEMDEF and could not be written at all: DAM= on a
/// weapon was refused outright, so one sword could never differ from another, and
/// editing an ITEMDEF changed every copy already in the world.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemCombatRatingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_dam_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private GameWorld Load()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "d.scp");
        File.WriteAllLines(file, new[]
        {
            "[ITEMDEF 0f5e]", "DEFNAME=i_cr_sword", "NAME=sword", "TYPE=t_weapon_sword",
            "DAM=10,15",
            "[ITEMDEF 01410]", "DEFNAME=i_cr_plate", "NAME=plate", "TYPE=t_armor",
            "LAYER=13", "ARMOR=4,6",
            "[CHARDEF 0013]", "DEFNAME=c_cr_beast", "NAME=beast", "DAM=5,9",
        });

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Make(GameWorld world, int id, Point3D at)
    {
        var it = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(it, id);
        world.PlaceItem(it, at);
        return it;
    }

    private static string Read(ObjBase o, string key)
    {
        Assert.True(o.TryGetProperty(key, out string v), $"{key} did not answer");
        return v;
    }

    /// <summary>A new weapon starts on its definition's damage.</summary>
    [Fact]
    public void AWeaponStartsOnItsDefinitionsDamage()
    {
        var world = Load();
        var sword = Make(world, 0x0F5E, new Point3D(100, 100, 0, 0));

        Assert.Equal("10,15", Read(sword, "DAM"));
        Assert.Equal("10", Read(sword, "DAM.LO"));
        Assert.Equal("15", Read(sword, "DAM.HI"));
        Assert.Equal("1", Read(sword, "ISWEAPON"));
    }

    /// <summary>And a script can change THAT weapon, which is the whole point: the
    /// next sword off the same definition is untouched.</summary>
    [Fact]
    public void ChangingOneWeaponLeavesTheRestAlone()
    {
        var world = Load();
        var mine = Make(world, 0x0F5E, new Point3D(100, 100, 0, 0));
        var other = Make(world, 0x0F5E, new Point3D(101, 100, 0, 0));

        Assert.True(mine.TrySetProperty("DAM", "40,50"));

        Assert.Equal("40,50", Read(mine, "DAM"));
        Assert.Equal("10,15", Read(other, "DAM"));
        Assert.Equal("10,15", Read(Make(world, 0x0F5E, new Point3D(102, 100, 0, 0)), "DAM"));
    }

    /// <summary>The single-value and the .LO/.HI forms all land on the same pair.</summary>
    [Fact]
    public void TheHalvesCanBeSetOnTheirOwn()
    {
        var world = Load();
        var sword = Make(world, 0x0F5E, new Point3D(100, 100, 0, 0));

        Assert.True(sword.TrySetProperty("DAM.HI", "30"));
        Assert.Equal("10,30", Read(sword, "DAM"));

        Assert.True(sword.TrySetProperty("DAM.LO", "20"));
        Assert.Equal("20,30", Read(sword, "DAM"));

        Assert.True(sword.TrySetProperty("DAM", "7"));
        Assert.Equal("7", Read(sword, "DAM"));      // no spread reads bare
        Assert.Equal("7", Read(sword, "DAM.HI"));
    }

    /// <summary>What the weapon says is what it hits with.</summary>
    [Fact]
    public void CombatSwingsWithTheWeaponsOwnDamage()
    {
        var world = Load();
        var attacker = world.CreateCharacter();
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        var sword = Make(world, 0x0F5E, new Point3D(101, 100, 0, 0));

        var (defLo, defHi) = CombatEngine.CalcWeaponDamage(attacker, sword);
        Assert.True(defLo >= 10 && defHi >= 15, $"started at {defLo}-{defHi}");

        sword.TrySetProperty("DAM", "200,220");
        var (lo, hi) = CombatEngine.CalcWeaponDamage(attacker, sword);
        Assert.True(lo >= 200 && hi >= 220, $"after the change it swung {lo}-{hi}");
    }

    /// <summary>Armour the same way, and the rating the defence maths uses follows
    /// it.</summary>
    [Fact]
    public void ArmourCarriesItsOwnRating()
    {
        var world = Load();
        var plate = Make(world, 0x1410, new Point3D(100, 100, 0, 0));

        Assert.Equal("4,6", Read(plate, "ARMOR"));
        Assert.Equal(5, plate.GetArmorDefense());          // upstream averages the pair

        Assert.True(plate.TrySetProperty("ARMOR", "20,20"));
        Assert.Equal("20", Read(plate, "ARMOR"));
        Assert.Equal(20, plate.GetArmorDefense());
    }

    /// <summary>They are the item's, so they outlive a restart.</summary>
    [Fact]
    public void TheyOutliveASave()
    {
        var world = Load();
        var sword = Make(world, 0x0F5E, new Point3D(100, 100, 0, 0));
        sword.TrySetProperty("DAM", "40,50");
        var plate = Make(world, 0x1410, new Point3D(101, 100, 0, 0));
        plate.TrySetProperty("ARMOR", "20,22");

        string sdir = Path.Combine(_dir, "save");
        Directory.CreateDirectory(sdir);
        using var lf = LoggerFactory.Create(_ => { });
        new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, sdir);

        var reloaded = new GameWorld(lf);
        reloaded.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => reloaded;
        Item.ResolveWorld = () => reloaded;
        new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, sdir);

        var backSword = reloaded.FindItem(sword.Uid);
        Assert.NotNull(backSword);
        Assert.Equal("40,50", Read(backSword!, "DAM"));

        var backPlate = reloaded.FindItem(plate.Uid);
        Assert.NotNull(backPlate);
        Assert.Equal("20,22", Read(backPlate!, "ARMOR"));
    }

    /// <summary>A creature carries its own damage too. Upstream keeps the pair on the
    /// shared base (CObjBase::m_attackBase) and copies it off the CHARDEF at
    /// CChar.cpp:313; the write here used to be accepted and thrown away, so a script
    /// arming one NPC differently was answered "done" and changed nothing.</summary>
    [Fact]
    public void ACreatureCarriesItsOwnDamage()
    {
        var world = Load();

        var gem = world.CreateItem();
        world.PlaceItem(gem, new Point3D(150, 150, 0, 0));
        var spawn = new SphereNet.Game.Components.SpawnComponent(gem, world) { MaxCount = 1 };
        var beast = spawn.SpawnSpecific(0x13);
        Assert.NotNull(beast);

        Assert.Equal("5,9", Read(beast!, "DAM"));

        Assert.True(beast!.TrySetProperty("DAM", "60,80"));
        Assert.Equal("60,80", Read(beast, "DAM"));

        var (lo, hi) = CombatEngine.CalcWeaponDamage(beast, null);
        Assert.True(lo >= 60 && hi >= 80, $"it swung {lo}-{hi} bare-handed");
    }

    /// <summary>An item from a save that predates the stamp has none, and reads its
    /// definition - which is what the engine did for everything before this.</summary>
    [Fact]
    public void AnUnstampedItemStillReadsItsDefinition()
    {
        var world = Load();
        var bare = world.CreateItem();
        bare.BaseId = 0x0F5E;                    // no ApplyInstanceMetadata: no stamp
        world.PlaceItem(bare, new Point3D(100, 100, 0, 0));

        Assert.Null(bare.AttackBaseRaw);
        Assert.Equal("10,15", Read(bare, "DAM"));
    }
}
