using SphereNet.Game.Movement;
using SphereNet.MapData.Tiles;

namespace SphereNet.Tests;

/// <summary>
/// Locks in the land-tile movement barrier rule: only WATER (Impassable + Wet)
/// blocks a walking mover. Dry "impassable" mountain/slope terrain stays
/// walkable to match the client's own movement prediction — blocking it
/// desynced server and client and snapped the running player sideways near
/// stairs/hills (the rubber-band teleport).
/// </summary>
public class LandBlockRuleTests
{
    [Fact]
    public void ImpassableDryLand_DoesNotBlock()
    {
        // Mountain / slope terrain: impassable bit set but NOT wet. The client
        // walks these (predicts movement onto them), so the server must too,
        // or a rejection rubber-bands the running player.
        var land = new LandTileData { Flags = TileFlag.Impassable };
        Assert.False(WalkCheck.LandBlocks(land));
    }

    [Fact]
    public void Water_ImpassableAndWet_Blocks()
    {
        var land = new LandTileData { Flags = TileFlag.Impassable | TileFlag.Wet };
        Assert.True(WalkCheck.LandBlocks(land));
    }

    [Fact]
    public void PassableLand_DoesNotBlock()
    {
        var land = new LandTileData { Flags = TileFlag.None };
        Assert.False(WalkCheck.LandBlocks(land));
    }

    [Fact]
    public void WetButPassableLand_DoesNotBlock()
    {
        // Wet but walkable (e.g. shallow/marsh land without the Impassable bit).
        var land = new LandTileData { Flags = TileFlag.Wet };
        Assert.False(WalkCheck.LandBlocks(land));
    }
}
