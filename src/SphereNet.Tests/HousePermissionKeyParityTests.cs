using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The permission gate a housing pack is built on (port plan İŞ-33 / PLAN-501).
///
/// Scripts-X housing asks the multi directly, over and over:
///     if (&lt;isowner &lt;src&gt;&gt;) || (&lt;GetCoownerPos &lt;src&gt;&gt; >= 0) || (&lt;GetFriendPos &lt;src&gt;&gt; >= 0)
/// ISOWNER answers 1/0 and every GET...POS answers the index or -1 (Source-X
/// GetCoownerIndex and siblings, CItemMulti.cpp:811/888/962/1037/1648/1818/1887).
///
/// None of them existed here, so every such test in that pack read as nothing.
/// Measured in the pack: ISOWNER 45 sites, GETCOOWNERPOS 32, GETFRIENDPOS 29,
/// GETSECUREDITEMS 3, GETACCESSPOS 2, GETBANPOS 2, GETSECUREDCONTAINERPOS 1.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class HousePermissionKeyParityTests
{
    private static (GameWorld World, Item Multi, House House) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        multi.BaseId = 0x4064;
        world.PlaceItem(multi, new Point3D(100, 100, 0, 0));

        var house = new House(multi);
        Item.ResolveHouse = uid => uid == multi.Uid ? house : null;
        return (world, multi, house);
    }

    private static string Read(Item multi, string key)
    {
        Assert.True(multi.TryGetProperty(key, out string v), $"'{key}' went unanswered");
        return v;
    }

    private static Serial Player(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return ch.Uid;
    }

    // ---- ISOWNER --------------------------------------------------------

    [Fact]
    public void IsOwnerAnswersOneForTheOwnerAndZeroForAnyoneElse()
    {
        var (world, multi, house) = Setup();
        var owner = Player(world);
        var stranger = Player(world);
        house.Owner = owner;

        Assert.Equal("1", Read(multi, $"ISOWNER 0{owner.Value:X}"));
        Assert.Equal("0", Read(multi, $"ISOWNER 0{stranger.Value:X}"));
    }

    [Fact]
    public void IsOwnerOnAnOwnerlessHouseIsZero()
    {
        var (world, multi, _) = Setup();
        Assert.Equal("0", Read(multi, $"ISOWNER 0{Player(world).Value:X}"));
    }

    // ---- the GET...POS family -------------------------------------------

    [Fact]
    public void ACoOwnerHasAPositionAndAStrangerHasMinusOne()
    {
        var (world, multi, house) = Setup();
        var first = Player(world);
        var second = Player(world);
        var stranger = Player(world);
        Assert.True(house.AddCoOwner(first));
        Assert.True(house.AddCoOwner(second));

        Assert.Equal("0", Read(multi, $"GETCOOWNERPOS 0{first.Value:X}"));
        Assert.Equal("1", Read(multi, $"GETCOOWNERPOS 0{second.Value:X}"));
        // The packs test "> = 0", so a non-member MUST be negative, not 0.
        Assert.Equal("-1", Read(multi, $"GETCOOWNERPOS 0{stranger.Value:X}"));
    }

    [Fact]
    public void ThePositionAgreesWithWhatTheIndexedReadGivesBack()
    {
        var (world, multi, house) = Setup();
        var a = Player(world);
        var b = Player(world);
        house.AddFriend(a);
        house.AddFriend(b);

        string pos = Read(multi, $"GETFRIENDPOS 0{b.Value:X}");
        string atPos = Read(multi, $"HOUSE.FRIEND.{pos}");
        Assert.Equal(b.Value, System.Convert.ToUInt32(atPos, 16));
    }

    [Theory]
    [InlineData("GETFRIENDPOS")]
    [InlineData("GETACCESSPOS")]
    [InlineData("GETBANPOS")]
    [InlineData("GETLOCKEDITEMPOS")]
    [InlineData("GETSECUREDCONTAINERPOS")]
    [InlineData("GETCOMPPOS")]
    [InlineData("GETHOUSEVENDORPOS")]
    public void EveryListAnswersMinusOneForSomethingItDoesNotHold(string key)
    {
        var (world, multi, _) = Setup();
        Assert.Equal("-1", Read(multi, $"{key} 0{Player(world).Value:X}"));
    }

    [Fact]
    public void ABannedPlayerIsFoundInTheBanList()
    {
        var (world, multi, house) = Setup();
        var banned = Player(world);
        Assert.True(house.AddBan(banned));

        Assert.Equal("0", Read(multi, $"GETBANPOS 0{banned.Value:X}"));
    }

    // ---- the secured counts ----------------------------------------------

    [Fact]
    public void SecuredContainersCountsBoxesAndSecuredItemsCountsWhatIsInThem()
    {
        // Source-X keeps these apart (CItemMulti.cpp:1904/1919): the dialog spends
        // the storage budget with the item count, not the container count.
        var (world, multi, house) = Setup();
        var owner = Player(world);
        house.Owner = owner;

        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        chest.BaseId = 0x0E3C;
        world.PlaceItem(chest, new Point3D(100, 100, 0, 0));
        Assert.True(house.SecureContainer(chest.Uid, owner));

        for (int i = 0; i < 3; i++)
        {
            var goods = world.CreateItem();
            goods.BaseId = 0x0F51;
            Assert.True(chest.TryAddItem(goods));
        }

        Assert.Equal("1", Read(multi, "GETSECUREDCONTAINERS"));
        Assert.Equal("3", Read(multi, "GETSECUREDITEMS"));
    }

    [Fact]
    public void AHouseWithNothingSecuredCountsZeroBothWays()
    {
        var (_, multi, _) = Setup();
        Assert.Equal("0", Read(multi, "GETSECUREDCONTAINERS"));
        Assert.Equal("0", Read(multi, "GETSECUREDITEMS"));
    }

    // ---- the marker events -----------------------------------------------

    [Fact]
    public void ALockedDownItemCarriesTheMarkerEventAndLosesItOnRelease()
    {
        // Source-X puts "EVENTS +ei_house_lockdown" on the item (CItemMulti.cpp:1779).
        // It is not a script the pack defines - it is how the pack RECOGNISES the
        // item: house_functions.scp asks <isevent.ei_house_lockdown> and then calls
        // unlockitem on it. Without the marker nothing there can tell a locked item
        // from a loose one.
        var (world, _, house) = Setup();
        var owner = Player(world);
        house.Owner = owner;

        var rug = world.CreateItem();
        rug.BaseId = 0x176F;
        world.PlaceItem(rug, new Point3D(100, 100, 0, 0));

        Assert.True(house.Lockdown(rug.Uid, owner));
        Assert.True(rug.TryGetProperty($"ISEVENT.{House.LockdownEvent}", out string on));
        Assert.Equal("1", on);

        Assert.True(house.ReleaseLockdown(rug.Uid, owner));
        Assert.True(rug.TryGetProperty($"ISEVENT.{House.LockdownEvent}", out string off));
        Assert.Equal("0", off);
    }

    [Fact]
    public void ASecuredContainerCarriesItsOwnMarkerEvent()
    {
        var (world, _, house) = Setup();
        var owner = Player(world);
        house.Owner = owner;

        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        chest.BaseId = 0x0E3C;
        world.PlaceItem(chest, new Point3D(100, 100, 0, 0));

        Assert.True(house.SecureContainer(chest.Uid, owner));
        Assert.True(chest.TryGetProperty($"ISEVENT.{House.SecureEvent}", out string on));
        Assert.Equal("1", on);
        // The two markers are distinct: a secured chest is not "locked down".
        Assert.True(chest.TryGetProperty($"ISEVENT.{House.LockdownEvent}", out string other));
        Assert.Equal("0", other);

        Assert.True(house.ReleaseSecure(chest.Uid, owner));
        Assert.True(chest.TryGetProperty($"ISEVENT.{House.SecureEvent}", out string off));
        Assert.Equal("0", off);
    }

    // ---- scoping ---------------------------------------------------------

    [Fact]
    public void APlainItemAnswersNoneOfThem()
    {
        var (world, _, _) = Setup();
        var rock = world.CreateItem();
        rock.BaseId = 0x1363;

        Assert.False(rock.TryGetProperty($"ISOWNER 0{Player(world).Value:X}", out _));
        Assert.False(rock.TryGetProperty("GETSECUREDITEMS", out _));
    }
}
