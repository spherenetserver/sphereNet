using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
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
    public void NetworkManager_PartialCryptoInit_WaitsForMoreData()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var config = new CryptConfig();
        var keys = (List<CryptoClientKey>)typeof(CryptConfig)
            .GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(config)!;
        keys.Add(new CryptoClientKey(70000000, 0x11111111, 0x22222222, SphereNet.Core.Enums.EncryptionType.Login));

        var manager = new SphereNet.Network.Manager.NetworkManager(1, loggerFactory)
        {
            CryptConfig = config
        };
        var state = TestHarness.CreateActiveNetState(loggerFactory, 2);
        state.IsSeeded = true;

        state.InjectReceived(new byte[10]);

        var method = typeof(SphereNet.Network.Manager.NetworkManager).GetMethod(
            "ProcessInput",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(manager, [state]);

        Assert.False(state.IsClosing);
        Assert.Equal(0x80, state.PendingPacketOpcode);
        Assert.Equal(62, state.PendingPacketLength);
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
