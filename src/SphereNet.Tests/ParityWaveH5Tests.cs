using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-H5 (wiki/hedef.txt long tail):
//   * NPC skill training payment (Source-X NPC_OnTrainPay): gold handed to a
//     trainer with a pending offer buys 0.1 skill per gp up to the cap;
//     overpayment bounces back
//   * NPC ground-food seeking (Source-X NPC_Act_Food): a hungry creature eats
//     edibles lying next to it instead of only pack food
public class ParityWaveH5Tests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void TrainPayment_RaisesSkill_CapsAndRefundsOverpayment()
    {
        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 1701);

        var student = world.CreateCharacter();
        student.IsPlayer = true;
        world.PlaceCharacter(student, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        student.Backpack = pack;
        student.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, student);

        var trainer = world.CreateCharacter();
        trainer.Name = "Anatomy Teacher";
        trainer.NpcBrain = NpcBrainType.Human;
        world.PlaceCharacter(trainer, new Point3D(101, 100, 0, 0));

        // The speech step ("train anatomy") would have armed this offer.
        student.SetTag("TRAIN_PENDING", $"{trainer.Uid.Value}|{(int)SkillType.Anatomy}|300");

        // First payment: 100 gp = +10.0 skill (0 → 100), gold fully consumed.
        var gold1 = world.CreateItem();
        gold1.ItemType = ItemType.Gold;
        gold1.BaseId = 0x0EED;
        gold1.Amount = 100;
        pack.AddItem(gold1);
        client.HandleItemPickup(gold1.Uid.Value, 100);
        client.HandleItemDrop(gold1.Uid.Value, 0, 0, 0, trainer.Uid.Value);

        Assert.Equal(100, student.GetSkill(SkillType.Anatomy));
        Assert.True(gold1.IsDeleted);
        Assert.True(student.TryGetTag("TRAIN_PENDING", out _)); // cap not reached yet

        // Second payment overshoots the 30.0 cap: 300 gp buys only 200 points,
        // the leftover 100 bounces back and the offer closes.
        var gold2 = world.CreateItem();
        gold2.ItemType = ItemType.Gold;
        gold2.BaseId = 0x0EED;
        gold2.Amount = 300;
        pack.AddItem(gold2);
        client.HandleItemPickup(gold2.Uid.Value, 300);
        client.HandleItemDrop(gold2.Uid.Value, 0, 0, 0, trainer.Uid.Value);

        Assert.Equal(300, student.GetSkill(SkillType.Anatomy));
        Assert.False(student.TryGetTag("TRAIN_PENDING", out _));
        Assert.False(gold2.IsDeleted);
        Assert.Equal(100, gold2.Amount);                 // refund
        Assert.Equal(pack.Uid, gold2.ContainedIn);       // back in the pack
    }

    [Fact]
    public void HungryNpc_EatsAdjacentGroundFood()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());

        // The NPC tick parks creatures with no player in view — anchor an
        // online player nearby so the deer's brain actually runs.
        var observer = world.CreateCharacter();
        observer.IsPlayer = true;
        observer.IsOnline = true;
        world.PlaceCharacter(observer, new Point3D(105, 100, 0, 0));
        world.AddOnlinePlayer(observer);
        world.OnTick();

        var deer = world.CreateCharacter();
        deer.NpcBrain = NpcBrainType.Animal;
        deer.SetTag("INTFOOD", "1");
        deer.NpcFood = 10; // hungry
        deer.NextNpcActionTime = 0;
        world.PlaceCharacter(deer, new Point3D(100, 100, 0, 0));

        var apple = world.CreateItem();
        apple.ItemType = ItemType.Fruit;
        apple.BaseId = 0x09D0;
        apple.Amount = 1;
        world.PlaceItem(apple, new Point3D(101, 100, 0, 0)); // adjacent

        ushort foodBefore = deer.NpcFood;
        for (int i = 0; i < 10 && !apple.IsDeleted; i++)
        {
            deer.NextNpcActionTime = 0;
            ai.OnTickAction(deer);
        }

        Assert.True(apple.IsDeleted);            // the meal was eaten off the ground
        Assert.True(deer.NpcFood > foodBefore);  // and the food meter rose
    }
}
