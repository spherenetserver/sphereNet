using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.State;

namespace SphereNet.Tests;

internal static class TestHarness
{
    public static ILoggerFactory CreateLoggerFactory() => LoggerFactory.Create(_ => { });

    public static GameWorld CreateWorld()
    {
        var world = new GameWorld(CreateLoggerFactory());
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    public static NetState CreateActiveNetState(ILoggerFactory loggerFactory, int id)
    {
        var state = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = id };
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        return state;
    }

    public static GameClient CreateClient(ILoggerFactory loggerFactory, GameWorld world, AccountManager accounts, int id)
    {
        var state = CreateActiveNetState(loggerFactory, id);
        return new GameClient(state, world, accounts, loggerFactory.CreateLogger<GameClient>());
    }

    public static Queue<PacketBuffer> GetQueuedPackets(NetState state) =>
        (Queue<PacketBuffer>)typeof(NetState)
            .GetField("_sendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(state)!;

    public static void AttachCharacter(GameClient client, Character ch, Account? account = null)
    {
        typeof(GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ch);
        if (account != null)
        {
            typeof(GameClient)
                .GetField("_account", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client, account);
        }
    }

    public static void SetPrivateField<T>(GameClient client, string fieldName, T value)
    {
        typeof(GameClient)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, value);
    }
}
