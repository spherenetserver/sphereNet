using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Network.State;
using SphereNet.Scripting.Variables;

namespace SphereNet.Tests;

public class RuntimeSafetyHotspotsTests
{
    private static readonly MethodInfo s_processInput =
        typeof(SphereNet.Network.Manager.NetworkManager).GetMethod(
            "ProcessInput",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

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
        AddCryptKey(config, new CryptoClientKey(70000000, 0x11111111, 0x22222222, SphereNet.Core.Enums.EncryptionType.Login));

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
    public void NetworkManager_CryptoInit_AcceptsCoalescedClassicSeedAndLogin()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var manager = new SphereNet.Network.Manager.NetworkManager(1, loggerFactory)
        {
            CryptConfig = new CryptConfig(),
            UseCrypt = true,
            UseNoCrypt = true
        };
        var state = TestHarness.CreateActiveNetState(loggerFactory, 3);
        state.Seed = 0x12345678;
        state.IsSeeded = true;

        byte[] data = new byte[66];
        WriteUInt32(data, 0, state.Seed);
        data[4] = 0x80;
        WriteAsciiFixed(data, 5, 30, "acct");
        WriteAsciiFixed(data, 35, 30, "pw");
        state.InjectReceived(data);

        var method = typeof(SphereNet.Network.Manager.NetworkManager).GetMethod(
            "ProcessInput",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(manager, [state]);

        Assert.False(state.IsClosing);
        Assert.Equal(ConnectType.Login, state.ConnectionType);
        Assert.Equal(0, state.ReceivedData.Length);
    }

    [Fact]
    public void NetworkManager_CryptoInit_TriesLoginBeforeGameForLongNoCryptLoginBuffer()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var manager = new SphereNet.Network.Manager.NetworkManager(1, loggerFactory)
        {
            CryptConfig = new CryptConfig(),
            UseCrypt = true,
            UseNoCrypt = true
        };
        var state = TestHarness.CreateActiveNetState(loggerFactory, 4);
        state.Seed = 0x12345678;
        state.IsSeeded = true;

        byte[] data = new byte[65];
        data[0] = 0x80;
        WriteAsciiFixed(data, 1, 30, "acct");
        WriteAsciiFixed(data, 31, 30, "pw");
        state.InjectReceived(data);

        var method = typeof(SphereNet.Network.Manager.NetworkManager).GetMethod(
            "ProcessInput",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(manager, [state]);

        Assert.False(state.IsClosing);
        Assert.Equal(ConnectType.Login, state.ConnectionType);
        Assert.Equal(3, state.ReceivedData.Length);
    }

    [Fact]
    public void NetworkManager_CryptoInit_TriesByteSwappedClassicSeed()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        const uint wireSeed = 0x0100007F;
        const uint cryptSeed = 0x7F000001;
        const uint key1 = 0x11111111;
        const uint key2 = 0x22222222;
        var config = new CryptConfig();
        AddCryptKey(config, new CryptoClientKey(70000000, key1, key2, EncryptionType.Login));
        var manager = new SphereNet.Network.Manager.NetworkManager(1, loggerFactory)
        {
            CryptConfig = config,
            UseCrypt = true,
            UseNoCrypt = true
        };
        var state = TestHarness.CreateActiveNetState(loggerFactory, 5);
        state.Seed = wireSeed;
        state.IsSeeded = true;

        byte[] plain = new byte[62];
        plain[0] = 0x80;
        WriteAsciiFixed(plain, 1, 30, "acct");
        WriteAsciiFixed(plain, 31, 30, "pw");
        byte[] encrypted = (byte[])plain.Clone();
        new SphereNet.Network.Encryption.LoginEncryption(cryptSeed, key1, key2)
            .Decrypt(encrypted, 0, encrypted.Length);
        state.InjectReceived(encrypted);

        var method = typeof(SphereNet.Network.Manager.NetworkManager).GetMethod(
            "ProcessInput",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(manager, [state]);

        Assert.False(state.IsClosing);
        Assert.Equal(ConnectType.Login, state.ConnectionType);
        Assert.Equal(cryptSeed, state.Seed);
        Assert.Equal(0, state.ReceivedData.Length);
    }

    [Fact]
    public void NetworkManager_CryptoInit_AcceptsLegacyPaddedEncryptedLogin()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        const uint seed = 0x0100007F;
        var config = new CryptConfig();
        AddCryptKey(config, new CryptoClientKey(7002000, 0x02BF084BD, 0x0A0FD127F, EncryptionType.Twofish));
        var manager = new SphereNet.Network.Manager.NetworkManager(1, loggerFactory)
        {
            CryptConfig = config,
            UseCrypt = true,
            UseNoCrypt = true
        };
        var state = TestHarness.CreateActiveNetState(loggerFactory, 6);
        state.Seed = seed;
        state.IsSeeded = true;
        byte[] plain = new byte[62];
        plain[0] = 0x80;
        WriteAsciiFixed(plain, 1, 30, "mortal");
        WriteAsciiFixed(plain, 31, 30, "pw");
        for (int i = 8; i < 31; i++)
            plain[i] = (byte)(0x80 + i); // legacy clients may leave non-zero padding after null
        for (int i = 34; i < 61; i++)
            plain[i] = (byte)(0x40 + i);
        byte[] encrypted = (byte[])plain.Clone();
        new SphereNet.Network.Encryption.LoginEncryption(seed, 0x02BF084BD, 0x0A0FD127F)
            .Decrypt(encrypted, 0, encrypted.Length);
        state.InjectReceived(encrypted);

        var method = typeof(SphereNet.Network.Manager.NetworkManager).GetMethod(
            "ProcessInput",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(manager, [state]);

        Assert.False(state.IsClosing);
        Assert.Equal(ConnectType.Login, state.ConnectionType);
        Assert.Equal(0, state.ReceivedData.Length);
    }

    [Fact]
    public void NetworkManager_LegacyAssistVersion_DoesNotBlockFollowingMovement()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var manager = new SphereNet.Network.Manager.NetworkManager(1, loggerFactory)
        {
            UseCrypt = false,
            UseNoCrypt = true
        };
        var state = TestHarness.CreateActiveNetState(loggerFactory, 7);
        int moves = 0;
        state.GameLoginHandler = (_, _, _, _) => { };
        state.MoveRequestHandler = (_, dir, seq, _) =>
        {
            moves++;
            Assert.Equal((byte)1, dir);
            Assert.Equal((byte)2, seq);
        };

        Pump(manager, state, Concat(BuildClassicSeed(0x12345678), BuildGameLogin(0xAABBCCDD, "acct", "pw")));
        Pump(manager, state, Concat([0xBE, 0x00, 0xFB, 0x00, 0x02], BuildMoveRequest(1, 2)));

        Assert.False(state.IsClosing);
        Assert.Equal(0x00FB0002u, state.AssistVersion);
        Assert.Equal(1, moves);
        Assert.Equal(0, state.PendingPacketLength);
        Assert.Equal(0, state.ReceivedData.Length);
    }

    [Fact]
    public void NetworkManager_HardwareInfoLength_DoesNotSwallowFollowingMovement()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var manager = new SphereNet.Network.Manager.NetworkManager(1, loggerFactory)
        {
            UseCrypt = false,
            UseNoCrypt = true
        };
        var state = TestHarness.CreateActiveNetState(loggerFactory, 8);
        int moves = 0;
        state.GameLoginHandler = (_, _, _, _) => { };
        state.MoveRequestHandler = (_, dir, seq, _) =>
        {
            moves++;
            Assert.Equal((byte)3, dir);
            Assert.Equal((byte)4, seq);
        };

        byte[] hardwareInfo = new byte[0x010C];
        hardwareInfo[0] = 0xD9;
        hardwareInfo[1] = 0x02;

        Pump(manager, state, Concat(BuildClassicSeed(0x12345678), BuildGameLogin(0xAABBCCDD, "acct", "pw")));
        Pump(manager, state, Concat(hardwareInfo, BuildMoveRequest(3, 4)));

        Assert.False(state.IsClosing);
        Assert.Equal(1, moves);
        Assert.Equal(0, state.PendingPacketLength);
        Assert.Equal(0, state.ReceivedData.Length);
    }

    private static void AddCryptKey(CryptConfig config, CryptoClientKey key)
    {
        var keys = (List<CryptoClientKey>)typeof(CryptConfig)
            .GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(config)!;
        keys.Add(key);
    }

    private static void Pump(SphereNet.Network.Manager.NetworkManager manager, NetState state, byte[] wire)
    {
        state.InjectReceived(wire);
        s_processInput.Invoke(manager, [state]);
    }

    private static byte[] BuildClassicSeed(uint seed)
    {
        var buffer = new byte[4];
        WriteUInt32(buffer, 0, seed);
        return buffer;
    }

    private static byte[] BuildGameLogin(uint authId, string account, string password)
    {
        var buffer = new byte[65];
        buffer[0] = 0x91;
        WriteUInt32(buffer, 1, authId);
        WriteAsciiFixed(buffer, 5, 30, account);
        WriteAsciiFixed(buffer, 35, 30, password);
        return buffer;
    }

    private static byte[] BuildMoveRequest(byte direction, byte sequence)
    {
        var buffer = new byte[7];
        buffer[0] = 0x02;
        buffer[1] = direction;
        buffer[2] = sequence;
        return buffer;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteAsciiFixed(byte[] buffer, int offset, int length, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Buffer.BlockCopy(bytes, 0, buffer, offset, Math.Min(length - 1, bytes.Length));
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
