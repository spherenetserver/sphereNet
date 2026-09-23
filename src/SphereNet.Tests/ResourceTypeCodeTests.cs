using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

/// <summary>RESOURCETYPE answers the Source-X RES_TYPE code (CResourceID.h), the
/// same numbers the packs' [DEFNAME] RES_* constants carry. Worldgen spawners test
/// <c>&lt;RESOURCETYPE x&gt; == &lt;def.res_chardef&gt;</c> to pick t_spawn_char or
/// t_spawn_item; an off-by-one code left every spawner without a type.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ResourceTypeCodeTests
{
    [Fact]
    public void ResourceTypeUsesTheSourceXEnumCodes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"restype-{Guid.NewGuid():N}.scp");
        using var logs = LoggerFactory.Create(_ => { });
        try
        {
            File.WriteAllText(path,
                "[CHARDEF c_restype_probe]\nID=c_man\n" +
                "[ITEMDEF i_restype_probe]\nID=0eed\n" +
                "[SPAWN sp_restype_probe]\nID=c_restype_probe\n" +
                "[TEMPLATE tm_restype_probe]\nITEM=i_restype_probe\n" +
                "[DEFNAME restype_aliases]\nrestype_alias {c_restype_probe 1}\n" +
                "[EOF]\n");
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19531);
            var player = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);

            string Read(string key)
            {
                Assert.True(client.TryResolveScriptVariable(key, player, null, out string value));
                return value;
            }

            // Written in hex, as upstream's FormatHex does.
            Assert.Equal("07", Read("RESOURCETYPE c_restype_probe"));
            Assert.Equal("0f", Read("RESOURCETYPE i_restype_probe"));
            Assert.Equal("026", Read("RESOURCETYPE sp_restype_probe"));
            Assert.Equal("02e", Read("RESOURCETYPE tm_restype_probe"));
            Assert.Equal("0", Read("RESOURCETYPE no_such_resource"));
            // The argument is evaluated, so an alias answers for what it names.
            Assert.Equal("07", Read("RESOURCETYPE restype_alias"));
            Assert.Equal(Read("RESOURCEINDEX c_restype_probe"), Read("RESOURCEINDEX restype_alias"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
