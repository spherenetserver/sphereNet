using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>SERV.SAVE, SERV.RESPAWN and SERV.RESTOCK written as script lines are
/// server verbs (CServer::r_Verb). They used to reach no handler and do nothing,
/// with no warning, so a script ending in SERV.SAVE never saved.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ServVerbLineTests
{
    [Theory]
    [InlineData("SERV.SAVE", "SAVE")]
    [InlineData("SERV.RESPAWN", "RESPAWN")]
    [InlineData("serv.restock", "RESTOCK")]
    public void AServerVerbLineReachesTheHost(string line, string expected)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var seen = new List<string>();
        stack.Interpreter.ServerPropertyResolver = p => { seen.Add(p); return ""; };
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();

        stack.Interpreter.Execute([new ScriptKey(line, "")], ch, null, new TriggerArgs(), new ScriptScope());

        Assert.Equal([expected], seen);
    }
}
