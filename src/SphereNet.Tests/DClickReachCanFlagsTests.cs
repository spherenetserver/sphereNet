using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// The CAN flags that decide a reach are read.
///
/// Upstream's touch test consults four of them before refusing: the item's
/// CAN_I_DCIGNORELOS and CAN_I_DCIGNOREDIST, and the reacher's CAN_C_DCIGNORELOS and
/// CAN_C_DCIGNOREDIST (CanTouch, CCharStatus.cpp:1415-1430), plus CAN_I_FORCEDC which
/// skips the test outright (Cmd_Use_Item, CClientUse.cpp:31). All of them were parsed
/// into the flag enum and then never read.
///
/// The shipped pack asks for them: all twelve tillermen carry CAN=can_i_dcignorelos and
/// the archery butte carries CAN=can_i_dcignoredist. A ship's own hull stands between
/// its tiller and anyone on the shore, so double-clicking the tillerman from the dock -
/// which is how a ship is turned back into a deed, and has to be done from OFF the ship -
/// was refused every time, and reported as distance.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DClickReachCanFlagsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_dcr_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const ushort WallGraphic = 0x0080;

    private sealed record Bench(SphereNet.Game.World.GameWorld World,
                                SphereNet.Game.Clients.GameClient Client,
                                SphereNet.Game.Objects.Characters.Character Me,
                                ushort DefId);

    /// <summary>Load one ITEMDEF carrying the given lines and hand back a live client.</summary>
    /// <summary>The definitions live in static tables that a second LoadAll adds to
    /// rather than replaces, so each bench gets its own ITEMDEF id - loading a flagless
    /// definition over a flagged one left the flag in place and every calibration half
    /// stopped failing.</summary>
    private Bench Build(int port, ushort defId, params string[] defLines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, $"c{defId:x}.scp");
        var lines = new List<string> { $"[ITEMDEF 0{defId:x}]",
                                       $"DEFNAME=i_probe_tiller_{defId:x}",
                                       "TYPE=t_ship_tiller" };
        lines.AddRange(defLines);
        // A body whose CHARDEF carries the reacher-side flag, for the test that uses it.
        // Deliberately NOT the default human body: putting the flag on 0190 handed it to
        // every probe character and the calibration halves stopped failing.
        lines.AddRange(["", "[CHARDEF 0191]", "DEFNAME=c_probe_seer",
                        "CAN=04000"]);
        File.WriteAllLines(file, lines);

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        // A real tile behind the reach test: without map data nothing blocks a ray, so
        // the calibration half would pass for the wrong reason.
        var md = new SphereNet.MapData.MapDataManager("");
        md.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        md.SetSyntheticItemTile(WallGraphic, new SphereNet.MapData.Tiles.ItemTileData
        {
            Flags = SphereNet.MapData.Tiles.TileFlag.Wall |
                    SphereNet.MapData.Tiles.TileFlag.Impassable,
            Height = 20, Name = "wall"
        });
        var world = new SphereNet.Game.World.GameWorld(lf);
        world.InitMap(0, 512, 512);
        world.MapData = md;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.PrivLevel = PrivLevel.Player;
        me.Str = me.Dex = me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return new Bench(world, client, me, defId);
    }

    /// <summary>A wall between the reacher and the target, so line of sight is the thing
    /// that decides - the ship's hull, in the reported case.</summary>
    private static Item Behind(Bench b, int dx)
    {
        var wall = b.World.CreateItem();
        wall.BaseId = WallGraphic;
        b.World.PlaceItem(wall, new Point3D((short)(100 + dx / 2), 100, 0, 0));

        var target = b.World.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(target, b.DefId);
        b.World.PlaceItem(target, new Point3D((short)(100 + dx), 100, 0, 0));
        return target;
    }

    private bool CanReach(Bench b, Item target)
    {
        var m = typeof(SphereNet.Game.Clients.ClientItemUseHandler)
            .GetMethod("CanReachTargetItem", System.Reflection.BindingFlags.Instance |
                                             System.Reflection.BindingFlags.NonPublic)!;
        return (bool)m.Invoke(b.Client.ItemUse, [target])!;
    }

    /// <summary>The reported case: a wall in between, and the definition says to ignore
    /// line of sight.</summary>
    [Fact]
    public void AnItemThatIgnoresLineOfSightIsReachableThroughAWall()
    {
        var withFlag = Build(8931, 0x3E4A, "CAN=04000");
        Assert.True(CanReach(withFlag, Behind(withFlag, 3)));

        var without = Build(8932, 0x3E4B);
        Assert.False(CanReach(without, Behind(without, 3)),
            "calibration: without the flag the wall must still refuse it");
    }

    /// <summary>And the distance half, which is what the archery butte asks for.</summary>
    [Fact]
    public void AnItemThatIgnoresDistanceIsReachableFromAcrossTheRoom()
    {
        var withFlag = Build(8933, 0x3E4C, "CAN=08000");
        var far = withFlag.World.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(far, withFlag.DefId);
        withFlag.World.PlaceItem(far, new Point3D(106, 100, 0, 0));
        Assert.True(CanReach(withFlag, far));

        var without = Build(8934, 0x3E4D);
        var far2 = without.World.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(far2, without.DefId);
        without.World.PlaceItem(far2, new Point3D(106, 100, 0, 0));
        Assert.False(CanReach(without, far2),
            "calibration: without the flag six tiles must still be out of reach");
    }

    /// <summary>The reacher's own flag does the same, which is how a creature or a staff
    /// body is given the exemption rather than every item it touches.</summary>
    [Fact]
    public void TheReachersOwnFlagCountsToo()
    {
        var b = Build(8935, 0x3E4E);
        var target = Behind(b, 3);
        Assert.False(CanReach(b, target));

        // The same character, now on a body whose CHARDEF declares CAN_C_DCIGNORELOS.
        b.Me.CharDefIndex = 0x0191;
        Assert.Equal(CanFlags.C_DcIgnoreLOS, CharDefHelper.GetCanFlags(b.Me));
        Assert.True(CanReach(b, target));
    }
}
