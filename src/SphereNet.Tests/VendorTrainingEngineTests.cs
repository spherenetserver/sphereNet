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
        trainer.SetSkill(SkillType.Swordsmanship, 1500); // trainer at 150.0

        // 30% of the trainer's 1500 is 450 — the absolute TrainSkillMax (420) wins
        // (Source-X TRAINSKILLPERCENT=30 / TRAINSKILLMAX=420 defaults).
        Assert.Equal(420, VendorTrainingEngine.GetTrainMax(trainer, student, SkillType.Swordsmanship));

        // Lower the trainer under the cap → percent-of-trainer binds (30% of 220).
        trainer.SetSkill(SkillType.Swordsmanship, 220);
        Assert.Equal(66, VendorTrainingEngine.GetTrainMax(trainer, student, SkillType.Swordsmanship));
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

    /// <summary>A trainer's own caps win over the shard's.
    ///
    /// NPC_GetTrainMax reads OVERRIDE.TRAINSKILLMAXPERCENT and OVERRIDE.TRAINSKILLMAX
    /// off the character's key chain before falling back to the config
    /// (CCharNPCStatus.cpp:523/529), and the reference pack's guildmasters set both -
    /// TRAINSKILLMAX=50.0 with TRAINSKILLMAXPERCENT=50. Ignored, every master trainer
    /// taught to the shard default of 30% and 42.0, the same as a village smith.</summary>
    [Fact]
    public void ATrainersOwnCapsOverrideTheShardDefaults()
    {
        var trainer = MakeChar();
        var student = MakeChar();
        trainer.SetSkill(SkillType.Swordsmanship, 1500);   // 150.0

        // Shard defaults: 30% of 1500 = 450, clipped to 420.
        Assert.Equal(420, VendorTrainingEngine.GetTrainMax(trainer, student, SkillType.Swordsmanship));

        // A guildmaster: half its own skill, up to 50.0. 50% of 1500 is 750, and the
        // trainer's own absolute cap of 500 binds.
        trainer.SetTag("OVERRIDE.TRAINSKILLMAXPERCENT", "50");
        trainer.SetTag("OVERRIDE.TRAINSKILLMAX", "50.0");   // the script number: 500
        Assert.Equal(500, VendorTrainingEngine.GetTrainMax(trainer, student, SkillType.Swordsmanship));

        // Under that cap the percent binds again: 50% of 800 is 400.
        trainer.SetSkill(SkillType.Swordsmanship, 800);
        Assert.Equal(400, VendorTrainingEngine.GetTrainMax(trainer, student, SkillType.Swordsmanship));
    }

    /// <summary>And its own price per point (OVERRIDE.TRAINSKILLCOST,
    /// CCharNPCAct_Vendor.cpp:286).</summary>
    [Fact]
    public void ATrainersOwnPriceOverridesTheShardDefault()
    {
        var trainer = MakeChar();
        Assert.Equal(VendorTrainingEngine.TrainSkillCost * 100,
            VendorTrainingEngine.TrainCost(trainer, 100));

        trainer.SetTag("OVERRIDE.TRAINSKILLCOST", "7");
        Assert.Equal(700, VendorTrainingEngine.TrainCost(trainer, 100));
    }
}
