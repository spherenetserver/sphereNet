using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Batch 3 house/ship placement: B4 — PlaceHouse/PlaceShip report a structured failure
/// reason (so the deed handler can show a specific message instead of one generic
/// "Cannot place here"); B13 — a customizable foundation is detected from the resolved
/// MULTIDEF type (t_multi_custom), not only a CUSTOMHOUSE deed tag.
/// </summary>
public sealed class SourceXPlacementResultTests
{
    private static MultiDef Geometry(ushort id, string type = "t_multi")
    {
        var def = new MultiDef { Id = id, MultiTypeName = type };
        def.Components.Add(new MultiComponent { TileId = 0x0001, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = true });
        def.RecalcBounds();
        return def;
    }

    [Fact]
    public void PlaceHouse_ReportsSpecificFailureReasons()
    {
        var world = TestHarness.CreateWorld();
        var registry = new MultiRegistry();
        registry.Register(Geometry(0x64));

        var engine = new HousingEngine(world, registry)
        {
            MaxHousesPerPlayer = 1,
            MaxHousesPerAccount = -1, // disabled → isolate the player limit
        };
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        // Unknown multi id → definition missing.
        Assert.Null(engine.PlaceHouse(owner, 0x0999, new Point3D(120, 120, 0, 0), out var missing));
        Assert.Equal(PlacementFailure.MultiDefinitionMissing, missing);

        // Off the edge of the map.
        Assert.Null(engine.PlaceHouse(owner, 0x64, new Point3D(30000, 30000, 0, 0), out var offMap));
        Assert.Equal(PlacementFailure.OutOfMap, offMap);

        // First placement succeeds; the second hits the per-player limit.
        Assert.NotNull(engine.PlaceHouse(owner, 0x64, new Point3D(120, 120, 0, 0), out var ok));
        Assert.Equal(PlacementFailure.None, ok);
        Assert.Null(engine.PlaceHouse(owner, 0x64, new Point3D(140, 140, 0, 0), out var limit));
        Assert.Equal(PlacementFailure.PlayerLimitReached, limit);
    }

    [Fact]
    public void IsCustomFoundation_DetectsFromMultiDefType()
    {
        var world = TestHarness.CreateWorld();
        var registry = new MultiRegistry();
        registry.Register(Geometry(0x1404, "t_multi_custom")); // foundation
        registry.Register(Geometry(0x64, "t_multi"));          // fixed house

        var engine = new HousingEngine(world, registry);

        Assert.True(engine.IsCustomFoundation(0x1404));
        Assert.False(engine.IsCustomFoundation(0x64));
        Assert.False(engine.IsCustomFoundation(0x0999)); // unknown id
    }
}
