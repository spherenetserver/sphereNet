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
    public void ItemVerb_ExpandsSequentialLootTemplateIntoBackpack()
    {
        // Source-X CItem::CreateHeader/ReadTemplate: ITEM=<template> pours the
        // whole loot list into the char's pack — dice amounts ({600 750}),
        // R-chance rows, inline weighted pools, nested templates and
        // CONTAINER= sub-bags included. The old path resolved the defname,
        // saw RES_TEMPLATE instead of RES_ITEMDEF and silently spawned
        // nothing — the "balron corpse is empty" field report.
        LoadDefinitions("""
            [ITEMDEF 0EED]
            NAME=gold coin

            [ITEMDEF i_bag_loot_test]
            ID=0x0E76
            TYPE=t_container
            NAME=bag

            [ITEMDEF i_sword_loot_test]
            ID=0x13FF
            NAME=sword

            [TEMPLATE loot_test_goodie]
            ITEM=i_sword_loot_test

            [TEMPLATE loot_test_balron]
            ITEM=0EED,{600 750}
            ITEM=i_sword_loot_test,1,R1
            ITEM={ i_sword_loot_test 1 }
            ITEM=loot_test_goodie
            CONTAINER=i_bag_loot_test
            ITEM=0EED,{10 20}
            """);
        var world = CreateWorld();

        var ch = world.CreateCharacter();
        Assert.True(ch.TryExecuteCommand("ITEM", "loot_test_balron", new NullConsole()));

        var pack = ch.Backpack;
        Assert.NotNull(pack);
        var contents = pack!.Contents.ToList();

        // top-level: gold pile (600-750), R1 sword (always), inline-pool sword,
        // nested-template sword, and the bag
        var gold = contents.FirstOrDefault(i => i.BaseId == 0x0EED);
        Assert.NotNull(gold);
        Assert.InRange(gold!.Amount, 600, 750);
        Assert.Equal(3, contents.Count(i => i.BaseId == 0x13FF));

        var bag = contents.FirstOrDefault(i => i.BaseId == 0x0E76);
        Assert.NotNull(bag);
        var bagGold = bag!.Contents.FirstOrDefault(i => i.BaseId == 0x0EED);
        Assert.NotNull(bagGold);
        Assert.InRange(bagGold!.Amount, 10, 20);
    }

    [Fact]
    public void GemSpawnedNpc_RunsRestockLoot_ThroughTheScriptInitHook()
    {
        // The missing link of the field report ("npclerden drop cikmiyor"):
        // the ITEM-verb machinery and the dispatcher chain worked, and the
        // GM .add path fired @NPCRestock — but SpawnComponent (the spawn
        // gems that populate the world) never ran the chardef script init,
        // so every gem-spawned monster had an empty pack.
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_spawnloot_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp, """
            [ITEMDEF 0eed]
            DEFNAME=i_spl_coin
            NAME=spl coin

            [CHARDEF 012]
            DEFNAME=c_spl_mob
            NAME=spl mob

            ON=@NPCRestock
            ITEM=i_spl_coin,25
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tmp);
            ScriptTestBootstrap.LoadDefinitions(stack.Resources);

            var world = CreateWorld();
            SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
            // The host wiring: spawn init = @Create + @NPCRestock through
            // the dispatcher (Source-X NPC_LoadScript sequence).
            SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = npc =>
            {
                stack.Dispatcher.FireCharTrigger(npc, CharTrigger.Create,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
                stack.Dispatcher.FireCharTrigger(npc, CharTrigger.NPCRestock,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
            };

            var gem = world.CreateItem();
            world.PlaceItem(gem, new SphereNet.Core.Types.Point3D(100, 100, 0, 0));
            var spawn = new SphereNet.Game.Components.SpawnComponent(gem, world) { MaxCount = 1 };
            var npc = spawn.SpawnSpecific(0x12);

            Assert.NotNull(npc);
            var pack = npc!.Backpack;
            Assert.NotNull(pack);
            var loot = pack!.Contents.FirstOrDefault(i => i.BaseId == 0x0eed);
            Assert.NotNull(loot);
            Assert.Equal(25, loot!.Amount);
        }
        finally
        {
            SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = null;
            try { File.Delete(tmp); } catch { }
        }
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
