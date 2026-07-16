using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>End-to-end regression net for the gather stroke model: a fishing
/// attempt scheduled through the real client path (HandleUseSkill → target
/// response → TickPendingSkill) must run strokes × DELAY and deliver the fish
/// at completion. Field report: "no fish at all" after the stroke rework.</summary>
[Collection("DefinitionLoaderSerial")]
public class GatherStrokeCompletionTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    private static void LoadDefinitions(string contents)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_gather_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, contents);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    [Fact]
    public void Fishing_DelayedGatherAttempt_DeliversFishOnCompletion()
    {
        LoadDefinitions("""
            [SKILL 18]
            DEFNAME=Skill_Fishing
            KEY=Fishing
            DELAY=1
            FLAGS=skf_gather

            [ITEMDEF 09cc]
            DEFNAME=i_test_fish
            NAME=test fish

            [REGIONRESOURCE mr_test_fish]
            DEFNAME=mr_test_fish
            AMOUNT=10
            REAP=09cc
            REAPAMOUNT=1
            SKILL=0.0

            [REGIONTYPE r_test_water t_water]
            RESOURCES=100.0 mr_test_fish
            """);

        var lf = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new SphereNet.Game.Accounts.AccountManager(lf), 1501);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.SetSkill(SkillType.Fishing, 1000);
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        player.Equip(pack, Layer.Pack);
        var pole = world.CreateItem();
        pole.BaseId = 0x0DBF;
        pole.ItemType = ItemType.FishPole;
        pack.TryAddItem(pole);

        client.SetEngines(
            skillHandlers: new SkillHandlers(world, new GatheringEngine(world)),
            triggerDispatcher: new TriggerDispatcher());

        // Deterministic success — the reference RNG (mr_nothing weights, skill
        // roll) is not under test here, only the schedule → complete → deliver
        // chain.
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;

        client.HandleUseSkill((int)SkillType.Fishing);
        client.HandleTargetResponse(1, 0, 0, 102, 100, 0, 0); // water 2 tiles away

        Assert.True(player.HasActiveSkillPending());

        // Strokes are DELAY (100ms) apart, 1-2 strokes for fishing: drive the
        // clock past every stroke and the completion deadline.
        for (int i = 0; i < 4 && player.HasActiveSkillPending(); i++)
        {
            Thread.Sleep(150);
            client.TickPendingSkill();
        }

        Assert.False(player.HasActiveSkillPending());
        Assert.Contains(pack.Contents, i => i.BaseId == 0x09CC);
    }

    [Fact]
    public void Mining_DelayedGatherAttempt_UsesRolledStrokeSchedule()
    {
        LoadDefinitions("""
            [SKILL 45]
            DEFNAME=Skill_Mining
            KEY=Mining
            DELAY=16
            FLAGS=skf_gather
            RANGE=2
            """);

        var lf = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new SphereNet.Game.Accounts.AccountManager(lf), 1502);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        client.SetEngines(
            skillHandlers: new SkillHandlers(world, new GatheringEngine(world)),
            triggerDispatcher: new TriggerDispatcher());

        client.HandleUseSkill((int)SkillType.Mining);
        client.HandleTargetResponse(1, 0, 0, 101, 100, 0, 0);

        Assert.True(player.HasActiveSkillPending());

        // Total window = strokes × DELAY: mining rolls 2-6 strokes at 1600ms
        // each, so completion must sit 3.2-9.6s out — never the bare 1.6s the
        // old total-duration model produced.
        long now = Environment.TickCount64;
        long remaining = player.SkillDelayEnd - now;
        Assert.InRange(remaining, 2 * 1600 - 400, 6 * 1600 + 400);
        Assert.True(player.SkillStrokeNext - now <= 1600 + 200);
    }
}
