using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Messages;
using SphereNet.Game.Skills;
using SphereNet.Core.Types;

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
    /// TRAINSKILLPERCENT; Source-X CServerConfig default 30).</summary>
    public static int TrainSkillPercent { get; set; } = 30;

    /// <summary>Absolute ceiling a trainer can raise any student to
    /// (TRAINSKILLMAX), in tenths — Source-X default 420 = 42.0.</summary>
    public static int TrainSkillMax { get; set; } = 420;

    /// <summary>Gold charged per 0.1 skill point trained (TRAINSKILLCOST).</summary>
    public static int TrainSkillCost { get; set; } = 1;

    /// <summary>SphereNet skill-lock byte for a DOWN lock (0=up, 1=down, 2=locked).</summary>
    private const byte LockDown = 1;

    /// <summary>Highest value this <paramref name="trainer"/> can raise
    /// <paramref name="student"/> to in <paramref name="skill"/> — Source-X
    /// NPC_GetTrainMax. min(percent-of-trainer, absolute cap, student per-skill cap).</summary>
    public static int GetTrainMax(Character trainer, Character student, SkillType skill)
    {
        // Both caps are overridable per trainer (NPC_GetTrainMax reads
        // OVERRIDE.TRAINSKILLMAXPERCENT and OVERRIDE.TRAINSKILLMAX off the char's key
        // chain, CCharNPCStatus.cpp:523/529). The reference pack's guildmasters set
        // both - TRAINSKILLMAX=50.0, TRAINSKILLMAXPERCENT=50 - and without them every
        // master trainer here taught to the shard default, 30% and 42.0.
        int percent = OverrideOr(trainer, "OVERRIDE.TRAINSKILLMAXPERCENT", TrainSkillPercent);
        int byPercent = percent * trainer.GetSkill(skill) / 100;
        int allowed = Math.Min(byPercent, OverrideOr(trainer, "OVERRIDE.TRAINSKILLMAX", TrainSkillMax));
        int studentCap = SkillEngine.GetSkillMax(student, skill);
        return Math.Min(allowed, studentCap);
    }

    /// <summary>A per-trainer OVERRIDE tag, else the shard setting. The value follows
    /// the script number convention, so a cap written as 50.0 is 500 tenths and one
    /// written as 500 is the same number.</summary>
    private static int OverrideOr(Character trainer, string tag, int fallback)
    {
        if (!trainer.TryGetTag(tag, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return fallback;
        int v = SphereNet.Scripting.Definitions.ValueCurve.ParseSphereNumber(raw);
        return v > 0 ? v : fallback;
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

    /// <summary>The same price with the trainer's own multiplier, if it carries one
    /// (OVERRIDE.TRAINSKILLCOST, CCharNPCAct_Vendor.cpp:286).</summary>
    public static int TrainCost(Character trainer, int amount) =>
        Math.Max(0, amount) * OverrideOr(trainer, "OVERRIDE.TRAINSKILLCOST", TrainSkillCost);

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
                    student.SetSkillRuntime(s, have - need);
                    need = 0;
                }
                else
                {
                    student.SetSkillRuntime(s, 0);
                    need -= have;
                }
            }
        }

        int newVal = Math.Min(student.GetSkill(skill) + amount, ushort.MaxValue);
        student.SetSkillRuntime(skill, newVal);
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

    /// <summary>NPC_MEM_ACT_SPEAK_TRAIN (CItem.h:54): the action a memory keeps in the
    /// low word of MORE1 once a trainer has named its price; the skill is the high word
    /// (m_itEqMemory.m_Action / m_Skill). The student's next gold is the fee.</summary>
    public const uint NpcMemActSpeakTrain = 1;

    /// <summary>The skill's script KEY (GetSkillKey): what a student names it by.</summary>
    public static string SkillKey(SkillType skill)
    {
        string? key = Definitions.DefinitionLoader.GetSkillDef((int)skill)?.DefName;
        return string.IsNullOrWhiteSpace(key) ? skill.ToString() : key;
    }

    /// <summary>FindStrWord: <paramref name="word"/> stands in <paramref name="text"/>
    /// as a whole word, case aside.</summary>
    private static bool HasWord(string text, string word)
    {
        int at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            int after = at + word.Length;
            if (before && (after >= text.Length || !char.IsLetterOrDigit(text[after])))
                return true;
            at++;
        }
        return false;
    }

    private static bool IsTrainableSkill(SkillType skill) =>
        (int)skill >= 0 && (int)skill < SkillEngine.BaseSkillCount &&
        Definitions.DefinitionLoader.GetSkillDef((int)skill) != null;

    /// <summary>NPC_OnTrainCheck (CCharNPCAct_Vendor.cpp:212): how much of the skill
    /// this trainer will teach the student now, or 0 after saying why not.</summary>
    public static int OnTrainCheck(Character trainer, Character student, SkillType skill, Action<string> speak)
    {
        if ((int)skill < 0 || (int)skill >= SkillEngine.BaseSkillCount)
        {
            speak(ServerMessages.Get(Msg.NpcTrainerDunno1));
            return 0;
        }
        int amount = CalcTrainableAmount(trainer, student, skill);
        int trainerVal = trainer.GetSkill(skill);
        string? why = trainerVal <= 0 ? Msg.NpcTrainerDunno2
            : student.GetSkill(skill) > trainerVal ? Msg.NpcTrainerDunno3
            : amount <= 0 ? Msg.NpcTrainerDunno4
            : null;
        if (why == null)
            return amount;
        speak(ServerMessages.GetFormatted(why, SkillKey(skill)));
        return 0;
    }

    /// <summary>NPC_OnTrainHear (CCharNPCAct_Vendor.cpp:371), the TRAIN verb a pack's
    /// "train" speech calls with the whole line: a skill named in it as a word gets its
    /// price and the trainer remembers the offer; otherwise the trainer lists what it
    /// can teach. Answers whether this NPC handled the request.</summary>
    public static bool OnTrainHear(Character trainer, Character student, string text, Action<string> speak)
    {
        int brain = NpcBrainNames.ToScriptNumber(trainer.NpcBrain);
        if (brain < NpcBrainNames.ToScriptNumber(NpcBrainType.Human) ||
            brain > NpcBrainNames.ToScriptNumber(NpcBrainType.Stable) ||
            trainer.NpcBrain == NpcBrainType.Guard)
            return false;

        if (trainer.Memory_FindObjTypes(student.Uid,
                MemoryType.Fight | MemoryType.HarmedBy | MemoryType.IrritatedBy | MemoryType.Aggreived) != null)
        {
            speak(ServerMessages.Get(Msg.NpcTrainerEnemy));
            return true;
        }

        for (int i = 0; i < SkillEngine.BaseSkillCount; i++)
        {
            var skill = (SkillType)i;
            if (!IsTrainableSkill(skill) || !HasWord(text, SkillKey(skill)))
                continue;
            int cost = OnTrainCheck(trainer, student, skill, speak) * TrainSkillCost;
            if (cost <= 0)
                return true;
            speak(ServerMessages.GetFormatted(Msg.NpcTrainerPrice, cost, SkillKey(skill)));
            var memory = trainer.Memory_AddObjTypes(student.Uid, MemoryType.Speak);
            memory.More1 = ((uint)i << 16) | NpcMemActSpeakTrain;
            return true;
        }

        // What can it teach? The first six by name, then "more" (the list builds
        // one name behind, joining with ", " and a final " and ").
        var line = new System.Text.StringBuilder(ServerMessages.Get(Msg.NpcTrainerPrice1));
        string? previous = null;
        int count = 0;
        for (int i = 0; i < SkillEngine.BaseSkillCount; i++)
        {
            var skill = (SkillType)i;
            if (!IsTrainableSkill(skill) || GetTrainMax(trainer, student, skill) - student.GetSkill(skill) <= 0)
                continue;
            if (count > 6)
            {
                previous = ServerMessages.Get(Msg.NpcTrainerPrice2);
                break;
            }
            if (count > 1)
                line.Append(ServerMessages.Get(Msg.NpcTrainerPrice3));
            if (previous != null)
                line.Append(previous);
            previous = SkillKey(skill);
            count++;
        }
        if (count == 0)
        {
            speak(ServerMessages.Get(Msg.NpcTrainerThatsall3));
            return true;
        }
        if (count > 1)
            line.Append(ServerMessages.Get(Msg.NpcTrainerThatsall4));
        line.Append(previous).Append('.');
        speak(line.ToString());
        return true;
    }

    /// <summary>The trainer's offer to this student, when its last word to them was a
    /// price (NPC_OnItemGive routes gold there, CCharNPCAct.cpp:2081).</summary>
    public static Item? FindOffer(Character trainer, Character student)
    {
        var memory = trainer.Memory_FindObj(student.Uid);
        return memory != null && (memory.More1 & 0xFFFF) == NpcMemActSpeakTrain ? memory : null;
    }

    /// <summary>NPC_OnTrainPay (CCharNPCAct_Vendor.cpp:273): gold handed over after
    /// the price. The fee is the teachable amount times the cost; a smaller stack buys
    /// that share of it, an exact one finishes the lesson, a larger one has its
    /// change returned. Answers how much of the stack the trainer keeps, 0 when it
    /// takes nothing (the gold then goes back).</summary>
    public static int OnTrainPay(Character trainer, Character student, Item memory, Item gold, Action<string> speak)
    {
        var skill = (SkillType)(memory.More1 >> 16);
        if (!IsTrainableSkill(skill))
        {
            speak(ServerMessages.Get(Msg.NpcTrainerForgot));
            return 0;
        }

        int amount = OnTrainCheck(trainer, student, skill, speak);
        int cost = Math.Min(ushort.MaxValue, TrainCost(trainer, amount));
        if (cost <= 0)
            return 0;

        speak(ServerMessages.Get(Msg.NpcTrainerSuccess));
        int keep = cost;
        if (gold.Amount < cost)
        {
            int diffPercent = cost * 100 / gold.Amount;
            amount = amount * 100 / diffPercent;
            keep = gold.Amount;
        }
        else
        {
            speak(ServerMessages.Get(gold.Amount == cost ? Msg.NpcTrainerThatsall1 : Msg.NpcTrainerThatsall2));
            memory.More1 &= 0xFFFF0000;
        }

        TrainSkill(student, skill, amount);
        return keep;
    }
}
