using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

/// <summary>
/// SERV.&lt;ini key&gt;=value changes the setting at run time: a SERV assignment no
/// verb claims is CServer::r_LoadVal, which tries g_Cfg.r_LoadVal first
/// (CServer.cpp:1620). A pack's f_onserver_save sets FORCEGARBAGECOLLECT to 1 one save
/// in five and to 0 otherwise; the write was dropped, the read kept answering 1, and
/// every save ran the full pre-save sweep.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ServConfigWriteTests
{
    private static readonly FieldInfo ConfigField = typeof(SphereNet.Server.Program)
        .GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public void AScriptSetsForceGarbageCollectAndReadsItBack()
    {
        object? saved = ConfigField.GetValue(null);
        var config = new SphereConfig { ForceGarbageCollect = true };
        ConfigField.SetValue(null, config);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            var resolve = typeof(SphereNet.Server.Program)
                .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
            stack.Interpreter.ServerPropertyResolver = p => (string?)resolve.Invoke(null, [p]);

            var world = TestHarness.CreateWorld();
            ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;
            var ch = world.CreateCharacter();
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            stack.Interpreter.Execute(
                [new ScriptKey("SERV.ForceGarbageCollect", "0"),
                 new ScriptKey("TAG.OUT", "<SERV.ForceGarbageCollect>")],
                ch, null, new TriggerArgs(), new ScriptScope());
            Assert.False(config.ForceGarbageCollect);
            Assert.Equal("0", ch.Tags.Get("OUT"));

            stack.Interpreter.Execute(
                [new ScriptKey("SERV.ForceGarbageCollect", "1")],
                ch, null, new TriggerArgs(), new ScriptScope());
            Assert.True(config.ForceGarbageCollect);
        }
        finally
        {
            ConfigField.SetValue(null, saved);
        }
    }
}
