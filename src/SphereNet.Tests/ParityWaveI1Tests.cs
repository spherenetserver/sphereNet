using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Game.Messages;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

public class ParityWaveI1Tests
{
    [Fact]
    public void ProgramServerProperties_ExposeChatSoundAndHearAllConfig()
    {
        var configField = typeof(SphereNet.Server.Program)
            .GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = configField.GetValue(null);
        var config = new SphereConfig
        {
            LogMask = 0,
            ChatFlags = 0x10,
            GenericSounds = false
        };

        try
        {
            configField.SetValue(null, config);

            Assert.Equal("0", Resolve("HEARALL"));
            Assert.Equal("16", Resolve("CHATFLAGS"));
            Assert.Equal("0", Resolve("GENERICSOUNDS"));

            Assert.Equal("1", Resolve("_HEARALL="));
            Assert.Equal("1", Resolve("HEARALL"));
            Assert.True((config.LogMask & SphereConfig.LogMaskPlayerSpeak) != 0);

            Assert.Equal("0", Resolve("_HEARALL=0"));
            Assert.Equal("0", Resolve("HEARALL"));
            Assert.False((config.LogMask & SphereConfig.LogMaskPlayerSpeak) != 0);
        }
        finally
        {
            configField.SetValue(null, previous);
        }
    }

    [Fact]
    public void DefMsgRuntimeSetter_OverridesServerMessagesAndResolverLookup()
    {
        ServerMessages.ClearOverrides();
        try
        {
            Assert.Equal("very clumsy", ServerMessages.Get(Msg.AnatomyDex1));

            Resolve("_SET_DEFMSG=DEFMSG.anatomy_dex_1=runtime dex");

            Assert.Equal("runtime dex", ServerMessages.Get(Msg.AnatomyDex1));
            Assert.Equal("runtime dex", Resolve("DEFMSG.anatomy_dex_1"));
        }
        finally
        {
            ServerMessages.ClearOverrides();
        }
    }

    [Fact]
    public void ScriptInterpreter_ServHearAll_RoutesToServerResolver()
    {
        var interpreter = new ScriptInterpreter(
            new ExpressionParser(),
            LoggerFactory.Create(_ => { }).CreateLogger<ScriptInterpreter>());

        string? request = null;
        interpreter.ServerPropertyResolver = r =>
        {
            request = r;
            return "1";
        };

        interpreter.Execute([new ScriptKey("SERV.HEARALL", "1")], new NullObj(), null, null, new ScriptScope());

        Assert.Equal("_HEARALL=1", request);
    }

    private static string Resolve(string property)
    {
        var method = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string?)method.Invoke(null, [property]) ?? "";
    }

    private sealed class NullObj : SphereNet.Core.Interfaces.IScriptObj
    {
        public string GetName() => "null";
        public bool TryGetProperty(string key, out string value) { value = ""; return false; }
        public bool TryExecuteCommand(string key, string args, SphereNet.Core.Interfaces.ITextConsole source) => false;
        public bool TrySetProperty(string key, string value) => false;
        public SphereNet.Core.Enums.TriggerResult OnTrigger(
            int triggerType,
            SphereNet.Core.Interfaces.IScriptObj? source,
            SphereNet.Core.Interfaces.ITriggerArgs? args) => SphereNet.Core.Enums.TriggerResult.Default;
    }
}
