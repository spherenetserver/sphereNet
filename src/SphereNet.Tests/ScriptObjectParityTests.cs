using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

public class ScriptObjectParityTests
{
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
