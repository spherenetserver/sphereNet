using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogExternalPackSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public void ConfiguredPackDialogsOpenAndAcceptCancelInAnIsolatedWorld()
    {
        // Opt-in real-pack smoke test. No server host, database adapter or save
        // loader is wired; DB and filesystem host verbs cannot reach real data.
        string? root = Environment.GetEnvironmentVariable("SPHERENET_DIALOG_PACK");
        if (string.IsNullOrWhiteSpace(root)) return;
        string folder = Path.Combine(root, "dialogs");
        Assert.True(Directory.Exists(folder));
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        foreach (string path in Directory.EnumerateFiles(root, "*.scp", SearchOption.AllDirectories))
            stack.Resources.LoadResourceFile(path);
        stack.Interpreter.FunctionLookup = stack.Runner.HasFunction;
        stack.Interpreter.ServerPropertyResolver = key =>
            key.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) && stack.Resources.TryGetDefValue(key[4..], out string value)
                ? value : null;
        var names = Directory.EnumerateFiles(folder, "*.scp", SearchOption.AllDirectories)
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"(?im)^\s*\[DIALOG\s+([^\s\]]+)\s*\]")
                .Select(m => m.Groups[1].Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.NotEmpty(names);
        using var logs = LoggerFactory.Create(_ => { });
        foreach (string name in names)
        {
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19533);
            var player = world.CreateCharacter();
            player.PrivLevel = PrivLevel.Admin;
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog(name, 0), name);
            Assert.Contains(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] is 0xDD or 0xB0);
            uint id = client.Gumps.OpenScriptDialogs[name];
            client.HandleGumpResponse(player.Uid.Value, id, 0, [], []);
            output.WriteLine($"Opened and dispatched cancel: {name}");
        }
        output.WriteLine($"Dialog definitions checked: {names.Length}");
    }
}
