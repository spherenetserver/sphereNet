using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Scripting.Execution;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// f_onserver_broadcast (Source-X CWorldComm::Broadcast, CWorldComm.cpp:228-236):
/// every world broadcast runs it with ARGS = the message; RETURN 1 stops the
/// broadcast, otherwise the (possibly rewritten) ARGS is what gets sent.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ServerBroadcastHookTests
{
    private static readonly Type P = typeof(SphereNet.Server.Program);

    private static List<byte[]> Broadcast(string script, string message)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"bcast-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, script);
        var hooksField = P.GetField("_systemHooks", BindingFlags.NonPublic | BindingFlags.Static)!;
        var clients = (Dictionary<int, GameClient>)P.GetField("_clients", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var logField = P.GetField("_log", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldHooks = hooksField.GetValue(null);
        var oldLog = logField.GetValue(null);
        const int clientKey = 987_654;
        try
        {
            stack.Resources.LoadResourceFile(path);
            var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19811);
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, ch);
            clients[clientKey] = client;
            hooksField.SetValue(null, new ScriptSystemHooks(stack.Runner));
            logField.SetValue(null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            P.GetMethod("HandleServBroadcast", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [message]);
            return TestHarness.GetQueuedPackets(client.NetState)
                .Select(p => p.Span.ToArray()).Where(p => p[0] == 0xAE).ToList();
        }
        finally
        {
            clients.Remove(clientKey);
            hooksField.SetValue(null, oldHooks);
            logField.SetValue(null, oldLog);
            File.Delete(path);
        }
    }

    private static bool Says(List<byte[]> packets, string text) =>
        packets.Any(p => p.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes(text)) >= 0);

    [Fact]
    public void ReturnOneStopsTheBroadcast()
    {
        var sent = Broadcast("[FUNCTION f_onserver_broadcast]\nRETURN 1\n", "Server restarting");
        Assert.False(Says(sent, "Server restarting"));
    }

    [Fact]
    public void TheRewrittenArgsIsWhatGoesOut()
    {
        var sent = Broadcast("[FUNCTION f_onserver_broadcast]\nARGS=[News] <ARGS>\n", "Server restarting");
        Assert.True(Says(sent, "[News] Server restarting"));
    }

    [Fact]
    public void WithoutTheFunctionTheMessageGoesOutUnchanged()
    {
        var sent = Broadcast("[FUNCTION f_other]\nRETURN 1\n", "Server restarting");
        Assert.True(Says(sent, "Server restarting"));
    }
}
