using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

public class ScriptObjectParityTests
{
    /// <summary>Records the privilege of the console a verb was run under.</summary>
    private sealed class PrivCapturingObj : IScriptObj
    {
        public PrivLevel? LastVerbPriv;
        public int VerbRuns;
        public string GetName() => "Capture";
        public bool TryGetProperty(string key, out string value) { value = ""; return false; }
        public bool TryExecuteCommand(string key, string args, ITextConsole source)
        {
            if (!key.Equals("MARKPRIV", StringComparison.OrdinalIgnoreCase))
                return false;
            LastVerbPriv = source.GetPrivLevel();
            VerbRuns++;
            return true;
        }
        public bool TrySetProperty(string key, string value) => false;
        public TriggerResult OnTrigger(int t, IScriptObj? s, ITriggerArgs? a) => TriggerResult.Default;
    }

    private sealed class FixedPrivConsole(PrivLevel priv) : ITextConsole
    {
        public PrivLevel GetPrivLevel() => priv;
        public void SysMessage(string text) { }
        public string GetName() => "src";
    }

    private static ScriptInterpreter NewInterpreter() =>
        new(new ExpressionParser(), LoggerFactory.Create(_ => { }).CreateLogger<ScriptInterpreter>());

    [Fact]
    public void ScriptInterpreter_TrySrv_RunsVerbAtServerPrivilege()
    {
        var interpreter = NewInterpreter();
        var target = new PrivCapturingObj();
        var guest = new FixedPrivConsole(PrivLevel.Guest);

        // TRY runs the verb under the ORIGINAL (guest) source.
        interpreter.Execute([new ScriptKey("TRY", "MARKPRIV")], target, guest, null, new ScriptScope());
        Assert.Equal(PrivLevel.Guest, target.LastVerbPriv);

        // TRYSRV elevates to server (Owner / PLEVEL 7) regardless of the guest source —
        // the fix: it used to pass the guest source, so a privileged verb still failed.
        interpreter.Execute([new ScriptKey("TRYSRV", "MARKPRIV")], target, guest, null, new ScriptScope());
        Assert.Equal(PrivLevel.Owner, target.LastVerbPriv);
    }

    [Fact]
    public void ScriptInterpreter_Tryp_GatesOnSourcePrivilege()
    {
        var interpreter = NewInterpreter();
        var target = new PrivCapturingObj();
        var guest = new FixedPrivConsole(PrivLevel.Guest);

        // A plevel-4 (GM) gate blocks a guest source — the verb does not run.
        interpreter.Execute([new ScriptKey("TRYP", "4 MARKPRIV")], target, guest, null, new ScriptScope());
        Assert.Equal(0, target.VerbRuns);

        // A plevel-0 gate passes for a guest.
        interpreter.Execute([new ScriptKey("TRYP", "0 MARKPRIV")], target, guest, null, new ScriptScope());
        Assert.Equal(1, target.VerbRuns);
    }

    [Fact]
    public void ScriptInterpreter_Link_ResolvesObjectsOwnLink_DistinctFromAct()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var interpreter = new ScriptInterpreter(
            new ExpressionParser(),
            loggerFactory.CreateLogger<ScriptInterpreter>());

        var door = world.CreateItem();
        door.Name = "oak door";
        var lever = world.CreateItem();
        lever.Name = "lever";
        lever.Link = door.Uid; // the lever's m_uidLink points at the door

        // A different object sits in the ACT / Object2 slot, to prove LINK no longer
        // collides with ACT (both used to resolve Object2).
        var bystander = new Character { Name = "Bystander" };
        bystander.SetUid(new Serial(0x00009999));
        var args = new TriggerArgs { Object2 = bystander };

        var scope = new ScriptScope();
        var lines = new[]
        {
            new ScriptKey("TAG.LINKUID", "<LINK>"),
            new ScriptKey("TAG.LINKNAME", "<LINK.NAME>"),
            new ScriptKey("TAG.ACTNAME", "<ACT.NAME>"),
        };

        interpreter.Execute(lines, lever, null, args, scope);

        // <LINK> reflects the lever's actual link, not the ACT object.
        Assert.True(lever.TryGetProperty("TAG.LINKUID", out var linkUid));
        Assert.Equal($"0{door.Uid.Value:X}", linkUid);
        Assert.True(lever.TryGetProperty("TAG.LINKNAME", out var linkName));
        Assert.Equal("oak door", linkName);
        // ACT still resolves Object2 — the two references are now independent.
        Assert.True(lever.TryGetProperty("TAG.ACTNAME", out var actName));
        Assert.Equal("Bystander", actName);
    }

    [Fact]
    public void ScriptInterpreter_LocalFloatAndRefValues_RoundTripThroughAngleExpressions()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(
            new ExpressionParser(),
            loggerFactory.CreateLogger<ScriptInterpreter>());

        var target = new Character { Name = "Target" };
        target.SetUid(new Serial(0x00000100));
        var linked = new Character { Name = "Linked" };
        linked.SetUid(new Serial(0x00000101));

        string? refExec = null;
        interpreter.ServerPropertyResolver = request =>
        {
            if (request == $"_REF_GET={linked.Uid.Value}|NAME")
                return linked.Name;
            if (request.StartsWith("_REF_EXEC=", StringComparison.Ordinal))
            {
                refExec = request;
                return "1";
            }
            return null;
        };

        var scope = new ScriptScope();
        var lines = new[]
        {
            new ScriptKey("LOCAL.NAME", "alpha"),
            new ScriptKey("LOCAL.HEX", "0A"),
            new ScriptKey("FLOAT.RATE", "<FEVAL 1/2+1>"),
            new ScriptKey("REF1", linked.Uid.Value.ToString()),
            new ScriptKey("REF1.TAG.MARK", "ok"),
            new ScriptKey("TAG.LOCAL", "<LOCAL.NAME>"),
            new ScriptKey("TAG.DLOCAL", "<DLOCAL.HEX>"),
            new ScriptKey("TAG.FLOAT", "<FLOAT.RATE>"),
            new ScriptKey("TAG.REFNAME", "<REF1.NAME>"),
        };

        interpreter.Execute(lines, target, null, null, scope);

        Assert.True(target.TryGetProperty("TAG.LOCAL", out var local));
        Assert.Equal("alpha", local);
        Assert.True(target.TryGetProperty("TAG.DLOCAL", out var dlocal));
        Assert.Equal("10", dlocal);
        Assert.True(target.TryGetProperty("TAG.FLOAT", out var flt));
        Assert.Equal("1.5", flt);
        Assert.True(target.TryGetProperty("TAG.REFNAME", out var refName));
        Assert.Equal("Linked", refName);
        Assert.Equal($"_REF_EXEC={linked.Uid.Value}|TAG.MARK|ok", refExec);
    }

    [Fact]
    public void ExpressionParser_StringHelpers_KeepSourceXFallbackSemantics()
    {
        var parser = new ExpressionParser();

        Assert.Equal("iron ingot", parser.EvaluateStr("<STRREPLACE iron ore,ore,ingot>"));
        Assert.Equal("a|b|c", parser.EvaluateStr("<STRJOIN |,a,b,c>"));
        Assert.Equal("0", parser.EvaluateStr("<ISOBSCENE anything>"));
    }

    [Fact]
    public void ScriptInterpreter_ServAndUidExpressions_UseServerResolver()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(
            new ExpressionParser(),
            loggerFactory.CreateLogger<ScriptInterpreter>());

        interpreter.ServerPropertyResolver = request => request.ToUpperInvariant() switch
        {
            "SERVNAME" => "SphereNet",
            "UID.00000101.NAME" => "Linked",
            _ => null
        };

        var target = new Character { Name = "Target" };
        target.SetUid(new Serial(0x00000100));

        var lines = new[]
        {
            new ScriptKey("TAG.SERVER", "<SERV.SERVNAME>"),
            new ScriptKey("TAG.UIDNAME", "<UID.00000101.NAME>"),
        };

        interpreter.Execute(lines, target, null, null, new ScriptScope());

        Assert.True(target.TryGetProperty("TAG.SERVER", out var serverName));
        Assert.Equal("SphereNet", serverName);
        Assert.True(target.TryGetProperty("TAG.UIDNAME", out var uidName));
        Assert.Equal("Linked", uidName);
    }

    [Fact]
    public void ScriptInterpreter_UidCommand_RoutesThroughRefExecBridge()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(
            new ExpressionParser(),
            loggerFactory.CreateLogger<ScriptInterpreter>());

        string? refExec = null;
        interpreter.ServerPropertyResolver = request =>
        {
            if (request.StartsWith("_REF_EXEC=", StringComparison.Ordinal))
                refExec = request;
            return "1";
        };

        var target = new Character { Name = "Target" };
        var lines = new[]
        {
            new ScriptKey("UID.00000101.DIALOG", "d_test"),
        };

        interpreter.Execute(lines, target, null, null, new ScriptScope());

        Assert.Equal("_REF_EXEC=00000101|DIALOG|d_test", refExec);
    }

    [Fact]
    public void ScriptInterpreter_ServAllClients_RoutesThroughServerIteratorBridge()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(
            new ExpressionParser(),
            loggerFactory.CreateLogger<ScriptInterpreter>());

        string? allClients = null;
        interpreter.ServerPropertyResolver = request =>
        {
            if (request.StartsWith("_ALLCLIENTS=", StringComparison.Ordinal))
                allClients = request;
            return "1";
        };

        var target = new Character { Name = "Target" };
        target.SetUid(new Serial(0x00000100));
        var args = new TriggerArgs { Source = target };

        var lines = new[]
        {
            new ScriptKey("SERV.ALLCLIENTS", "f_test"),
        };

        interpreter.Execute(lines, target, null, args, new ScriptScope());

        Assert.True(target.TryGetProperty("UID", out var uid));
        Assert.Equal($"_ALLCLIENTS={uid}|f_test", allClients);
    }
}
