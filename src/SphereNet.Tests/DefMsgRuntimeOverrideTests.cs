using System.Collections.Generic;
using SphereNet.Game.Messages;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Where a DEFMSG override lives, and how long it lasts.
///
/// The parity matrix listed "persistent save/load of runtime DEFMSG changes" as an open
/// item. It is not one. Upstream keeps default messages in CServerConfig, loaded from
/// sphere_msgs.scp and overridable at runtime, and the world save never writes them:
/// CWorld only ever READS a message to broadcast it (CWorld.cpp:944, 1250, 1793). A
/// runtime change therefore lasts until the scripts are read again, and the scripts are
/// the record.
///
/// Persisting them would be a divergence, and a confusing one: the same key would then
/// have two sources of truth - the script pack an operator edits and a saved value they
/// cannot see - and the edit would silently lose. These tests pin the contract so it
/// stays a decision rather than becoming a bug report.
///
/// Serialized with the rest of the engine-static tests: the override table is
/// process-wide, and two other classes write to it. Running in parallel with them, a
/// clear from one test lands in the middle of another.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DefMsgRuntimeOverrideTests
{
    private readonly ITestOutputHelper _out;
    public DefMsgRuntimeOverrideTests(ITestOutputHelper output) => _out = output;

    private const string Key = "worldsave_complete";

    [Fact]
    public void ARuntimeOverrideWinsOverTheBuiltInDefault()
    {
        ServerMessages.ClearOverrides();
        try
        {
            string builtIn = ServerMessages.Get(Key);
            ServerMessages.SetOverride(Key, "the world is saved");

            _out.WriteLine($"built in: '{builtIn}' -> overridden: '{ServerMessages.Get(Key)}'");
            Assert.Equal("the world is saved", ServerMessages.Get(Key));
            Assert.NotEqual(builtIn, ServerMessages.Get(Key));
        }
        finally
        {
            ServerMessages.ClearOverrides();
        }
    }

    [Fact]
    public void AScriptOverrideIsWhatComesBackAfterAReload()
    {
        // The reload sequence the server runs: forget the overrides, then load what the
        // scripts say. A runtime change made in between is gone, on purpose - the
        // script pack is the record.
        ServerMessages.ClearOverrides();
        try
        {
            ServerMessages.LoadOverrides(new Dictionary<string, string> { [Key] = "from the pack" });
            Assert.Equal("from the pack", ServerMessages.Get(Key));

            ServerMessages.SetOverride(Key, "typed by a GM at runtime");
            Assert.Equal("typed by a GM at runtime", ServerMessages.Get(Key));

            // RESYNC / restart: clear, then re-read the pack.
            ServerMessages.ClearOverrides();
            ServerMessages.LoadOverrides(new Dictionary<string, string> { [Key] = "from the pack" });

            _out.WriteLine($"after a reload: '{ServerMessages.Get(Key)}'");
            Assert.Equal("from the pack", ServerMessages.Get(Key));
        }
        finally
        {
            ServerMessages.ClearOverrides();
        }
    }

    [Fact]
    public void ClearingOverridesFallsBackToTheBuiltInMessage()
    {
        ServerMessages.ClearOverrides();
        string builtIn = ServerMessages.Get(Key);

        ServerMessages.SetOverride(Key, "something else");
        ServerMessages.ClearOverrides();

        // With no script override either, the engine's own text is the floor - a key
        // never resolves to nothing just because somebody overrode it once.
        Assert.Equal(builtIn, ServerMessages.Get(Key));
        Assert.NotEqual(string.Empty, builtIn);
    }

    [Fact]
    public void AKeyIsKnownWhetherItComesFromTheDefaultsOrAnOverride()
    {
        ServerMessages.ClearOverrides();
        try
        {
            Assert.True(ServerMessages.HasKey(Key));
            Assert.False(ServerMessages.HasKey("a_key_nobody_defined"));

            ServerMessages.SetOverride("a_key_nobody_defined", "now it exists");
            Assert.True(ServerMessages.HasKey("a_key_nobody_defined"));
        }
        finally
        {
            ServerMessages.ClearOverrides();
        }
    }
}
