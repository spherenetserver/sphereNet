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
[Collection("DefinitionLoaderSerial")]
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

    private static (Character Trainer, Character Student) Pair(SkillType skill, ushort trainerVal, ushort studentVal)
    {
        TestHarness.SeedSkillAdvRates();
        var world = TestHarness.CreateWorld();
        var trainer = world.CreateCharacter();
        trainer.NpcBrain = NpcBrainType.Vendor;
        var student = world.CreateCharacter();
        student.IsPlayer = true;
        for (int i = 0; i < SkillEngine.BaseSkillCount; i++)
        {
            trainer.SetSkill((SkillType)i, 0); student.SetSkill((SkillType)i, 0);
            student.SetSkillLock((SkillType)i, 0);
        }
        trainer.SetSkill(skill, trainerVal);
        student.SetSkill(skill, studentVal);
        return (trainer, student);
    }

    private static SphereNet.Game.Objects.Items.Item Gold(int amount)
    {
        var gold = TestHarness.CreateWorld().CreateItem();
        gold.ItemType = ItemType.Gold;
        gold.Amount = (ushort)amount;
        return gold;
    }

    /// <summary>NPC_OnTrainHear finds the skill named anywhere in the line, as a word
    /// (FindStrWord): a pack's "train" speech hands the TRAIN verb the whole line,
    /// "train blacksmithing". The trainer names its price and remembers the offer in
    /// a MEMORY_SPEAK (NPC_MEM_ACT_SPEAK_TRAIN, the skill in the high word).</summary>
    [Fact]
    public void TrainHear_NamesThePriceForASkillInTheLine()
    {
        var (trainer, student) = Pair(SkillType.Blacksmithing, 1000, 100);
        var said = new List<string>();

        Assert.True(VendorTrainingEngine.OnTrainHear(trainer, student, "train blacksmithing", said.Add));

        Assert.Equal(SphereNet.Game.Messages.ServerMessages.GetFormatted(
            SphereNet.Game.Messages.Msg.NpcTrainerPrice, 200, "Blacksmithing"), Assert.Single(said));
        var offer = VendorTrainingEngine.FindOffer(trainer, student);
        Assert.NotNull(offer);
        Assert.Equal((uint)SkillType.Blacksmithing, offer!.More1 >> 16);
    }

    [Fact]
    public void TrainHear_WithoutASkillListsWhatCanBeTaught()
    {
        var (trainer, student) = Pair(SkillType.Blacksmithing, 1000, 100);
        trainer.SetSkill(SkillType.Alchemy, 500);
        var said = new List<string>();

        Assert.True(VendorTrainingEngine.OnTrainHear(trainer, student, "train", said.Add));

        Assert.Equal("For a fee I can teach thee of Alchemy and Blacksmithing.", Assert.Single(said));
        Assert.Null(VendorTrainingEngine.FindOffer(trainer, student));
    }

    [Fact]
    public void TrainPay_MoreThanTheFee_TrainsKeepsTheFeeAndEndsTheLesson()
    {
        var (trainer, student) = Pair(SkillType.Blacksmithing, 1000, 100); // teaches 200
        VendorTrainingEngine.OnTrainHear(trainer, student, "train blacksmithing", _ => { });
        var offer = VendorTrainingEngine.FindOffer(trainer, student)!;
        var said = new List<string>();

        int keep = VendorTrainingEngine.OnTrainPay(trainer, student, offer, Gold(1000), said.Add);

        Assert.Equal(200, keep);
        Assert.Equal(300, student.GetSkill(SkillType.Blacksmithing));
        Assert.Equal(new[]
        {
            SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.NpcTrainerSuccess),
            SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.NpcTrainerThatsall2),
        }, said);
        Assert.Null(VendorTrainingEngine.FindOffer(trainer, student)); // the lesson is over
    }

    [Fact]
    public void TrainPay_LessThanTheFee_TrainsThatShareAndTakesTheGold()
    {
        var (trainer, student) = Pair(SkillType.Alchemy, 1000, 100); // teaches 200 for 200
        VendorTrainingEngine.OnTrainHear(trainer, student, "train alchemy", _ => { });
        var offer = VendorTrainingEngine.FindOffer(trainer, student)!;

        int keep = VendorTrainingEngine.OnTrainPay(trainer, student, offer, Gold(100), _ => { });

        Assert.Equal(100, keep);
        Assert.Equal(200, student.GetSkill(SkillType.Alchemy));
        Assert.NotNull(VendorTrainingEngine.FindOffer(trainer, student)); // more can be bought
    }

    [Fact]
    public void TrainPay_NothingLeftToTeach_TakesNothing()
    {
        var (trainer, student) = Pair(SkillType.Alchemy, 1000, 300);
        var memory = trainer.Memory_AddObjTypes(student.Uid, MemoryType.Speak);
        memory.More1 = ((uint)SkillType.Alchemy << 16) | VendorTrainingEngine.NpcMemActSpeakTrain;
        var said = new List<string>();

        Assert.Equal(0, VendorTrainingEngine.OnTrainPay(trainer, student, memory, Gold(100), said.Add));
        Assert.Equal(SphereNet.Game.Messages.ServerMessages.GetFormatted(
            SphereNet.Game.Messages.Msg.NpcTrainerDunno4, "Alchemy"), Assert.Single(said));
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
