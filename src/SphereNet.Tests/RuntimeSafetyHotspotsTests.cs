using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Network.State;
using SphereNet.Scripting.Variables;

namespace SphereNet.Tests;

public class RuntimeSafetyHotspotsTests
{
    [Fact]
    public void NetState_PendingPacket_TracksAndClearsState()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var state = new NetState(loggerFactory.CreateLogger<NetState>());

        state.MarkPendingPacket(0xBF, 1024, 1234);

        Assert.Equal(0xBF, state.PendingPacketOpcode);
        Assert.Equal(1024, state.PendingPacketLength);
        Assert.Equal(1234, state.PendingPacketStartTick);

        state.ClearPendingPacket();

        Assert.Equal(0, state.PendingPacketLength);
        Assert.Equal(0, state.PendingPacketStartTick);
    }

    [Fact]
    public void NetworkManager_PartialPacketTimeout_MarksConnectionClosing()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var manager = new SphereNet.Network.Manager.NetworkManager(1, loggerFactory);
        var state = TestHarness.CreateActiveNetState(loggerFactory, 1);
        var method = typeof(SphereNet.Network.Manager.NetworkManager).GetMethod(
            "MarkOrDropPartialPacket",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        state.MarkPendingPacket(0xBF, 4096, Environment.TickCount64 - 20_000);
        method.Invoke(manager, [state, (byte)0xBF, 4096]);

        Assert.True(state.IsClosing);
    }

    [Fact]
    public void GameWorld_MoveCharacter_CrossSector_PositionAndMembershipMatch()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(63, 10, 0, 0));

        world.MoveCharacter(ch, new Point3D(64, 10, 0, 0));

        var oldSector = world.GetSector(0, 0, 0);
        var newSector = world.GetSector(0, 1, 0);
        Assert.DoesNotContain(ch, oldSector!.Characters);
        Assert.Contains(ch, newSector!.Characters);
        Assert.Same(newSector, world.GetSector(ch.Position));
    }

    [Fact]
    public void VarMap_RemoveByPrefix_TrimsAndMatchesCaseInsensitive()
    {
        var vars = new VarMap();
        vars.Set("Dialog.Admin.Index", "1");
        vars.Set("dialog.admin.Page", "2");
        vars.Set("Dialog.Other", "3");

        int removed = vars.RemoveByPrefix(" dialog.admin ");

        Assert.Equal(2, removed);
        Assert.False(vars.Has("Dialog.Admin.Index"));
        Assert.False(vars.Has("dialog.admin.Page"));
        Assert.True(vars.Has("Dialog.Other"));
    }

    [Fact]
    public void VarMap_RemoveByPrefix_EmptyPrefixClearsAll()
    {
        var vars = new VarMap();
        vars.Set("A", "1");
        vars.Set("B", "2");

        int removed = vars.RemoveByPrefix(" ");

        Assert.Equal(2, removed);
        Assert.Equal(0, vars.Count);
    }
}
