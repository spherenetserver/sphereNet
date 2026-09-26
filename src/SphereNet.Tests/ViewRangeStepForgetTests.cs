using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using Xunit;

namespace SphereNet.Tests;

/// <summary>The client drops a ground object the moment it passes out of its view
/// range and says nothing. A step out and straight back between two view ticks used to
/// leave the object known here and gone there; each step now forgets what it put out of
/// range, as Source-X decides per step from the point it left (addPlayerSee(ptOld)).</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ViewRangeStepForgetTests
{
    [Fact]
    public void AStepForgetsWhatItPutOutOfRangeAndKeepsTheRest()
    {
        var world = TestHarness.CreateWorld();
        var me = world.CreateCharacter();
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));

        var near = world.CreateItem();
        world.PlaceItem(near, new Point3D(118, 100, 0, 0));
        var far = world.CreateItem();
        world.PlaceItem(far, new Point3D(119, 100, 0, 0));
        var house = world.CreateItem();
        house.ItemType = ItemType.Multi;
        world.PlaceItem(house, new Point3D(130, 100, 0, 0));
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 125, 0, 0));

        var view = new ClientViewCache();
        foreach (var item in new[] { near, far, house })
        {
            view.KnownItems.Add(item.Uid.Value);
            view.LastKnownItemState[item.Uid.Value] = (item.X, item.Y, item.Z, item.DispIdFull, item.Hue, item.Amount, item.Direction);
        }
        view.KnownChars.Add(npc.Uid.Value);
        view.LastKnownPos[npc.Uid.Value] = (npc.X, npc.Y, npc.Z, 0, 0, 0, 0, 0);

        ClientViewUpdater.ForgetBeyondRange(view, world, me, 18);

        Assert.Contains(near.Uid.Value, view.KnownItems);
        Assert.DoesNotContain(far.Uid.Value, view.KnownItems);
        Assert.False(view.LastKnownItemState.ContainsKey(far.Uid.Value));
        Assert.Contains(house.Uid.Value, view.KnownItems);
        Assert.DoesNotContain(npc.Uid.Value, view.KnownChars);
    }
}
