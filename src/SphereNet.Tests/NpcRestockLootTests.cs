using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>Field report: every spawner NPC had an empty backpack and dropped
/// no loot. The pack declares monster gear/loot in ON=@NPCRestock blocks
/// (ITEMNEWBIE gear + ITEM backpack loot); Source-X NPC_LoadScript fires
/// @NPCRestock for EVERY new NPC (CCharNPC.cpp:289-290), and ITEMNEWBIE maps
/// to ATTR_NEWBIE so worn gear stays off the corpse.</summary>
[Collection("DefinitionLoaderSerial")]
public class NpcRestockLootTests
{
    private sealed class NullConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "test";
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    private static void LoadDefinitions(string contents)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_restock_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, contents);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    [Fact]
    public void ItemNewbieVerb_FlagsNewbieAttr_PlainItemVerbDoesNot()
    {
        LoadDefinitions("""
            [ITEMDEF 0eed]
            DEFNAME=i_test_coin
            NAME=test coin

            [ITEMDEF 0f7e]
            DEFNAME=i_test_bone
            NAME=test bone
            """);

        var world = CreateWorld();
        var npc = world.CreateCharacter();
        var console = new NullConsole();

        Assert.True(npc.TryExecuteCommand("ITEMNEWBIE", "i_test_bone", console));
        Assert.True(npc.TryExecuteCommand("ITEM", "i_test_coin,15", console));

        var pack = npc.Backpack;
        Assert.NotNull(pack);

        var bone = pack!.Contents.FirstOrDefault(i => i.BaseId == 0x0f7e);
        var coin = pack.Contents.FirstOrDefault(i => i.BaseId == 0x0eed);
        Assert.NotNull(bone);
        Assert.NotNull(coin);

        // ITEMNEWBIE = ATTR_NEWBIE (stays with owner on death); plain ITEM
        // loot must NOT carry the flag or it would never drop to the corpse.
        Assert.True(bone!.IsAttr(ObjAttributes.Newbie));
        Assert.False(coin!.IsAttr(ObjAttributes.Newbie));
        Assert.Equal(15, coin.Amount);
    }

    [Fact]
    public void NpcRestockTriggerBody_FillsBackpackThroughDispatcher()
    {
        // The live pack declares monster loot inside ON=@NPCRestock chardef
        // bodies; the full chain (FireCharTrigger → chardef-own trigger →
        // interpreter ITEM verbs) must land the items on the NPC.
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_restock_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp, """
            [ITEMDEF 0eed]
            DEFNAME=i_rst_coin
            NAME=rst coin

            [ITEMDEF 0f7e]
            DEFNAME=i_rst_bone
            NAME=rst bone

            [CHARDEF 011]
            DEFNAME=c_rst_mob
            NAME=rst mob

            ON=@NPCRestock
            ITEMNEWBIE=i_rst_bone
            ITEM=i_rst_coin,15
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tmp);
            ScriptTestBootstrap.LoadDefinitions(stack.Resources);

            var world = CreateWorld();
            var npc = world.CreateCharacter();
            npc.CharDefIndex = 0x11;

            stack.Dispatcher.FireCharTrigger(npc,
                SphereNet.Core.Enums.CharTrigger.NPCRestock,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });

            var pack = npc.Backpack;
            Assert.NotNull(pack);
            Assert.Contains(pack!.Contents, i => i.BaseId == 0x0f7e);
            var coin = pack.Contents.FirstOrDefault(i => i.BaseId == 0x0eed);
            Assert.NotNull(coin);
            Assert.Equal(15, coin!.Amount);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
