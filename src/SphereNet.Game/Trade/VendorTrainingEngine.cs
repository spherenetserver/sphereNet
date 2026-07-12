using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;

namespace SphereNet.Game.Trade;

/// <summary>
/// NPC skill-training-for-pay — Source-X CChar::NPC_GetTrainMax /
/// NPC_OnTrainCheck / NPC_TrainSkill (CCharNPCAct_Vendor.cpp / CCharNPCStatus.cpp).
/// A teacher NPC can raise a student's skill up to a fraction of its own skill,
/// bounded by an absolute cap and the student's per-skill and total-skill caps;
/// going over the total cap drains the student's DOWN-locked skills. Cost is a
/// per-point multiplier paid in gold.
/// </summary>
public static class VendorTrainingEngine
{
    /// <summary>Trainer teaches up to this percent of its own skill (sphere.ini
    /// TRAINSKILLPERCENT).</summary>
    public static int TrainSkillPercent { get; set; } = 100;

    /// <summary>Absolute ceiling a trainer can raise any student to
    /// (TRAINSKILLMAX), in tenths — 300 = 30.0.</summary>
    public static int TrainSkillMax { get; set; } = 300;

    /// <summary>Gold charged per 0.1 skill point trained (TRAINSKILLCOST).</summary>
    public static int TrainSkillCost { get; set; } = 1;

    /// <summary>SphereNet skill-lock byte for a DOWN lock (0=up, 1=down, 2=locked).</summary>
    private const byte LockDown = 1;

    /// <summary>Highest value this <paramref name="trainer"/> can raise
    /// <paramref name="student"/> to in <paramref name="skill"/> — Source-X
    /// NPC_GetTrainMax. min(percent-of-trainer, absolute cap, student per-skill cap).</summary>
    public static int GetTrainMax(Character trainer, Character student, SkillType skill)
    {
        int byPercent = TrainSkillPercent * trainer.GetSkill(skill) / 100;
        int allowed = Math.Min(byPercent, TrainSkillMax);
        int studentCap = SkillEngine.GetSkillMax(student, skill);
        return Math.Min(allowed, studentCap);
    }

    /// <summary>How much of <paramref name="skill"/> the student can buy now —
    /// Source-X NPC_OnTrainCheck. The raw gap to the trainer's cap, throttled by
    /// how much total-skill headroom exists (DOWN-locked skills can be sacrificed
    /// to make room). 0 means "cannot train" (already past it, or no headroom).</summary>
    public static int CalcTrainableAmount(Character trainer, Character student, SkillType skill)
    {
        int trainVal = GetTrainMax(trainer, student, skill) - student.GetSkill(skill);
        if (trainVal <= 0)
            return 0;

        int sum = SkillEngine.GetSkillSum(student);
        int sumMax = SkillEngine.GetSkillSumMax(student);
        if (sum + trainVal <= sumMax)
            return trainVal;

        // Over the total cap: bounded by what the DOWN-locked skills can free.
        int freeable = SumDownLockedSkills(student);
        return Math.Min(trainVal, freeable);
    }

    /// <summary>Gold price for training <paramref name="amount"/> points.</summary>
    public static int TrainCost(int amount) => Math.Max(0, amount) * TrainSkillCost;

    /// <summary>Raise <paramref name="student"/>'s <paramref name="skill"/> by
    /// <paramref name="amount"/> — Source-X NPC_TrainSkill. When that would exceed
    /// the total-skill cap, drain DOWN-locked skills to make room first.</summary>
    public static void TrainSkill(Character student, SkillType skill, int amount)
    {
        if (amount <= 0)
            return;

        int sum = SkillEngine.GetSkillSum(student);
        int sumMax = SkillEngine.GetSkillSumMax(student);
        if (sum + amount > sumMax)
        {
            int need = amount;
            for (int i = 0; i < SkillEngine.BaseSkillCount && need > 0; i++)
            {
                var s = (SkillType)i;
                if (s == skill || student.GetSkillLock(s) != LockDown)
                    continue;
                int have = student.GetSkill(s);
                if (have >= need)
                {
                    student.SetSkill(s, (ushort)(have - need));
                    need = 0;
                }
                else
                {
                    student.SetSkill(s, 0);
                    need -= have;
                }
            }
        }

        int newVal = Math.Min(student.GetSkill(skill) + amount, ushort.MaxValue);
        student.SetSkill(skill, (ushort)newVal);
    }

    private static int SumDownLockedSkills(Character student)
    {
        int total = 0;
        for (int i = 0; i < SkillEngine.BaseSkillCount; i++)
        {
            if (student.GetSkillLock((SkillType)i) == LockDown)
                total += student.GetSkill((SkillType)i);
        }
        return total;
    }

    /// <summary>Tag key under which a teacher NPC records a student's pending
    /// training offer (skill id), keyed by the student's serial.</summary>
    public static string PendingTag(Character student) =>
        "TRAIN." + student.Uid.Value.ToString("X");

    /// <summary>Record a pending training offer on the teacher — Source-X
    /// NPC_OnTrainHear sets a MEMORY_SPEAK with NPC_MEM_ACT_SPEAK_TRAIN + skill.</summary>
    public static void RememberOffer(Character trainer, Character student, SkillType skill) =>
        trainer.SetTag(PendingTag(student), ((int)skill).ToString());

    /// <summary>
    /// Complete a pending training sale when a teacher is handed gold — Source-X
    /// NPC_OnTrainPay. Consumes up to the quoted cost, raises the student's skill
    /// by the points actually paid for, clears the offer, and returns the trained
    /// skill (or null when there was no matching offer / nothing to train).
    /// </summary>
    public static SkillType? TryPay(Character trainer, Character student, Item gold)
    {
        string tag = PendingTag(student);
        if (!trainer.TryGetTag(tag, out string? skillStr) || !int.TryParse(skillStr, out int skillId))
            return null;

        var skill = (SkillType)skillId;
        int trainable = CalcTrainableAmount(trainer, student, skill);
        if (trainable <= 0)
        {
            trainer.RemoveTag(tag);
            return null;
        }

        int fullCost = TrainCost(trainable);
        int paid = Math.Min(gold.Amount, fullCost);
        int pointsPaid = fullCost > 0 ? (int)((long)trainable * paid / fullCost) : 0;
        if (pointsPaid <= 0)
            return null;

        TrainSkill(student, skill, pointsPaid);

        if (paid >= gold.Amount)
            gold.RemoveFromWorld();
        else
            gold.Amount -= (ushort)paid;

        trainer.RemoveTag(tag);
        return skill;
    }
}
