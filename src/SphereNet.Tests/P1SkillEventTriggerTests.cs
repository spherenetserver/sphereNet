using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the wired P1 triggers @Eat, @SkillMenu and @SkillWait. @Eat fires (and
// can block) on eating/drinking; @SkillMenu fires when a skill opens a selection
// menu (Tracking); @SkillWait fires each tick while a delayed skill is still in
// progress and is IsTrigUsed-gated so the per-tick loop costs nothing when nobody
// hooks it.
public class P1SkillEventTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (GameClient client, Character player, TriggerDispatcher d, List<string> order) Setup(GameWorld world)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1601);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, player);

        var d = new TriggerDispatcher();
        var order = new List<string>();
        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: d);
        return (client, player, d, order);
    }

    [Fact]
    public void Eat_Food_FiresEatTrigger_AndFeedsByDefault()
    {
        var world = CreateWorld();
        var (client, player, d, _) = Setup(world);
        int eatN1 = -1;
        d.RegisterCharEvent("EVENTSPLAYER", "Eat", (_, a) => { eatN1 = a.N1; return TriggerResult.Default; });

        player.Food = 0;
        var food = world.CreateItem();
        food.ItemType = ItemType.Food;
        food.Amount = 1;
        player.Backpack!.AddItem(food);

        client.HandleDoubleClick(food.Uid.Value);

        Assert.Equal(5, eatN1);          // @Eat fired with hunger restored
        Assert.Equal(5, player.Food);    // fed by default
        Assert.True(food.IsDeleted);     // single unit consumed
    }

    [Fact]
    public void Eat_Food_ReturnTrue_BlocksTheMeal()
    {
        var world = CreateWorld();
        var (client, player, d, _) = Setup(world);
        d.RegisterCharEvent("EVENTSPLAYER", "Eat", (_, _) => TriggerResult.True);

        player.Food = 0;
        var food = world.CreateItem();
        food.ItemType = ItemType.Food;
        food.Amount = 1;
        player.Backpack!.AddItem(food);

        client.HandleDoubleClick(food.Uid.Value);

        Assert.Equal(0, player.Food);    // blocked — no feeding
        Assert.False(food.IsDeleted);    // and not consumed
    }

    [Fact]
    public void SkillMenu_TrackingOpensMenu_FiresSkillMenu()
    {
        var world = CreateWorld();
        var (client, player, d, _) = Setup(world);
        int menuSkill = -1;
        d.RegisterCharEvent("EVENTSPLAYER", "SkillMenu", (_, a) => { menuSkill = a.N1; return TriggerResult.Default; });

        client.HandleUseSkill((int)SkillType.Tracking); // menu-kind skill

        Assert.Equal((int)SkillType.Tracking, menuSkill);
    }

    [Fact]
    public void SkillWait_PendingSkillTick_FiresWhenHooked()
    {
        var world = CreateWorld();
        var (client, player, d, _) = Setup(world);
        int waitSkill = -1;
        d.RegisterCharEvent("EVENTSPLAYER", "SkillWait", (_, a) => { waitSkill = a.N1; return TriggerResult.Default; });

        // Pending skill: delay far ahead, next stroke parked far ahead so only the
        // "still waiting" branch runs this tick.
        int skillId = (int)SkillType.Hiding;
        player.BeginSkillPending(skillId, delayEnd: long.MaxValue / 2, strokeNext: long.MaxValue / 2, Serial.Invalid, null);

        client.TickPendingSkill();

        Assert.Equal(skillId, waitSkill); // @SkillWait fired (IsTrigUsed gate open)
        Assert.True(player.HasActiveSkillPending()); // still waiting, not completed
    }
}
