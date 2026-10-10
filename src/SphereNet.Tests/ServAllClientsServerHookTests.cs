using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>SERV.ALLCLIENTS runs its line on every client's character with the caller's
/// pSrc, whatever that is (Source-X CServer.cpp:1867-1880). A server hook such as
/// f_onserver_save_finished has no SRC character, and the call used to stop there - so a
/// pack that announces its saves through "serv.allclients f_announce_save" stayed silent.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ServAllClientsServerHookTests : IDisposable
{
    private readonly Dictionary<FieldInfo, object?> _saved = new();

    private void SetServer(string name, object? value)
    {
        var field = typeof(SphereNet.Server.Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        if (!_saved.ContainsKey(field))
            _saved.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    public void Dispose()
    {
        foreach (var (field, value) in _saved)
            field.SetValue(null, value);
    }

    private static void HandleAllClients(string data) =>
        typeof(SphereNet.Server.Program)
            .GetMethod("HandleAllClients", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [data]);

    [Theory]
    [InlineData("0")]          // the interpreter's "no SRC" uid
    [InlineData("01234567")]   // a uid that names nothing
    public void AServerSideCallWithoutASourceCharacterStillReachesEveryClient(string srcUid)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string script = Path.Combine(Path.GetTempPath(), $"allclients_{Guid.NewGuid():N}.scp");
        File.WriteAllText(script, "[FUNCTION f_mark_save]\r\nTAG.SAVE_SEEN=1\r\n");
        try { stack.Resources.LoadResourceFile(script); }
        finally { File.Delete(script); }

        var world = TestHarness.CreateWorld();
        SetServer("_world", world);
        SetServer("_log", NullLogger.Instance);
        SetServer("_triggerRunner", stack.Runner);

        var a = world.CreateCharacter();
        a.IsPlayer = true;
        a.IsOnline = true;
        world.PlaceCharacter(a, new Point3D(100, 100, 0, 0));
        world.AddOnlinePlayer(a);
        var b = world.CreateCharacter();
        b.IsPlayer = true;
        b.IsOnline = true;
        world.PlaceCharacter(b, new Point3D(200, 200, 0, 0));
        world.AddOnlinePlayer(b);

        HandleAllClients($"{srcUid}|f_mark_save");

        Assert.True(a.TryGetTag("SAVE_SEEN", out _));
        Assert.True(b.TryGetTag("SAVE_SEEN", out _));
    }

    /// <summary>The line is a function call like any other: the first word names the
    /// function and the rest is its ARGS (pChar->r_Verb, CServer.cpp:1867). The whole
    /// line was looked up as the name, so "serv.allclients f_announce_activity &lt;text&gt;"
    /// matched nothing and an announcement that carries its text reached nobody.</summary>
    [Fact]
    public void AFunctionLineWithArgumentsRunsWithThemAsArgs()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string script = Path.Combine(Path.GetTempPath(), $"allclients_{Guid.NewGuid():N}.scp");
        File.WriteAllLines(script, [
            "[FUNCTION f_announce]",
            "IF <account.tag0.no_announce> == 0",
            "TAG.GOT=<args>",
            "ENDIF",
        ]);
        try { stack.Resources.LoadResourceFile(script); }
        finally { File.Delete(script); }

        var world = TestHarness.CreateWorld();
        SetServer("_world", world);
        SetServer("_log", NullLogger.Instance);
        SetServer("_triggerRunner", stack.Runner);

        var a = world.CreateCharacter();
        a.IsPlayer = true;
        a.IsOnline = true;
        world.PlaceCharacter(a, new Point3D(100, 100, 0, 0));
        world.AddOnlinePlayer(a);

        HandleAllClients("0|f_announce 10 Ekim 2026 tarihine etkinlik eklendi!");

        Assert.Equal("10 Ekim 2026 tarihine etkinlik eklendi!", a.Tags.Get("GOT"));
    }
}
