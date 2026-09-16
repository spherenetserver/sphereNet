using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// How high above the plank a boarder lands.
///
/// Upstream takes the plank's own point and raises it by ONE before teleporting:
/// `CPointMap pntTarg = pItem->GetTopPoint(); ++pntTarg.m_z; Spell_Teleport(...)`
/// (CChar::Use_Item, CCharUse.cpp:1822-1826). This engine used three, which put the
/// boarder two units above the deck they were meant to land on - and every step they
/// took afterwards was measured from that wrong height, which is what turns "walk
/// ashore" into a refused step on a shoreline whose own height is only a little
/// different.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ShipBoardingHeightTests
{
    private readonly ITestOutputHelper _out;
    public ShipBoardingHeightTests(ITestOutputHelper output) => _out = output;

    private static (SphereNet.Game.Clients.GameClient Client, Character Me, Item Plank) Stage(int port)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var lf = LoggerFactory.Create(_ => { });

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.IsOnline = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var plank = world.CreateItem();
        plank.BaseId = 0x3EB1;
        plank.ItemType = ItemType.ShipPlank;
        world.PlaceItem(plank, new Point3D(102, 100, 5, 0));
        return (client, me, plank);
    }

    [Fact]
    public void BoardingLandsOneAboveThePlank()
    {
        var (client, me, plank) = Stage(17410);

        client.HandleDoubleClick(plank.Uid.Value);

        _out.WriteLine($"plank z {plank.Z}, boarder z {me.Z}");
        Assert.Equal(plank.X, me.X);
        Assert.Equal(plank.Y, me.Y);
        Assert.Equal((sbyte)(plank.Z + 1), me.Z);
    }

    [Fact]
    public void BoardingRevealsTheBoarder()
    {
        // Upstream boards through Spell_Teleport, which ends in Reveal
        // (CCharUse.cpp:1827 -> CCharSpell.cpp:232).
        var (client, me, plank) = Stage(17411);
        me.SetStatFlag(StatFlag.Hidden);

        client.HandleDoubleClick(plank.Uid.Value);

        Assert.False(me.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void ADeepPlankStillLandsExactlyOneAbove()
    {
        // The offset is absolute, not a floor: a plank below the waterline boards the
        // same way.
        var (client, me, plank) = Stage(17412);
        plank.Position = new Point3D(plank.X, plank.Y, -8, plank.MapIndex);

        client.HandleDoubleClick(plank.Uid.Value);

        Assert.Equal((sbyte)(-7), me.Z);
    }
}
