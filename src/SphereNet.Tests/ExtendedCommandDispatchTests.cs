using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

// 0xBF extended-command dispatch integration. Drives the production-registered
// PacketExtendedCommand router (NetworkManager.Packets.GetHandler(0xBF)) with raw
// sub-command payloads, through the same NetState.OnExtendedCommand wiring
// Program uses (buffer -> ReadBytes -> GameClient.HandleExtendedCommand), and
// asserts the per-subcommand engine effect. PacketManagerTests already covers
// the 0xBF parse/route to state; this closes the remaining gap: sub-command ->
// GameClient handler -> observable state change, plus the registry gate dropping
// unknown sub-commands.
public class ExtendedCommandDispatchTests
{
    private static (GameClient client, NetState state, List<ushort> dispatched) Wire(
        ILoggerFactory lf, GameWorld world, AccountManager accounts, Character? character)
    {
        var state = TestHarness.CreateActiveNetState(lf, 1);
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        if (character != null)
            TestHarness.AttachCharacter(client, character);

        // Mirror Program.OnExtendedCommand: drain the remaining buffer into a byte
        // array and dispatch into the client. Record the sub-command so a dropped
        // (unknown) command is observable as a non-dispatch.
        var dispatched = new List<ushort>();
        state.ExtendedCommandHandler = (_, subCmd, buffer) =>
        {
            dispatched.Add(subCmd);
            client.HandleExtendedCommand(subCmd, buffer.ReadBytes(buffer.Remaining));
        };
        return (client, state, dispatched);
    }

    // The 0xBF router's OnReceive is handed the payload positioned at the
    // sub-command word (opcode + length already stripped by the framing layer).
    private static byte[] ExtPayload(ushort subCmd, params byte[] data)
    {
        var b = new byte[2 + data.Length];
        b[0] = (byte)(subCmd >> 8);
        b[1] = (byte)subCmd;
        Buffer.BlockCopy(data, 0, b, 2, data.Length);
        return b;
    }

    private static (GameWorld world, AccountManager accounts, ILoggerFactory lf) CreateEnv()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return (world, new AccountManager(lf), lf);
    }

    private static Character NewChar(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.Name = "hero";
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void ScreenSize_0x05_SetsNetStateScreen()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = new NetworkManager(8, lf);
        var ch = NewChar(world);
        var (_, state, dispatched) = Wire(lf, world, accounts, ch);

        // HandleExtendedScreenSize: width = data[4..5], height = data[6..7].
        var router = nm.Packets.GetHandler(0xBF)!;
        router.OnReceive(new PacketBuffer(
            ExtPayload(0x0005, 0, 0, 0, 0, 0x07, 0x80, 0x04, 0x38)), state);

        Assert.Contains((ushort)0x0005, dispatched);
        Assert.Equal(1920, state.ScreenWidth);
        Assert.Equal(1080, state.ScreenHeight);
    }

    [Fact]
    public void Language_0x0B_SetsClientLanguage()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = new NetworkManager(8, lf);
        var (_, state, _) = Wire(lf, world, accounts, NewChar(world));

        var router = nm.Packets.GetHandler(0xBF)!;
        router.OnReceive(new PacketBuffer(
            ExtPayload(0x000B, (byte)'D', (byte)'E', (byte)'U')), state);

        Assert.Equal("DEU", state.ClientLanguage);
    }

    [Fact]
    public void StatLock_0x1A_SetsCharacterStatLock()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = new NetworkManager(8, lf);
        var ch = NewChar(world);
        var (_, state, _) = Wire(lf, world, accounts, ch);

        // data = [statIndex, lockState]; lock strength (index 0) to "locked" (2).
        var router = nm.Packets.GetHandler(0xBF)!;
        router.OnReceive(new PacketBuffer(ExtPayload(0x001A, 0x00, 0x02)), state);

        Assert.Equal(2, ch.GetStatLock(0));
    }

    [Fact]
    public void UnknownSubCommand_IsDroppedByRegistryGate()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = new NetworkManager(8, lf);
        var (_, state, dispatched) = Wire(lf, world, accounts, NewChar(world));

        // 0x07FF is not in ExtendedCommandRegistry: the router must drop it before
        // reaching OnExtendedCommand, so nothing is dispatched into the client.
        var router = nm.Packets.GetHandler(0xBF)!;
        router.OnReceive(new PacketBuffer(ExtPayload(0x07FF, 0xDE, 0xAD)), state);

        Assert.Empty(dispatched);
    }
}
