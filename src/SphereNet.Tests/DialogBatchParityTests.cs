using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogBatchParityTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"dialog-batch-{Guid.NewGuid():N}.scp");
        private readonly ILoggerFactory _logs = LoggerFactory.Create(_ => { });
        public GameWorld World { get; }
        public GameClient Client { get; }
        public Character Player { get; }
        public Fixture(string script = "[DIALOG d_batch]\n0,0\nDTEXT 1 1 0 Test\n[DIALOG d_batch BUTTON]\nON=7\nTAG.HIT=1\n")
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            File.WriteAllText(_path, script);
            stack.Resources.LoadResourceFile(_path);
            World = TestHarness.CreateWorld();
            Client = TestHarness.CreateClient(_logs, World, new AccountManager(_logs), 19530);
            Player = World.CreateCharacter();
            TestHarness.AttachCharacter(Client, Player);
            Client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
        }
        public string Read(string key)
        {
            Assert.True(Client.TryResolveScriptVariable(key, Player, null, out string value));
            return value;
        }
        public void Dispose() { File.Delete(_path); _logs.Dispose(); }
    }

    [Fact]
    public void RepeatedDialogKeepsBothSubjectsAndCounts()
    {
        using var f = new Fixture();
        var first = f.World.CreateItem();
        var second = f.World.CreateItem();
        Assert.True(f.Client.TryShowScriptDialog("d_batch", 0, first));
        uint id = f.Client.Gumps.OpenScriptDialogs["d_batch"];
        Assert.True(f.Client.TryShowScriptDialog("D_BATCH", 0, second));
        Assert.Equal(id, f.Client.Gumps.OpenScriptDialogs["D_BATCH"]);
        Assert.Equal("1", f.Read("DIALOGLIST.COUNT"));
        Assert.Equal("2", f.Read("DIALOGLIST.0.COUNT"));
        // A response for a different subject cannot consume an open instance.
        f.Client.HandleGumpResponse(f.Player.Uid.Value, id, 7, [], []);
        Assert.Equal("2", f.Read("DIALOGLIST.0.COUNT"));
        f.Client.HandleGumpResponse(second.Uid.Value, id, 7, [], []);
        Assert.True(second.TryGetTag("HIT", out _));
        Assert.False(first.TryGetTag("HIT", out _));
        Assert.Equal("1", f.Read("DIALOGLIST.0.COUNT"));
        f.Client.HandleGumpResponse(first.Uid.Value, id, 7, [], []);
        Assert.True(first.TryGetTag("HIT", out _));
        Assert.Equal("0", f.Read("DIALOGLIST.COUNT"));
    }

    [Fact]
    public void CloseConsumesOneOfTwoInstances()
    {
        using var f = new Fixture();
        f.Client.TryShowScriptDialog("d_batch", 0);
        f.Client.TryShowScriptDialog("d_batch", 0);
        Assert.True(f.Client.CloseScriptDialog("D_BATCH", 7));
        Assert.Equal("1", f.Read("DIALOGLIST.0.COUNT"));
        Assert.True(f.Client.CloseScriptDialog("d_batch", 7));
        Assert.Equal("0", f.Read("DIALOGLIST.COUNT"));
    }

    [Theory]
    [InlineData(40_003_000u, false)]
    [InlineData(40_004_000u, true)]
    [InlineData(70_020_000u, true)]
    public void CloseUsesVersionDependentResponse(uint version, bool synthetic)
    {
        using var f = new Fixture();
        f.Client.NetState.ClientVersionNumber = version;
        f.Client.TryShowScriptDialog("d_batch", 0);
        uint id = f.Client.Gumps.OpenScriptDialogs["d_batch"];
        Assert.True(f.Client.CloseScriptDialog("d_batch", 7));
        Assert.Equal(synthetic, f.Player.TryGetTag("HIT", out _));
        Assert.Equal(!synthetic, f.Client.IsScriptDialogOpen("d_batch"));
        if (!synthetic)
        {
            f.Client.HandleGumpResponse(f.Player.Uid.Value, id, 7, [], []);
            Assert.True(f.Player.TryGetTag("HIT", out _));
            Assert.False(f.Client.IsScriptDialogOpen("d_batch"));
        }
    }

    [Theory]
    [InlineData(30_000_000u, 0xB0)]
    [InlineData(49_999_999u, 0xB0)]
    [InlineData(50_000_000u, 0xDD)]
    [InlineData(70_020_000u, 0xDD)]
    public void CompressionStartsAtVersionFive(uint version, byte opcode)
    {
        using var f = new Fixture();
        f.Client.NetState.ClientVersionNumber = version;
        f.Client.TryShowScriptDialog("d_batch", 0);
        Assert.Contains(TestHarness.GetQueuedPackets(f.Client.NetState), p => p.Span[0] == opcode);
    }

    [Theory]
    [InlineData(-1, 65535)]
    [InlineData(65536, 0)]
    [InlineData(65538, 2)]
    public void OnlyPageRemappingUsesUnsignedWord(int requested, int mapped)
    {
        using var f = new Fixture($"[DIALOG d_batch]\n0,0\nTAG.PAGE=<ARGN1>\nPAGE {mapped}\nDTEXT 1 1 0 Test\n");
        Assert.True(f.Client.TryShowScriptDialog("d_batch", requested));
        Assert.True(f.Player.TryGetTag("PAGE", out var actual));
        Assert.Equal(requested.ToString(), actual);
        byte[] packet = TestHarness.GetQueuedPackets(f.Client.NetState).Last(p => p.Span[0] == 0xDD).Span.ToArray();
        using var input = new MemoryStream(packet, 27, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(19)) - 4);
        using var zip = new ZLibStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(zip, Encoding.ASCII);
        string layout = reader.ReadToEnd();
        if (mapped > 0) Assert.Contains("{ page 1 }", layout);
        else Assert.DoesNotContain("{ page 1 }", layout);
    }

    [Fact]
    public void EmptyLayoutDoesNotOpenOrRegister()
    {
        using var f = new Fixture("[DIALOG d_batch]\n[DIALOG d_batch BUTTON]\nON=7\nTAG.HIT=1\n");
        Assert.False(f.Client.TryShowScriptDialog("d_batch", 0));
        Assert.False(f.Client.IsScriptDialogOpen("d_batch"));
        Assert.DoesNotContain(TestHarness.GetQueuedPackets(f.Client.NetState), p => p.Span[0] is 0xDD or 0xB0);
    }
}
