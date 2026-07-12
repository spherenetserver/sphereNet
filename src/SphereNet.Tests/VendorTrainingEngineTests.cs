using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.Trade;

namespace SphereNet.Tests;

/// <summary>
/// NPC skill-training math (Source-X NPC_GetTrainMax / NPC_OnTrainCheck /
/// NPC_TrainSkill): the teacher's cap, the total-skill headroom throttle with
/// DOWN-lock sacrifice, and the actual skill raise.
/// </summary>
public class VendorTrainingEngineTests
{
    private static Character MakeChar()
    {
        var ch = new Character();
        // Park every skill at 0 with an UP lock so the total-skill cap is wide open.
        for (int i = 0; i < SkillEngine.BaseSkillCount; i++)
        {
            ch.SetSkill((SkillType)i, 0);
            ch.SetSkillLock((SkillType)i, 0);
        }
        return ch;
    }

    [Fact]
    public void GetTrainMax_IsCappedByTrainerSkillAndAbsoluteMax()
    {
        var trainer = MakeChar();
        var student = MakeChar();
        trainer.SetSkill(SkillType.Swordsmanship, 500); // trainer at 50.0

        // 100% of the trainer's 500, but the absolute TrainSkillMax (300) wins.
        Assert.Equal(300, VendorTrainingEngine.GetTrainMax(trainer, student, SkillType.Swordsmanship));

        // Lower the trainer under the cap → percent-of-trainer binds.
        trainer.SetSkill(SkillType.Swordsmanship, 220);
        Assert.Equal(220, VendorTrainingEngine.GetTrainMax(trainer, student, SkillType.Swordsmanship));
    }

    [Fact]
    public void CalcTrainableAmount_GapToCap_ZeroWhenAlreadyAbove()
    {
        var trainer = MakeChar();
        var student = MakeChar();
        trainer.SetSkill(SkillType.Magery, 1000); // cap → min(300 absolute, ...)

        student.SetSkill(SkillType.Magery, 100);
        Assert.Equal(200, VendorTrainingEngine.CalcTrainableAmount(trainer, student, SkillType.Magery));

        // A student already past the trainer's cap cannot train.
        student.SetSkill(SkillType.Magery, 350);
        Assert.Equal(0, VendorTrainingEngine.CalcTrainableAmount(trainer, student, SkillType.Magery));
    }

    [Fact]
    public void TrainSkill_RaisesSkill_WithinHeadroom()
    {
        var student = MakeChar();
        student.SetSkill(SkillType.Fencing, 100);

        VendorTrainingEngine.TrainSkill(student, SkillType.Fencing, 150);

        Assert.Equal(250, student.GetSkill(SkillType.Fencing));
    }

    [Fact]
    public void TrainSkill_OverTotalCap_DrainsDownLockedSkills()
    {
        var student = MakeChar();
        // Fill the total-skill budget so a raise must free room elsewhere.
        int sumMax = SkillEngine.GetSkillSumMax(student);
        student.SetSkill(SkillType.Tactics, (ushort)(sumMax - 100)); // near the cap
        student.SetSkill(SkillType.Wrestling, 100);
        student.SetSkillLock(SkillType.Wrestling, 1); // DOWN — sacrificeable

        // Train Fencing by 100. Sum is already at cap, so the 100 must come out
        // of the DOWN-locked Wrestling.
        VendorTrainingEngine.TrainSkill(student, SkillType.Fencing, 100);

        Assert.Equal(100, student.GetSkill(SkillType.Fencing));
        Assert.Equal(0, student.GetSkill(SkillType.Wrestling)); // drained to make room
    }

    [Fact]
    public void TrainCost_ScalesByPointsAndMultiplier()
    {
        int savedCost = VendorTrainingEngine.TrainSkillCost;
        try
        {
            VendorTrainingEngine.TrainSkillCost = 3;
            Assert.Equal(300, VendorTrainingEngine.TrainCost(100));
        }
        finally { VendorTrainingEngine.TrainSkillCost = savedCost; }
    }

    [Fact]
    public void TryPay_FullPayment_TrainsAndConsumesGold()
    {
        var world = TestHarness.CreateWorld();
        var trainer = world.CreateCharacter();
        var student = world.CreateCharacter();
        for (int i = 0; i < SkillEngine.BaseSkillCount; i++)
        {
            trainer.SetSkill((SkillType)i, 0); student.SetSkill((SkillType)i, 0);
            student.SetSkillLock((SkillType)i, 0);
        }
        trainer.SetSkill(SkillType.Blacksmithing, 1000); // teaches up to 300
        student.SetSkill(SkillType.Blacksmithing, 100);  // trainable = 200

        VendorTrainingEngine.RememberOffer(trainer, student, SkillType.Blacksmithing);

        var gold = world.CreateItem();
        gold.ItemType = ItemType.Gold;
        gold.Amount = 1000; // more than the 200-point cost at 1/pt

        var trained = VendorTrainingEngine.TryPay(trainer, student, gold);

        Assert.Equal(SkillType.Blacksmithing, trained);
        Assert.Equal(300, student.GetSkill(SkillType.Blacksmithing)); // reached the cap
        Assert.Equal(800, gold.Amount);                                // 200 gold spent
        Assert.False(trainer.TryGetTag(VendorTrainingEngine.PendingTag(student), out _)); // offer cleared
    }

    [Fact]
    public void TryPay_NoOffer_ReturnsNull()
    {
        var world = TestHarness.CreateWorld();
        var trainer = world.CreateCharacter();
        var student = world.CreateCharacter();
        var gold = world.CreateItem();
        gold.ItemType = ItemType.Gold;
        gold.Amount = 100;

        Assert.Null(VendorTrainingEngine.TryPay(trainer, student, gold));
        Assert.Equal(100, gold.Amount); // untouched
    }

    [Fact]
    public void TryPay_PartialPayment_TrainsProportionally()
    {
        var world = TestHarness.CreateWorld();
        var trainer = world.CreateCharacter();
        var student = world.CreateCharacter();
        for (int i = 0; i < SkillEngine.BaseSkillCount; i++)
        {
            trainer.SetSkill((SkillType)i, 0); student.SetSkill((SkillType)i, 0);
            student.SetSkillLock((SkillType)i, 0);
        }
        trainer.SetSkill(SkillType.Alchemy, 1000);
        student.SetSkill(SkillType.Alchemy, 100); // trainable 200, cost 200 @1/pt

        VendorTrainingEngine.RememberOffer(trainer, student, SkillType.Alchemy);

        var gold = world.CreateItem();
        gold.ItemType = ItemType.Gold;
        gold.Amount = 100; // only half the 200 cost

        var trained = VendorTrainingEngine.TryPay(trainer, student, gold);

        Assert.Equal(SkillType.Alchemy, trained);
        Assert.Equal(200, student.GetSkill(SkillType.Alchemy)); // 100 of 200 points
        Assert.True(gold.IsDeleted);                             // all 100 gold consumed
    }
}
