using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Commands typed in game are logged the way upstream's Event_Command logs
/// them - "'name' commands 'line'=allowed" for everyone at or above COMMANDLOG - at a
/// level the file log keeps. They were Debug, so no staff command reached the log.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CommandAuditLogTests
{
    private sealed class Capture : ILoggerProvider
    {
        public readonly List<(LogLevel Level, string Text)> Entries = [];
        public ILogger CreateLogger(string categoryName) => new Sink(Entries);
        public void Dispose() { }

        private sealed class Sink(List<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter)
            {
                lock (entries) entries.Add((level, formatter(state, ex)));
            }
        }
    }

    private static (Game.Clients.GameClient Client, CommandHandler Commands, Capture Log) Setup(PrivLevel plevel)
    {
        var capture = new Capture();
        var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Name = "Staffer";
        ch.IsPlayer = true;
        ch.PrivLevel = plevel;
        world.PlaceCharacter(ch, new Point3D(500, 500, 0, 0));
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id: 7931);
        TestHarness.AttachCharacter(client, ch);
        client.SetEngines(commands: commands, skillHandlers: new SkillHandlers(world));
        return (client, commands, capture);
    }

    [Fact]
    public void AnAllowedCommandIsLoggedAsAllowed()
    {
        var (client, _, log) = Setup(PrivLevel.GM);
        client.TryHandleCommandSpeech(".where");
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning &&
            e.Text.Contains("[AUDIT]") && e.Text.Contains("'Staffer'") && e.Text.Contains("commands 'where'=1"));
    }

    [Fact]
    public void ARefusedCommandIsLoggedAsRefused()
    {
        var (client, _, log) = Setup(PrivLevel.Player);
        client.TryHandleCommandSpeech(".shutdown");
        Assert.Contains(log.Entries, e => e.Text.Contains("[AUDIT]") && e.Text.Contains("commands 'shutdown'=0"));
    }

    [Fact]
    public void CommandsBelowTheThresholdAreNotLogged()
    {
        var (client, commands, log) = Setup(PrivLevel.Player);
        commands.CommandLogPrivLevel = (int)PrivLevel.Counsel;
        client.TryHandleCommandSpeech(".where");
        Assert.DoesNotContain(log.Entries, e => e.Text.Contains("[AUDIT]"));
    }
}
