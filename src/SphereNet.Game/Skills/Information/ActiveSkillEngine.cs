using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Skills.Information;

/// <summary>
/// Source-X parity for the "active" skills dispatched by CChar::Skill_*.
/// Every entry point folds the upstream stage machine
/// (SKTRIG_START → STROKE → SUCCESS/FAIL) into a single synchronous call:
///
///   1. Pre-check phase (target validity, tool present, range).
///   2. START emote / SysMessage.
///   3. Roll difficulty + <see cref="SkillEngine.UseQuick"/>.
///   4. SUCCESS or FAIL message + side effects (consume bandage, set Hidden,
///      push criminal flag, ...).
///
/// Side effects use only <see cref="IActiveSkillSink"/>, which keeps the
/// engine network-free and unit-testable. The engine itself is deliberately
/// stateless: it resolves the SUCCESS/FAIL outcome in one call and holds no
/// per-tick state. The Source-X per-tick STROKE loop is layered on top by the
/// client (GameClient.TickPendingSkill / TryScheduleActiveSkillDelay), which,
/// for skills with a DELAY, fires @SkillStroke on an interval and then invokes
/// these methods at completion. Ordering and interrupt behaviour of that loop
/// are locked by ActiveSkillStrokeMatrixTests / SkillDelayTests.
/// </summary>
public static class ActiveSkillEngine
{
    // ---------------------------------------------------------------- Hiding

    /// <summary>Skill_Hiding START (CCharSkill.cpp:2500-2516): nothing worn on a visible
    /// layer may be a light (DEFMSG_HIDING_TOOLIT); the difficulty is rand(70). No
    /// war-mode refusal. Run when the skill starts.</summary>
    public static int HidingStart(IActiveSkillSink sink)
    {
        if (IsCarryingLight(sink.Self, sink.World))
        {
            sink.SysMessage(ServerMessages.Get(Msg.HidingToolit));
            return -3;
        }
        return sink.Random.Next(70);
    }

    /// <summary>Source-X CChar::Skill_Hiding (CCharSkill.cpp:2468-2518). START is
    /// <see cref="HidingStart"/> - the light was judged after the DELAY. SUCCESS hides
    /// (DEFMSG_HIDING_SUCCESS over the hider) and drops an invisibility; FAIL is
    /// Reveal(), with its @Reveal and its hooks - the flags used to be cleared
    /// directly.</summary>
    public static bool Hiding(IActiveSkillSink sink, int? startDifficulty = null)
    {
        var ch = sink.Self;
        int diff = startDifficulty ?? HidingStart(sink);
        if (diff < -1)
            return false;
        bool success = diff >= 0 && SkillEngine.UseQuick(ch, SkillType.Hiding, diff);
        if (success)
        {
            ch.SetStatFlag(StatFlag.Hidden);
            ch.ClearStatFlag(StatFlag.Invisible);
            sink.ObjectMessage(ch, ServerMessages.Get(Msg.HidingSuccess));
        }
        else
        {
            // DEFMSG_HIDING_STUMBLE belongs to walking into a hidden char, not here.
            ch.ClearHiddenState();
        }
        return success;
    }

    /// <summary>Is anything equipped on a visible layer CAN_I_LIGHT? The flag is the
    /// item definition's CAN plus the tile data's light-source bit
    /// (CItemBase::GetItemTiledataFlags, CItemBase.cpp:730), so a lit torch - whose
    /// graphic is the lit one - counts and an unlit one does not.</summary>
    internal static bool IsCarryingLight(Character ch, World.GameWorld? world)
    {
        for (var layer = Layer.None + 1; layer <= Layer.Horse; layer++)
        {
            var worn = ch.GetEquippedItem(layer);
            if (worn == null || !Item.IsVisibleLayer(layer))
                continue;
            var def = Definitions.DefinitionLoader.GetItemDef(
                Definitions.ItemDefHelper.ResolveInstanceDefIndex(worn));
            ulong can = (ulong)(def?.Can ?? CanFlags.None);
            var md = world?.MapData;
            if (md != null &&
                (md.GetItemTileData(worn.DispIdFull).Flags & SphereNet.MapData.Tiles.TileFlag.LightSource) != 0)
                can |= (ulong)CanFlags.I_Light;
            // CANMASK is XORed over the definition (GetCanFlags, CObjBase.h:153).
            if (((can ^ worn.CanMask) & (ulong)CanFlags.I_Light) != 0)
                return true;
        }
        return false;
    }

    // --------------------------------------------------------------- Stealth

    /// <summary>Source-X SKILL_STEALTH has no engine stage at all: Skill_Stage returns
    /// 0 for every stage (CCharSkill.cpp:3671-3675), so the difficulty is 0 and the
    /// engine sets nothing. The pack owns the rest - [SKILL 47] @PreStart refuses an
    /// unhidden user, @Success sets STEPSTEALTH and @Fail REVEALs
    /// (skills/skill47_stealth.scp); those fire from the [SKILL] section after this
    /// result. The hidden gate, the step budget and the English lines this had were
    /// invented.</summary>
    public static bool Stealth(IActiveSkillSink sink)
        => SkillEngine.UseQuick(sink.Self, SkillType.Stealth, 0);

    // ---------------------------------------------------------- DetectHidden

    /// <summary>Source-X CChar::Skill_DetectHidden (CCharSkill.cpp:1700-1755): START is a
    /// difficulty of 10. SUCCESS looks around (radius ACTEFFECT, else skill/100) and
    /// reveals whoever loses the contest; finding nobody is a FAIL. With
    /// REVEALF_DETECTINGHIDDEN off the skill succeeds and does nothing (:1716) - it was
    /// a failure.</summary>
    public static bool DetectHidden(IActiveSkillSink sink, int? startDifficulty = null)
    {
        var ch = sink.Self;
        int diff = startDifficulty ?? 10;
        bool succeeded = diff >= 0 && SkillEngine.UseQuick(ch, SkillType.DetectingHidden, diff);
        if (!succeeded) return false;
        if ((Character.ActiveRevealFlags & RevealFlags.DetectingHidden) == 0)
            return true;

        int detectSkill = SkillEngine.GetAdjustedSkill(ch, SkillType.DetectingHidden);
        int radius = Math.Max(0, SkillEngine.GetEffect(SkillType.DetectingHidden,
            detectSkill, detectSkill / 100));
        bool found = false;
        foreach (var nearby in sink.World.GetCharsInRange(ch.Position, radius))
        {
            if (nearby == ch || !nearby.IsStatFlag(StatFlag.Hidden | StatFlag.Invisible)) continue;
            int sourceRoll = detectSkill + sink.Random.Next(210) - 100;
            int targetRoll = SkillEngine.GetAdjustedSkill(nearby, SkillType.Hiding) +
                sink.Random.Next(210) - 100;
            if (sourceRoll >= targetRoll && nearby.ClearHiddenState(RevealFlags.DetectingHidden))
            {
                // Source-X SysMessagef(DEFMSG_DETECTHIDDEN_SUCC, name) — CCharSkill.cpp:1746
                sink.SysMessage(ServerMessages.GetFormatted(Msg.DetecthiddenSucc, nearby.Name));
                found = true;
            }
        }
        return found;
    }

    // ----------------------------------------------------------- Meditation

    /// <summary>Skill_Meditation START (CCharSkill.cpp:2673-2682): at full mana it is at
    /// peace (DEFMSG_MEDITATION_PEACE_1); otherwise DEFMSG_MEDITATION_TRY and a difficulty
    /// of rand(100). Run when the skill starts - the try line used to wait for the
    /// DELAY.</summary>
    public static int MeditationStart(IActiveSkillSink sink)
    {
        var ch = sink.Self;
        if (ch.Mana >= ch.MaxMana)
        {
            sink.SysMessage(ServerMessages.Get(Msg.MeditationPeace1));
            return -3;
        }
        sink.SysMessage(ServerMessages.Get(Msg.MeditationTry));
        return sink.Random.Next(100);
    }

    /// <summary>Source-X CChar::Skill_Meditation. STAT_INT cap rejects, success regens mana.</summary>
    public static bool Meditation(IActiveSkillSink sink, int? startDifficulty = null)
    {
        var ch = sink.Self;
        // A START that came back negative is bound to fail (Skill_Done, :3928): the FAIL
        // stage, which says nothing - the peace line was said again here.
        if (startDifficulty is < 0)
            return false;
        bool first = !ch.IsStatFlag(StatFlag.Meditation);
        if (first && !startDifficulty.HasValue && ch.Mana >= ch.MaxMana)
        {
            // Called with no START behind it: START's own refusal.
            sink.SysMessage(ServerMessages.Get(Msg.MeditationPeace1));
            return false;
        }
        if (!first && ch.Mana >= ch.MaxMana)
        {
            // SUCCESS at full mana: at peace, and only now the skill credit (:2690-2697).
            ch.ClearStatFlag(StatFlag.Meditation);
            sink.SysMessage(ServerMessages.Get(Msg.MeditationPeace2));
            SkillEngine.GainExperience(ch, SkillType.Meditation, ch.ActDiff);
            return true;
        }

        if (first)
        {
            if (startDifficulty.HasValue)
                ch.ActDiff = startDifficulty.Value;
            else
            {
                sink.SysMessage(ServerMessages.Get(Msg.MeditationTry));
                ch.ActDiff = sink.Random.Next(100);
            }
            if (!SkillEngine.UseQuick(ch, SkillType.Meditation, ch.ActDiff, allowGain: false))
            {
                SkillEngine.GainExperience(ch, SkillType.Meditation, -ch.ActDiff);
                return false;
            }
            // The first SUCCESS may find the mana full already - a pack's @Start that set
            // ACTDIFF back to 0 over the peace refusal lets it get here.
            if (ch.Mana >= ch.MaxMana)
            {
                sink.SysMessage(ServerMessages.Get(Msg.MeditationPeace2));
                SkillEngine.GainExperience(ch, SkillType.Meditation, ch.ActDiff);
                return true;
            }
            ch.SetStatFlag(StatFlag.Meditation);
            if (!SkillEngine.HasFlag(SkillType.Meditation, SkillFlag.NoSfx))
                sink.Sound(0x0F9);
            sink.SysMessage(ServerMessages.Get(Msg.MeditationSuccess));
        }
        // Source-X Skill_Meditation SUCCESS: one application per skill DELAY,
        // independent of passive REGEN1. ACTEFFECT may explicitly be zero.
        int amount = ch.ActionEffect >= 0 ? ch.ActionEffect :
            SkillEngine.GetEffect(SkillType.Meditation, ch.GetSkill(SkillType.Meditation), 1);
        ch.Mana = (short)Math.Min(ch.MaxMana, (long)ch.Mana + Math.Max(0, amount));
        return true;
    }

    // ---------------------------------------------------------- SpiritSpeak

    /// <summary>Source-X CChar::Skill_SpiritSpeak (CCharSkill.cpp:2623-2650): START is a
    /// difficulty of rand(90), set when the skill starts; SUCCESS sets STATF_SPIRITSPEAK
    /// for 24 seconds with sound 0x24A.</summary>
    public static bool SpiritSpeak(IActiveSkillSink sink, int? startDifficulty = null)
    {
        var ch = sink.Self;
        if (ch.IsStatFlag(StatFlag.SpiritSpeak)) return false;

        int diff = startDifficulty ?? sink.Random.Next(90);
        bool success = diff >= 0 && SkillEngine.UseQuick(ch, SkillType.SpiritSpeak, diff);
        if (success)
        {
            if (!SkillEngine.HasFlag(SkillType.SpiritSpeak, SkillFlag.NoSfx))
                sink.Sound(0x24A);
            sink.SysMessage(ServerMessages.Get(Msg.SpiritspeakSuccess));
            ch.SetStatFlag(StatFlag.SpiritSpeak);
            ch.SetTag("SPIRITSPEAK_UNTIL", // 4*60 tenths = 24s (Source-X CCharSkill.cpp:2644)
                (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 24_000).ToString());
        }
        return success;
    }

    // -------------------------------------------------------------- Begging

    /// <summary>Source-X CChar::Skill_Begging (CCharSkill.cpp:2929-2962): any
    /// character but the beggar may be targeted; the difficulty is the target's
    /// adjusted INT (:2946) and SUCCESS does nothing (:2958 - "Not sure how to make
    /// begging successful"). The human-brain gate and the 1-10 gold reward were
    /// invented; a pack that wants a reward scripts it on @Success.</summary>
    public static bool Begging(IActiveSkillSink sink, Character? target)
    {
        var ch = sink.Self;
        // Skill_Begging asks only for a mark that is not the beggar (CCharSkill.cpp:2935);
        // a 3-tile reach was invented. Its START line is said when the skill starts
        // (BeggingStartLine), not after the DELAY.
        if (target == null || target == ch || target.IsDeleted)
            return false;
        return SkillEngine.UseQuick(ch, SkillType.Begging, Combat.CombatEngine.EffectiveInt(target));
    }

    /// <summary>Skill_Begging's START line (CCharSkill.cpp:2942-2946): "You grovel at
    /// %s's feet", or null when there is no mark to beg from.</summary>
    public static string? BeggingStartLine(Character self, ObjBase? target) =>
        target is Character mark && mark != self && !mark.IsDeleted
            ? ServerMessages.GetFormatted(Msg.BeggingStart, mark.GetName())
            : null;

    // ------------------------------------------------------------- Stealing

    /// <summary>What every Skill_Stealing stage asks (CCharSkill.cpp:4190-4320): the
    /// item - a character picked means a random thing from their pack
    /// (DEFMSG_STEALING_NOTHING / EMPTY), which becomes ACT; not from or of a corpse
    /// (CORPSE), a game board (GAMEBOARD) or a trade window (TRADE); not a pickpocket
    /// dip (PICKPOCKET) or a game piece; within touch (REACH); one that can be moved and
    /// carried (HEAVY - a newbie, blessed or cursed thing on someone else among them);
    /// a crime at all (NONEED) and not in a safe area (STOP). From a mark: within the
    /// skill's RANGE, 2 when it names none, in 3D (MARK); no player in a no-PvP area
    /// (SAFE); never a higher rank (bound to fail). Answers below 0 to stop, else 0,
    /// with the item, its mark (null off the ground) and whether it is bound to fail.</summary>
    private static int StealingTarget(IActiveSkillSink sink, ObjBase? act, out Item? item,
        out Character? mark, out bool boundToFail)
    {
        var ch = sink.Self;
        item = act as Item;
        mark = null;
        boundToFail = false;
        if (item == null)
        {
            if (act is not Character picked)
            {
                sink.SysMessage(ServerMessages.Get(Msg.StealingNothing));
                return -3;
            }
            // The item a START already chose stays the one (m_Act_UID = pItem).
            if (ch.Act.IsValid && sink.World.FindItem(ch.Act) is { } chosen &&
                chosen.GetTopLevelObj() == picked && chosen.ContainedIn == picked.Backpack?.Uid)
                item = chosen;
            else
            {
                var contents = picked.Backpack?.Contents.Where(i => !i.IsDeleted).ToList();
                if (contents == null || contents.Count == 0)
                {
                    sink.SysMessage(ServerMessages.Get(Msg.StealingEmpty));
                    return -3;
                }
                item = contents[sink.Random.Next(contents.Count)];
                ch.Act = item.Uid;
            }
        }
        var holder = item.ContainedIn.IsValid ? sink.World.FindItem(item.ContainedIn) : null;
        if (holder?.ItemType == ItemType.Corpse || item.ItemType == ItemType.Corpse)
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingCorpse));
            return -3;
        }
        if (holder?.ItemType == ItemType.GameBoard)
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingGameboard));
            return -3;
        }
        if (holder?.ItemType == ItemType.EqTradeWindow)
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingTrade));
            return -3;
        }
        if (item.ItemType == ItemType.TrainPickpocket)
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingPickpocket));
            return -3;
        }
        if (item.ItemType == ItemType.GamePiece)
            return -3;
        if (!Scripting.ScriptTouchAccess.CanTouch(ch, item))
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingReach));
            return -3;
        }
        // CanMoveItem refuses a newbie, blessed or cursed thing on another character
        // (CCharStatus.cpp, "Can't steal/move newbie items on another cchar").
        bool guarded = (item.IsAttr(ObjAttributes.Newbie) || item.IsAttr(ObjAttributes.Blessed2) ||
                        item.IsAttr(ObjAttributes.Cursed) || item.IsAttr(ObjAttributes.Cursed2)) &&
                       item.GetTopLevelObj() is Character other && other != ch;
        if (guarded || !ItemMoveRules.CanMove(ch, item, out _) || !ch.CanCarry(item))
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingHeavy));
            return -3;
        }
        if (!IsTakeCrime(ch, item, out mark))
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingNoneed));
            return -3;
        }
        if (sink.World.FindRegion(ch.Position)?.IsFlag(RegionFlag.Safe) == true)
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingStop));
            return -3;
        }
        if (mark != null)
        {
            int range = SkillEngine.GetUseRange(SkillType.Stealing, 2);
            var me = ch.GetTopLevelObj().Position;
            var him = mark.GetTopLevelObj().Position;
            int dist = me.Map != him.Map ? int.MaxValue
                : Math.Max(Math.Max(Math.Abs(me.X - him.X), Math.Abs(me.Y - him.Y)), Math.Abs(me.Z - him.Z) / 8);
            if (dist > range)
            {
                sink.SysMessage(ServerMessages.Get(Msg.StealingMark));
                return -3;
            }
            if (sink.World.FindRegion(ch.Position)?.IsFlag(RegionFlag.NoPvP) == true && mark.IsPlayer &&
                !ch.IsGmMode)
            {
                sink.SysMessage(ServerMessages.Get(Msg.StealingSafe));
                return -1;
            }
            if (ch.PrivLevel < mark.PrivLevel)
                boundToFail = true;
        }
        return 0;
    }

    /// <summary>Skill_Stealing START (CCharSkill.cpp:4307/4314): from a mark,
    /// Calc_StealingItem; off the ground 1, "town stuff on the ground is too easy".
    /// Run when the skill starts.</summary>
    public static int StealingStart(IActiveSkillSink sink, ObjBase? act)
    {
        int code = StealingTarget(sink, act, out var item, out var mark, out bool boundToFail);
        if (code < 0)
            return code;
        if (boundToFail)
            return -1;
        return mark != null ? CalcStealingItem(sink.Self, item!, mark, sink.Random) : 1;
    }

    /// <summary>Source-X CChar::Skill_Stealing (CCharSkill.cpp:4180-4345). START is
    /// <see cref="StealingStart"/>; after the DELAY the target is judged again. The
    /// goods are delivered on SUCCESS, and always off the ground; REVEALFLAGS decides
    /// whether the thief is revealed; a theft of no difficulty is no crime, otherwise
    /// the onlookers may notice it (DEFMSG_STEALING_YOUR / SOMEONE), which costs 100
    /// karma down to -1000.</summary>
    public static bool Stealing(IActiveSkillSink sink, ObjBase? act, int? startDifficulty = null)
    {
        var ch = sink.Self;
        int stealDiff;
        if (startDifficulty.HasValue)
            stealDiff = startDifficulty.Value;
        else
        {
            stealDiff = StealingStart(sink, act);
            if (stealDiff < -1)
                return false;
        }
        if (StealingTarget(sink, act, out var target, out var owner, out bool boundToFail) < -1)
            return false;
        if (boundToFail)
            stealDiff = -1;
        bool success = stealDiff >= 0 && SkillEngine.UseQuick(ch, SkillType.Stealing, stealDiff);

        // Deliver the goods (CCharSkill.cpp:4321-4331): on SUCCESS, and ALWAYS for an
        // item taken off the ground - "stealing off the ground should always
        // succeed, it's just a matter of getting caught" (:4311-4318). The item
        // loses ATTR_OWNED ("Now it's mine") and goes into the thief's pack.
        bool fromGround = owner == null;
        if ((success || fromGround) && ch.Backpack != null)
        {
            Item? sourceContainer = null;
            if (target!.ContainedIn.IsValid)
            {
                // The insert into the thief's pack takes the item off every screen
                // first (CItemContainer::OnInsert -> RemoveFromView, CItemContainer.cpp:727)
                // and then redraws it where it now is. Without the removal the mark's
                // open pack kept showing the stolen item until it was reopened.
                Objects.ObjBase.BroadcastNearby?.Invoke(ch.Position, Clients.GameClient.UpdateRange,
                    new Network.Packets.Outgoing.PacketDeleteObject(target.Uid.Value), 0);
                sourceContainer = sink.World.FindItem(target.ContainedIn);
                sourceContainer?.RemoveItem(target);
            }

            target.ClearAttr(ObjAttributes.Owned);
            var actual = ch.Backpack.TryAddItemWithStack(target);
            if (actual == null)
            {
                if (sourceContainer?.TryAddItem(target) != true)
                    sink.World.PlaceItemWithDecay(target, owner?.Position ?? ch.Position);
                success = false;
            }
            else if (actual != target)
            {
                sink.World.RemoveItem(target);
            }
        }
        else if (success && ch.Backpack == null)
        {
            // No backpack = nowhere to stash the loot, so the theft can't succeed.
            success = false;
        }

        // A theft gives the thief away, and which outcome does it is the shard's
        // choice: REVEALF_STEALING_SUCCESS and REVEALF_STEALING_FAIL are separate
        // flags (CCharSkill.cpp:4333).
        ch.ClearHiddenState(success ? RevealFlags.StealingSuccess : RevealFlags.StealingFail);

        // "Too easy to be bad" (CCharSkill.cpp:4338): a zero-difficulty theft is
        // never a crime.
        if (stealDiff == 0)
            return success;

        // Witnesses are told "stealing" for a theft that worked, "attempting to steal"
        // for one that did not; a theft someone saw costs Noto_Karma(-100) down to
        // -1000 (CCharSkill.cpp:4342-4343).
        if (CrimeWitnessService.CheckCrimeSeen(sink.World, ch, owner, SkillType.Stealing, sink.Random,
                item: target, action: ServerMessages.Get(success ? Msg.StealingSomeone : Msg.StealingYour)))
            ApplySkillKarma(ch, -100, -1000, sink.SysMessage);

        return success;
    }

    /// <summary>Source-X CServerConfig::Calc_StealingItem (CResourceCalc.cpp:431-446,
    /// the "Melt mod"): the mark's stealing/5 + rand(mark dex/2) + the item's weight
    /// (IMulDiv(weight, 4, WEIGHT_UNITS)); an equipped item adds the mark's dex/2 and
    /// int, a thief in war mode another rand(dex/2); the whole is halved.</summary>
    internal static int CalcStealingItem(Character thief, Item item, Character mark, Random random)
    {
        int dexMark = Combat.CombatEngine.EffectiveDex(mark);
        int skillMark = SkillEngine.GetAdjustedSkill(mark, SkillType.Stealing);
        int diff = skillMark / 5 + RandVal(random, dexMark / 2) +
            InfoSkillEngine.IMulDiv(item.TotalWeightTenths, 4, Item.WeightUnits);
        if (item.IsEquipped)
            diff += dexMark / 2 + Combat.CombatEngine.EffectiveInt(mark);
        if (thief.IsStatFlag(StatFlag.War))
            diff += RandVal(random, dexMark / 2);
        return diff / 2;
    }

    /// <summary>CSRand::GetVal: 0..n-1, and 0 for anything below 2 (CSRand.cpp:40).</summary>
    private static int RandVal(Random random, int n) => n < 2 ? 0 : random.Next(n);

    /// <summary>Source-X CChar::Noto_Karma (CCharNotoriety.cpp:512) for a skill's
    /// karma cost: scaled by Calc_KarmaScale (CResourceCalc.cpp:388 - a good
    /// character loses twice as fast, gains half, and a gain under karma/64 is
    /// nothing), bounded by the karma limits (a loss by <paramref name="bottom"/>,
    /// MINKARMA when unset), then offered to @KarmaChange. The change actually
    /// applied is reported through Noto_ChangeDeltaMsg (CCharNotoriety.cpp:547) to
    /// <paramref name="message"/>. (The Noto_ChangeNewMsg title line that follows is
    /// handed the CURRENT level, so upstream never prints it - :551/468.)</summary>
    internal static void ApplySkillKarma(Character ch, int change, int bottom = int.MinValue,
        Action<string>? message = null)
    {
        int karma = ch.Karma;
        if (karma > 0)
            change = change < 0 ? change * 2 : change / 2;
        if (change > 0 && change < karma / 64)
            change = 0;
        if (change > 0)
            change = Math.Min(change, Death.DeathEngine.MaxKarma - karma);
        else
        {
            // Upstream sets the change to reach the bottom whenever it would pass it
            // (CCharNotoriety.cpp:526-529) - literally, so a karma already under a
            // custom bottom is raised to it.
            if (bottom == int.MinValue)
                bottom = Death.DeathEngine.MinKarma;
            if (karma + change < bottom)
                change = bottom - karma;
        }
        if (Character.OnKarmaChanging != null)
        {
            int? adjusted = Character.OnKarmaChanging(ch, change);
            if (adjusted == null) return;
            change = adjusted.Value;
        }
        ch.Karma = (short)Math.Clamp(karma + change, Death.DeathEngine.MinKarma, Death.DeathEngine.MaxKarma);
        if (message != null && NotoChangeDeltaMessage(ch.Karma - karma, ServerMessages.Get(Msg.NotoKarma)) is { } text)
            message(text);
    }

    /// <summary>The Noto_ChangeDeltaMsg degree words (CCharNotoriety.cpp:443-453).</summary>
    private static readonly string[] NotoDegreeTable =
    {
        Msg.MsgNotoChange1, Msg.MsgNotoChange2, Msg.MsgNotoChange3, Msg.MsgNotoChange4,
        Msg.MsgNotoChange5, Msg.MsgNotoChange6, Msg.MsgNotoChange7, Msg.MsgNotoChange8,
    };

    /// <summary>Source-X CChar::Noto_ChangeDeltaMsg (CCharNotoriety.cpp:434-463):
    /// "You have gained/lost [degree] [karma]." with the degree
    /// min(|delta| / (300/8), 7). Null for no change.</summary>
    internal static string? NotoChangeDeltaMessage(int delta, string type)
    {
        if (delta == 0)
            return null;
        int degree = Math.Min(Math.Abs(delta) / (300 / 8), 7);
        return ServerMessages.GetFormatted(Msg.MsgNotoChange0,
            ServerMessages.Get(delta < 0 ? Msg.MsgNotoChangeLost : Msg.MsgNotoChangeGain),
            ServerMessages.Get(NotoDegreeTable[degree]), type);
    }

    // -------------------------------------------------------------- Snooping

    /// <summary>CChar::IsTakeCrime (CCharAct.cpp): whether laying hands on this item
    /// wrongs someone, and who - the character it is on, else null. Staff in GM or
    /// ALLMOVE mode commit none; one's own things, a pet's or a friend's (MEMORY_FRIEND)
    /// and an unowned pack animal's are no crime; a loose item is one when it is OWNED
    /// by someone else or lies in an OWNED container.</summary>
    public static bool IsTakeCrime(Character ch, Item item, out Character? mark)
    {
        var top = item.GetTopLevelObj();
        mark = top as Character;
        if (ch.IsGmMode || ch.AllMove)
            return false;
        if (top == ch)
            return false;
        if (mark == null)
        {
            if (item.IsAttr(ObjAttributes.Owned) && item.Link != ch.Uid)
                return true;
            return top is Item container && container != item && container.IsAttr(ObjAttributes.Owned);
        }
        if (mark.HasOwner(ch.Uid) || mark.Memory_FindObjTypes(ch.Uid, MemoryType.Friend) != null)
            return false;
        if (!mark.IsPlayer && mark.NpcBrain == NpcBrainType.Animal && !mark.IsStatFlag(StatFlag.Pet))
            return false;
        return true;
    }

    /// <summary>What every Skill_Snooping stage asks (CCharSkill.cpp:4119-4148): a
    /// container; whose (no crime, no mark - nothing more to it); the mark within the
    /// skill's RANGE, 1 when it names none, in 3D (DEFMSG_SNOOPING_REACH); the container
    /// within touch (DEFMSG_SNOOPING_CANT). Null mark with true = not a crime.</summary>
    private static bool SnoopingTarget(IActiveSkillSink sink, Item? container, out Character? mark)
    {
        var ch = sink.Self;
        mark = null;
        if (container == null || container.IsDeleted ||
            container.ItemType is not (ItemType.Container or ItemType.ContainerLocked or ItemType.Corpse
                or ItemType.EqVendorBox or ItemType.EqBankBox or ItemType.ShipHold or ItemType.ShipHoldLock))
            return false;
        if (!IsTakeCrime(ch, container, out mark) || mark == null)
        {
            mark = null;
            return true;
        }
        int range = SkillEngine.GetUseRange(SkillType.Snooping, 1);
        var me = ch.GetTopLevelObj().Position;
        var him = mark.GetTopLevelObj().Position;
        int dist = me.Map != him.Map ? int.MaxValue
            : Math.Max(Math.Max(Math.Abs(me.X - him.X), Math.Abs(me.Y - him.Y)), Math.Abs(me.Z - him.Z) / 8);
        if (dist > range)
        {
            sink.SysMessage(ServerMessages.Get(Msg.SnoopingReach));
            return false;
        }
        if (!Scripting.ScriptTouchAccess.CanTouch(ch, container))
        {
            sink.SysMessage(ServerMessages.Get(Msg.SnoopingCant));
            return false;
        }
        return true;
    }

    /// <summary>Skill_Snooping START (CCharSkill.cpp:4150-4158): nobody snoops a higher
    /// rank (DEFMSG_SNOOPING_CANT); the difficulty is all or nothing - 100 when the
    /// adjusted skill loses to rand(1000), else 0. Run when the skill starts.</summary>
    public static int SnoopingStart(IActiveSkillSink sink, Item? container)
    {
        var ch = sink.Self;
        if (!SnoopingTarget(sink, container, out var mark))
            return -3;
        if (mark == null)
            return 0;
        if (ch.PrivLevel < mark.PrivLevel)
        {
            sink.SysMessage(ServerMessages.Get(Msg.SnoopingCant));
            return -3;
        }
        return SkillEngine.GetAdjustedSkill(ch, SkillType.Snooping) < sink.Random.Next(1000) ? 100 : 0;
    }

    /// <summary>Source-X CChar::Skill_Snooping (CCharSkill.cpp:4108-4177), started by
    /// opening someone's container (Skill_Snoop_Check). START is
    /// <see cref="SnoopingStart"/>; after the DELAY the target is judged again, the
    /// onlookers may notice (CheckCrimeSeen, DEFMSG_SNOOPING_ATTEMPTING) and the snooper
    /// loses 4 karma, success or not; FAIL says DEFMSG_SNOOPING_FAILED and gives the
    /// snooper away unless half their Hiding beats rand(1000); SUCCESS opens the
    /// container. It used to be decided at the double-click, with no DELAY.</summary>
    public static bool Snooping(IActiveSkillSink sink, Item? container, int? startDifficulty = null)
    {
        var ch = sink.Self;
        int diff;
        if (startDifficulty.HasValue)
            diff = startDifficulty.Value;
        else
        {
            diff = SnoopingStart(sink, container);
            if (diff < -1)
                return false;
        }
        if (!SnoopingTarget(sink, container, out var mark))
            return false;
        if (mark == null)
            return true;   // not a crime really (:4123-4124)

        bool success = diff >= 0 && SkillEngine.UseQuick(ch, SkillType.Snooping, diff);

        CrimeWitnessService.CheckCrimeSeen(sink.World, ch, mark, SkillType.Snooping,
            sink.Random, isSnoop: true, item: container, action: ServerMessages.Get(Msg.SnoopingAttempting));
        ApplySkillKarma(ch, -4, message: sink.SysMessage);

        if (!success)
        {
            sink.SysMessage(ServerMessages.Get(Msg.SnoopingFailed));
            if (SkillEngine.GetAdjustedSkill(ch, SkillType.Hiding) / 2 < sink.Random.Next(1000))
                ch.ClearHiddenState();
            return false;
        }
        sink.OpenContainer(container!);
        return true;
    }

    // ---------------------------------------------------------- Lockpicking

    /// <summary>CItem::IsTypeLocked (CItem.cpp:4054-4066).</summary>
    private static bool IsTypeLocked(Item item) =>
        item.ItemType is ItemType.ContainerLocked or ItemType.DoorLocked or ItemType.ShipHoldLock
            or ItemType.ShipSideLocked;

    /// <summary>CItem::Use_LockPick (CItem.cpp:5378-5450): how hard this lock is to pick,
    /// 0 trivial, below 0 not possible. Only a locked thing (DEFMSG_UNLOCK_CANT_GENERAL)
    /// that nobody else carries (DEFMSG_UNLOCK_CANT_THERE). The key makes it trivial
    /// (and, not testing, says so - DEFMSG_LOCK_HAS_KEY - without unlocking). A door
    /// under MAGICUNLOCKDOOR needs its key at 0 and is otherwise impossible (10000)
    /// unless a one-in-N roll lets it be tried, a crime the onlookers may see. Not
    /// testing, it unlocks (Use_KeyChange); the difficulty is the lock's complexity
    /// (MORE2) / 10.</summary>
    public static int UseLockPick(IActiveSkillSink sink, Item lockItem, bool test, bool fail)
    {
        var ch = sink.Self;
        if (!IsTypeLocked(lockItem))
        {
            sink.SysMessage(ServerMessages.Get(Msg.UnlockCantGeneral));
            return -1;
        }
        if (lockItem.GetTopLevelObj() is Character carrier && carrier != ch)
        {
            sink.SysMessage(ServerMessages.Get(Msg.UnlockCantThere));
            return -1;
        }
        if (ch.FindKeyFor(lockItem) != null)
        {
            if (!test)
                sink.SysMessage(ServerMessages.Get(Msg.LockHasKey));
            return 0;
        }
        if (lockItem.ItemType == ItemType.DoorLocked && Item.MagicUnlockDoor != -1)
        {
            if (Item.MagicUnlockDoor == 0)
            {
                sink.SysMessage(ServerMessages.Get(Msg.UnlockDoorNeedsKey));
                return -1;
            }
            if (!test)
                CrimeWitnessService.CheckCrimeSeen(sink.World, ch, null, SkillType.Snooping, sink.Random,
                    item: lockItem, action: ServerMessages.Get(Msg.LockPickCrime));
            if (sink.Random.Next(Item.MagicUnlockDoor) != 0)
                return 10000;   // plain impossible
        }
        if (!test)
        {
            if (fail)
            {
                sink.SysMessage(ServerMessages.Get(lockItem.ItemType == ItemType.DoorLocked
                    ? Msg.UnlockFailDoor : Msg.UnlockFailCont));
                return -1;
            }
            UnlockWithMessage(sink, lockItem);
        }
        return (int)Math.Min(lockItem.More2 / 10u, (uint)int.MaxValue);
    }

    /// <summary>The unlocking half of CChar::Use_KeyChange (CCharUse.cpp): the locked
    /// type turns into its open one, and the picker is told which.</summary>
    private static void UnlockWithMessage(IActiveSkillSink sink, Item lockItem)
    {
        (ItemType open, string msg) = lockItem.ItemType switch
        {
            ItemType.ContainerLocked => (ItemType.Container, Msg.MsgKeyTargContUlock),
            ItemType.ShipHoldLock => (ItemType.ShipHold, Msg.MsgKeyTargHoldUlock),
            ItemType.ShipSideLocked => (ItemType.ShipSide, Msg.MsgKeyTargShipUlock),
            _ => (ItemType.Door, Msg.MsgKeyTargDoorUlock),
        };
        lockItem.ItemType = open;
        sink.SysMessage(ServerMessages.Get(msg));
    }

    /// <summary>What every Skill_Lockpicking stage asks (CCharSkill.cpp:2425-2441): the
    /// pick, ACTPRV (DEFMSG_LOCKPICKING_NOPICK), the lock (DEFMSG_LOCKPICKING_WITEM),
    /// the pick still carried (DEFMSG_LOCKPICKING_PREACH). With no pick in ACTPRV one
    /// is taken from the pack.</summary>
    private static Item? LockpickingPick(IActiveSkillSink sink, Item? lockItem)
    {
        var ch = sink.Self;
        var pick = ch.ActPrv.IsValid ? sink.World.FindItem(ch.ActPrv) : sink.FindBackpackItem(ItemType.Lockpick);
        if (pick == null || pick.IsDeleted || pick.ItemType != ItemType.Lockpick)
        {
            sink.SysMessage(ServerMessages.Get(Msg.LockpickingNopick));
            return null;
        }
        if (lockItem == null || lockItem.IsDeleted)
        {
            sink.SysMessage(ServerMessages.Get(Msg.LockpickingWitem));
            return null;
        }
        if (pick.GetTopLevelObj() != ch)
        {
            sink.SysMessage(ServerMessages.Get(Msg.LockpickingPreach));
            return null;
        }
        return pick;
    }

    /// <summary>Skill_Lockpicking START (CCharSkill.cpp:2450-2457): the lock within
    /// touch (DEFMSG_LOCKPICKING_REACH), then Use_LockPick testing it. Run when the
    /// skill starts.</summary>
    public static int LockpickingStart(IActiveSkillSink sink, Item? lockItem)
    {
        if (LockpickingPick(sink, lockItem) == null)
            return -3;
        if (!Scripting.ScriptTouchAccess.CanTouch(sink.Self, lockItem!))
        {
            sink.SysMessage(ServerMessages.Get(Msg.LockpickingReach));
            return -3;
        }
        return UseLockPick(sink, lockItem!, test: true, fail: false);
    }

    /// <summary>Source-X CChar::Skill_Lockpicking (CCharSkill.cpp:2413-2466). START is
    /// <see cref="LockpickingStart"/>. FAIL damages the pick (OnTakeDamage(1,
    /// HIT_BLUNT), which for a lockpick only fires its @Damage) and says nothing of
    /// its own; a START that came back negative is bound to fail (Skill_Done :3928).
    /// SUCCESS is Use_LockPick for real: it unlocks, saying which, or, with the key in
    /// the pack, only says the key would do. The pick used to be any one in the pack,
    /// the reach an English line of SphereNet's own, and the door gate a refusal before
    /// the roll.</summary>
    public static bool Lockpicking(IActiveSkillSink sink, Item? lockedTarget, int? startDifficulty = null)
    {
        var ch = sink.Self;
        int diff;
        if (startDifficulty.HasValue)
            diff = startDifficulty.Value;
        else
        {
            diff = LockpickingStart(sink, lockedTarget);
            if (diff < -1)
                return false;
        }
        var pick = LockpickingPick(sink, lockedTarget);
        if (pick == null)
            return false;

        if (diff < 0 || !SkillEngine.UseQuick(ch, SkillType.Lockpicking, diff))
        {
            Combat.CombatEngine.OnItemDamaged?.Invoke(pick, 1, ch, Combat.DamageType.HitBlunt);
            return false;
        }
        if (!Scripting.ScriptTouchAccess.CanTouch(ch, lockedTarget!))
        {
            sink.SysMessage(ServerMessages.Get(Msg.LockpickingReach));
            return false;
        }
        if (UseLockPick(sink, lockedTarget!, test: false, fail: false) < 0)
        {
            // -SKTRIG_FAIL: Skill_Fail runs the FAIL stage (CCharSkill.cpp:2463).
            Combat.CombatEngine.OnItemDamaged?.Invoke(pick, 1, ch, Combat.DamageType.HitBlunt);
            return false;
        }
        return true;
    }

    // ----------------------------------------------------------- RemoveTrap

    /// <summary>What every Skill_RemoveTrap stage asks first (CCharSkill.cpp:2900-2910):
    /// an IT_TRAP (DEFMSG_REMOVETRAPS_WITEM, also for nothing at all) the remover can
    /// touch (DEFMSG_REMOVETRAPS_REACH). One already sprung (IT_TRAP_ACTIVE) or
    /// disarmed is not a target.</summary>
    private static bool RemoveTrapTarget(IActiveSkillSink sink, Item? trap)
    {
        if (trap == null || trap.IsDeleted || trap.ItemType != ItemType.Trap)
        {
            sink.SysMessage(ServerMessages.Get(Msg.RemovetrapsWitem));
            return false;
        }
        if (!Scripting.ScriptTouchAccess.CanTouch(sink.Self, trap))
        {
            sink.SysMessage(ServerMessages.Get(Msg.RemovetrapsReach));
            return false;
        }
        return true;
    }

    /// <summary>Skill_RemoveTrap START (CCharSkill.cpp:2911-2914): the trap judged, and
    /// a difficulty of rand(95). Run when the skill starts.</summary>
    public static int RemoveTrapStart(IActiveSkillSink sink, Item? trap) =>
        RemoveTrapTarget(sink, trap) ? RandVal(sink.Random, 95) : -3;

    /// <summary>Source-X CChar::Skill_RemoveTrap (CCharSkill.cpp:2888-2926). START is
    /// <see cref="RemoveTrapStart"/>; the trap is judged again after the DELAY. A START
    /// that came back negative is bound to fail (Skill_Done, :3928) and its FAIL stage
    /// runs. SUCCESS disarms the trap for five minutes, FAIL sets it off.</summary>
    public static bool RemoveTrap(IActiveSkillSink sink, Item? trap, int? startDifficulty = null)
    {
        var ch = sink.Self;
        if (!RemoveTrapTarget(sink, trap))
            return false;
        int diff = startDifficulty ?? RandVal(sink.Random, 95);
        bool success = diff >= 0 && SkillEngine.UseQuick(ch, SkillType.RemoveTrap, diff);
        if (success)
        {
            // Disabled, and it re-arms by itself five minutes later
            // (SetTrapState(IT_TRAP_INACTIVE, ITEMID_NOTHING, 5*60), :2921).
            trap!.SetTrapState(ItemType.TrapInactive, 0, 5 * 60);
        }
        else
        {
            // A botched disarm sets it off: Use_Item on the trap (:2917), which
            // is Use_Trap - arm the graphic, damage from the trap's own field
            // (default 2) - taken through OnTakeDamage(HIT_BLUNT|GENERAL)
            // (CCharUse.cpp:1753-1759, CItem.cpp:5493-5508).
            int trapDmg = trap!.UseTrap();
            Combat.CombatEngine.ApplyScriptDamage(ch, trapDmg,
                Combat.DamageType.HitBlunt | Combat.DamageType.General);
            if (ch.Hits <= 0 && !ch.IsDead)
            {
                if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(ch, null);
                else ch.Kill();
            }
        }
        return success;
    }

    // -------------------------------------------------------------- Healing

    /// <summary>What every Skill_Healing stage asks first (CCharSkill.cpp:2743-2814):
    /// the bandage, ACTPRV (DEFMSG_HEALING_NOAIDS, DEFMSG_HEALING_WITEM), the patient
    /// (DEFMSG_HEALING_BEYOND for a corpse whose owner is gone, DEFMSG_HEALING_NONCHAR for
    /// one that cannot be selected), within two tiles (DEFMSG_HEALING_TOOFAR, and the
    /// patient hears DEFMSG_HEALING_ATTEMPT), a corpse that can be raised over, and
    /// someone who needs it (DEFMSG_HEALING_HEALTHY / NONEED). With no bandage in ACTPRV
    /// one is taken from the pack. Null when the stage stops.</summary>
    private static Character? HealingPatient(IActiveSkillSink sink, Character? target,
        SkillType healingSkill, Item? selectedCorpse, out Item? bandage)
    {
        var ch = sink.Self;
        bandage = ch.ActPrv.IsValid ? sink.World.FindItem(ch.ActPrv) : sink.FindBackpackItem(ItemType.Bandage);
        if (bandage == null || bandage.IsDeleted)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingNoaids));
            return null;
        }
        if (bandage.ItemType != ItemType.Bandage)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingWitem));
            return null;
        }

        // "No target given, treat me" and "the target I picked is gone" are different
        // answers: a corpse whose owner cannot be resolved stops with HEALING_BEYOND
        // (CCharSkill.cpp:2769).
        if (target == null && selectedCorpse?.ItemType == ItemType.Corpse)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingBeyond));
            return null;
        }
        target ??= ch;
        if ((CharDefHelper.GetCanFlags(target) & CanFlags.C_NonSelectable) != 0)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingNonchar));
            return null;
        }

        // Healing and Veterinary share Skill_Healing with no species gate between
        // them (CCharSkill.cpp:3718-3720).
        var corpse = selectedCorpse?.ItemType == ItemType.Corpse ? selectedCorpse : null;
        Point3D healAnchor = corpse?.Position ?? target.Position;
        if (!CanReachPoint(ch, healAnchor, sink.World, SkillEngine.GetUseRange(healingSkill, 2)))
        {
            sink.SysMessage(ServerMessages.GetFormatted(Msg.HealingToofar, corpse?.Name ?? target.Name));
            if (target != ch)
                Character.SendOwnerMessage?.Invoke(target, ServerMessages.GetFormatted(Msg.HealingAttempt,
                    ch.Name, corpse?.Name ?? ServerMessages.Get(Msg.HealingWho)));
            return null;
        }

        // Every corpse-side condition, before anything is spent (CCharSkill.cpp:2796).
        if (corpse != null && !IsCorpseResurrectable(sink, ch, target, corpse))
            return null;

        if (!target.IsStatFlag(StatFlag.Poisoned | StatFlag.Dead) && target.Hits >= target.MaxHits)
        {
            sink.SysMessage(target == ch
                ? ServerMessages.Get(Msg.HealingHealthy)
                : ServerMessages.GetFormatted(Msg.HealingNoneed, target.Name));
            return null;
        }

        // A ghost that has not manifested cannot be raised (IsCorpseResurrectable,
        // CItemCorpse.cpp:42). Source-X asks for STATF_INSUBSTANTIAL, which death sets
        // only on a PLAYER who was not already in war mode (CCharAct.cpp:4468) and never
        // on an NPC; SphereNet uses war mode as the manifest signal for players, so a
        // bonded pet that died out of combat can still be revived by Veterinary.
        if (target.IsStatFlag(StatFlag.Dead) && target.IsPlayer && !target.IsInWarMode)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingResManifest));
            return null;
        }
        return target;
    }

    /// <summary>Skill_Healing START (CCharSkill.cpp:2823-2841): the patient judged, the
    /// emote ("applies a bandage"), and the difficulty: rand(25)+85 to raise the dead,
    /// rand(50)+50 to cure poison, rand(80) to heal. Run when the skill starts.</summary>
    public static int HealingStart(IActiveSkillSink sink, Character? target,
        SkillType healingSkill = SkillType.Healing, Item? selectedCorpse = null)
    {
        var ch = sink.Self;
        var patient = HealingPatient(sink, target, healingSkill, selectedCorpse, out _);
        if (patient == null)
            return -3;
        sink.Emote(patient == ch
            ? ServerMessages.Get(Msg.HealingSelf)
            : ServerMessages.GetFormatted(Msg.HealingTo, patient.Name));
        bool raise = selectedCorpse?.ItemType == ItemType.Corpse || patient.IsStatFlag(StatFlag.Dead);
        return raise ? 85 + RandVal(sink.Random, 25)
             : patient.IsStatFlag(StatFlag.Poisoned) ? 50 + RandVal(sink.Random, 50)
             : RandVal(sink.Random, 80);
    }

    /// <summary>
    /// Source-X CChar::Skill_Healing (CCharSkill.cpp:2724-2886), for Healing and
    /// Veterinary. START is <see cref="HealingStart"/>; the patient is judged again
    /// after the DELAY. A START that came back negative is bound to fail (Skill_Done,
    /// :3928). FAIL spends the bandage and the patient hears DEFMSG_HEALING_ATTEMPTF;
    /// SUCCESS spends it, leaves a bloody one, and raises, cures or heals.
    /// </summary>
    public static bool Healing(IActiveSkillSink sink, Character? target,
        SkillType healingSkill = SkillType.Healing, Item? selectedCorpse = null, int? startDifficulty = null)
    {
        var ch = sink.Self;
        int diff;
        if (startDifficulty.HasValue)
        {
            diff = startDifficulty.Value;
        }
        else
        {
            diff = HealingStart(sink, target, healingSkill, selectedCorpse);
            if (diff < 0)
                return false;
        }
        var patient = HealingPatient(sink, target, healingSkill, selectedCorpse, out var bandage);
        if (patient == null)
            return false;
        var corpse = selectedCorpse?.ItemType == ItemType.Corpse ? selectedCorpse : null;

        // No animation and no sound: Skill_Healing plays neither at any stage, and
        // healing is neither a crafting nor a gathering skill, so Skill_Start and
        // Skill_Stroke play none for it either (CCharSkill.cpp:2724-2886/4543).
        if (diff < 0 || !SkillEngine.UseQuick(ch, healingSkill, diff))
        {
            // FAIL (:2814-2821): the bandage is spent, and the patient told.
            sink.ConsumeAmount(bandage!);
            if (patient != ch)
                Character.SendOwnerMessage?.Invoke(patient, ServerMessages.GetFormatted(Msg.HealingAttemptf,
                    ch.Name, corpse?.Name ?? ServerMessages.Get(Msg.HealingWho)));
            return false;
        }
        sink.ConsumeAmount(bandage!);
        target = patient;

        // Only a SUCCESSFUL bandage leaves a bloody one, made from its item
        // definition - ITEMID_BANDAGES_BLOODY1 or 2 at random (CCharSkill.cpp:2846-2847).
        var bloody = sink.World.CreateItem();
        ushort bloodyId = sink.Random.Next(2) != 0 ? (ushort)0x0E20 : (ushort)0x0E22;
        bloody.BaseId = bloodyId;
        if (!Definitions.ItemDefHelper.ApplyInstanceMetadata(bloody, bloodyId))
            bloody.ItemType = ItemType.BandageBlood;
        sink.DeliverItem(bloody);

        ch.FlagForHelpingCriminalIfNeeded(target);

        // Spell_Resurrection(pCorpse, this) (:2855): no further reach or sight test.
        if (corpse != null || target.IsStatFlag(StatFlag.Dead))
        {
            sink.ResurrectTarget(target);
            return true;
        }

        if (target.IsStatFlag(StatFlag.Poisoned))
        {
            // Source-X Skill_Healing (CCharSkill.cpp:2859): Calc_CurePoisonChance on the
            // worn poison against the healer's skill; a success only cures.
            int skillLvl = ch.GetSkill(healingSkill);
            if (CharacterPoisonState.CureChance(target.Poison.Memory, skillLvl, ch.PrivLevel >= PrivLevel.GM))
            {
                target.SetPoisonCure(true);
                sink.SysMessage(ServerMessages.GetFormatted(Msg.HealingCure1,
                    target == ch ? ServerMessages.Get(Msg.HealingYourself) : target.Name));
                if (target != ch)
                    Character.SendOwnerMessage?.Invoke(target, ServerMessages.GetFormatted(Msg.HealingCure2, ch.Name));
            }
            else
            {
                if (target != ch)
                    sink.SysMessage(ServerMessages.Get(Msg.HealingCure3));
                if (target != ch)
                    Character.SendOwnerMessage?.Invoke(target, ServerMessages.Get(Msg.HealingCure4));
                else
                    sink.SysMessage(ServerMessages.Get(Msg.HealingCure4));
                return false;
            }
            return true;
        }

        // The amount healed is the skill's EFFECT curve at the adjusted skill
        // (m_Act_Effect, set at Skill_Start, CCharSkill.cpp:4466-4471), 1 when the
        // skill names no EFFECT (:2881-2884).
        int heal = ch.ActionEffect >= 0
            ? ch.ActionEffect
            : SkillEngine.GetEffect(healingSkill, SkillEngine.GetAdjustedSkill(ch, healingSkill), 1);
        short hitsBefore = target.Hits;
        target.Hits = (short)Math.Min(target.MaxHits, target.Hits + Math.Max(0, heal));
        target.NotifyStatValChange(Character.StatValHits, hitsBefore, ch);
        return true;
    }

    /// <summary>The corpse-side half of a bandage resurrection, mirroring Source-X
    /// CItemCorpse::IsCorpseResurrectable (CItemCorpse.cpp:28). These are checks ON TOP
    /// of the healer's own reach to the corpse, not a replacement for it - the healer
    /// can stand over a corpse whose ghost has wandered off, and used to be able to
    /// raise it from anywhere on the map, or from another one.</summary>
    private static bool IsCorpseResurrectable(IActiveSkillSink sink, Character healer,
        Character ghost, Item corpse)
    {
        // A corpse inside a container cannot be resurrected over; it has to be a
        // top-level ground object (:62).
        if (corpse.ContainedIn.IsValid)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingCorpseg));
            return false;
        }

        var corpsePos = corpse.Position;

        if (ghost.IsStatFlag(StatFlag.Dead))
        {
            // The ghost has to be able to see its own corpse and be within two tiles
            // of it (:48, :55) - conditions on the GHOST, which nothing here checked.
            if (ghost.MapIndex != corpsePos.Map ||
                ghost.Position.GetDistanceTo(corpsePos) > 2)
            {
                sink.SysMessage(ServerMessages.Get(Msg.HealingResToofar));
                return false;
            }
            if (!sink.World.CanSeeLOSFor(ghost, ghost.Position, corpsePos))
            {
                sink.SysMessage(ServerMessages.Get(Msg.HealingResLos));
                return false;
            }
        }

        // The antimagic flags of the CORPSE's region, refused before anything is spent
        // (:73). Character.Resurrect only ever looked at NoMagic, and only at the very
        // end - by which point the bandage was gone and the skill had reported success.
        var region = sink.World.FindRegion(corpsePos);
        if (region != null &&
            (region.IsFlag(RegionFlag.NoMagic) || region.IsFlag(RegionFlag.Recall) ||
             region.IsFlag(RegionFlag.NoTeleport)))
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingAm));
            return false;
        }

        return true;
    }

    // --------------------------------------------------------------- Taming

    /// <summary>What every Skill_Taming stage asks first (CCharSkill.cpp:2276-2335): a
    /// creature (none: nothing said), not oneself (DEFMSG_TAMING_YMASTER), not a player
    /// (DEFMSG_TAMING_CANT), within the skill's RANGE in 3D, 10 when it names none
    /// (DEFMSG_TAMING_REACH), in sight (DEFMSG_TAMING_LOS); the tamer turns to it. Out
    /// of GM mode a pet is DEFMSG_TAMING_TAME, and one with no TAMING, one that knows
    /// Animal Lore or one of a playable body DEFMSG_TAMING_TAMED.</summary>
    private static bool TamingTarget(IActiveSkillSink sink, Character? target)
    {
        var ch = sink.Self;
        if (target == null || target.IsDeleted)
            return false;
        if (target == ch)
        {
            sink.SysMessage(ServerMessages.Get(Msg.TamingYmaster));
            return false;
        }
        if (target.IsPlayer)
        {
            sink.SysMessage(ServerMessages.Get(Msg.TamingCant));
            return false;
        }
        int range = SkillEngine.GetUseRange(SkillType.Taming, 10);
        var me = ch.Position;
        var it = target.Position;
        int dist = me.Map != it.Map ? int.MaxValue
            : Math.Max(Math.Max(Math.Abs(me.X - it.X), Math.Abs(me.Y - it.Y)), Math.Abs(me.Z - it.Z) / 8);
        if (dist > range)
        {
            sink.SysMessage(ServerMessages.Get(Msg.TamingReach));
            return false;
        }
        if (!sink.World.CanSeeLOSFor(ch, target))
        {
            sink.SysMessage(ServerMessages.Get(Msg.TamingLos));
            return false;
        }
        FaceSkillTarget(ch, target.Position);   // UpdateDir (:2307)
        if (!ch.IsGmMode)
        {
            if (target.IsStatFlag(StatFlag.Pet))
            {
                sink.SysMessage(ServerMessages.GetFormatted(Msg.TamingTame, target.Name));
                return false;
            }
            if (target.GetSkill(SkillType.Taming) == 0 || target.GetSkill(SkillType.AnimalLore) > 0 ||
                Combat.BodyAnimTranslator.IsPlayableBody(target.BodyId))
            {
                sink.SysMessage(ServerMessages.GetFormatted(Msg.TamingTamed, target.Name));
                return false;
            }
        }
        return true;
    }

    /// <summary>Skill_Taming START (CCharSkill.cpp:2337-2345): TAMING/10, plus 50 when the
    /// creature remembers the tamer as a foe; 2 to 5 strokes to come. Run when the skill
    /// starts.</summary>
    public static int TamingStart(IActiveSkillSink sink, Character? target)
    {
        var ch = sink.Self;
        if (!TamingTarget(sink, target))
            return -3;
        int diff = target!.GetSkill(SkillType.Taming) / 10;
        if (target.Memory_FindObjTypes(ch.Uid, MemoryType.Fight | MemoryType.HarmedBy |
                MemoryType.IrritatedBy | MemoryType.Aggreived) != null)
            diff += 50;   // "I've attacked it before?"
        ch.SkillStrokesLeft = RandVal(sink.Random, 4) + 2;
        return diff;
    }

    /// <summary>Skill_Taming STROKE (CCharSkill.cpp:2350-2371), on each timeout: the
    /// creature judged again, then, while strokes are left and out of GM mode, the
    /// tamer says one of DEFMSG_TAMING_1..4 aloud and waits another DELAY. 1 = keep
    /// going, 0 = the strokes are done, below 0 = it stops there.</summary>
    public static int TamingStroke(IActiveSkillSink sink, Character? target)
    {
        var ch = sink.Self;
        if (!TamingTarget(sink, target))
            return -1;
        if (ch.SkillStrokesLeft <= 0 || ch.IsGmMode)
            return 0;
        string[] tries = { Msg.Taming1, Msg.Taming2, Msg.Taming3, Msg.Taming4 };
        ch.SayAloud(ServerMessages.GetFormatted(tries[sink.Random.Next(tries.Length)], target!.Name));
        ch.SkillStrokesLeft--;
        return 1;
    }

    /// <summary>Source-X CChar::Skill_Taming (CCharSkill.cpp:2270-2401). START is
    /// <see cref="TamingStart"/> and the strokes <see cref="TamingStroke"/>; whether a
    /// creature can be tamed is data, its own TAMING skill. SUCCESS: a creature this tamer
    /// tamed before takes them back with DEFMSG_TAMING_REMEMBER over it and no skill
    /// credit; a first taming says DEFMSG_TAMING_SUCCESS and leaves the memory
    /// (MEMORY_SPEAK, NPC_MEM_ACT_TAMED) against a tame/release loop.</summary>
    public static bool Taming(IActiveSkillSink sink, Character? target, int? startDifficulty = null)
    {
        var ch = sink.Self;
        int diff;
        if (startDifficulty.HasValue)
            diff = startDifficulty.Value;
        else
        {
            diff = TamingStart(sink, target);
            if (diff < -1)
                return false;
        }
        if (!TamingTarget(sink, target))
            return false;

        var tamedMemory = target!.Memory_FindObjTypes(ch.Uid, MemoryType.Speak);
        bool tamedBefore = tamedMemory != null && (tamedMemory.More1 & 0xFFFF) == Character.NpcMemActTamed;
        bool success = diff >= 0 && SkillEngine.UseQuick(ch, SkillType.Taming, diff, allowGain: !tamedBefore);
        if (success)
            success = target.TryAssignOwnership(ch, ch, summoned: false, enforceFollowerCap: true);
        if (success)
        {
            target.Food = 50;   // Stat_SetVal(STAT_FOOD, 50)
            if (tamedBefore)
            {
                sink.ObjectMessage(target, ServerMessages.GetFormatted(Msg.TamingRemember, target.Name));
            }
            else
            {
                sink.SysMessage(ServerMessages.GetFormatted(Msg.TamingSuccess, target.Name));
                var memory = target.Memory_AddObjTypes(ch.Uid, MemoryType.Speak);
                memory.More1 = (memory.More1 & 0xFFFF0000u) | Character.NpcMemActTamed;
            }
        }
        return success;
    }

    // -------------------------------------------------------------- Herding

    /// <summary>What every Skill_Herding stage asks first (CCharSkill.cpp:2535-2546): the
    /// creature (DEFMSG_HERDING_LTARG) and the crook, ACTPRV (DEFMSG_HERDING_NOCROOK).
    /// A start with no crook in ACTPRV takes one from the pack.</summary>
    private static Item? HerdingCrook(IActiveSkillSink sink, Character? animal)
    {
        var ch = sink.Self;
        if (animal == null || animal.IsDeleted)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HerdingLtarg));
            return null;
        }
        var crook = (ch.ActPrv.IsValid ? sink.World.FindItem(ch.ActPrv) : null) is
            { ItemType: ItemType.WeaponMaceCrook, IsDeleted: false } actCrook
            ? actCrook
            : FindGatherTool(sink, ItemType.WeaponMaceCrook);
        if (crook == null)
            sink.SysMessage(ServerMessages.Get(Msg.HerdingNocrook));
        return crook;
    }

    /// <summary>Skill_Herding START (CCharSkill.cpp:2551-2577): the creature and the
    /// spot must be in line of sight (DEFMSG_MSG_MOUNT_DIST), a pet cannot be herded
    /// (DEFMSG_TAMING_TAME), the spot must be valid (DEFMSG_LOCATION_INVALID); the
    /// difficulty is INT/2 + rand(INT/2). Run when the skill starts.</summary>
    public static int HerdingStart(IActiveSkillSink sink, Character? animal, Point3D? destination)
    {
        var ch = sink.Self;
        if (HerdingCrook(sink, animal) == null)
            return -3;
        if (!sink.World.CanSeeLOSFor(ch, animal!) ||
            destination.HasValue && destination.Value.Map == ch.MapIndex &&
            !sink.World.CanSeeLOSFor(ch, ch.Position, destination.Value))
        {
            sink.SysMessage(ServerMessages.Get(Msg.MsgMountDist));
            return -2;
        }
        if (animal!.IsStatFlag(StatFlag.Pet))
        {
            sink.SysMessage(ServerMessages.GetFormatted(Msg.TamingTame, animal.GetName()));
            return -2;
        }
        if (!destination.HasValue || destination.Value.Map != ch.MapIndex ||
            !IsValidMapPoint(sink.World, destination.Value))
        {
            sink.SysMessage(ServerMessages.Get(Msg.LocationInvalid));
            return -2;
        }
        int intVal = Combat.CombatEngine.EffectiveInt(animal) / 2;
        return intVal + RandVal(sink.Random, intVal);
    }

    /// <summary>CPointBase::IsValidPoint: inside the map and the Z range.</summary>
    private static bool IsValidMapPoint(GameWorld world, Point3D p)
    {
        var (w, h) = world.MapData?.GetMapSize(p.Map) ?? (7168, 4096);
        return p.X >= 0 && p.X < w && p.Y >= 0 && p.Y < h && p.Z > -127 && p.Z < 127;
    }

    /// <summary>Source-X CChar::Skill_Herding (CCharSkill.cpp:2522-2619). START is
    /// <see cref="HerdingStart"/>. SUCCESS: a disconnected or ridden creature is
    /// left alone; staff in GM mode teleport it there (anyone of no higher rank); a
    /// player otherwise moves only an animal-brained NPC (DEFMSG_HERDING_PLAYER for
    /// anything else, after the roll), which WALKS to the spot (NPCACT_GOTO), and
    /// DEFMSG_HERDING_SUCCESS is said over it. The animal used to be teleported,
    /// and its kind and the reach (an invented 8 tiles) were judged before the roll.</summary>
    public static bool Herding(IActiveSkillSink sink, Character? animal, Point3D? destination,
        int? startDifficulty = null)
    {
        var ch = sink.Self;
        if (HerdingCrook(sink, animal) == null)
            return false;
        int diff = startDifficulty ?? HerdingStart(sink, animal, destination);
        if (diff < 0)
            return false;
        if (!SkillEngine.UseQuick(ch, SkillType.Herding, diff))
            return false;

        if (animal!.IsLoggedOut || animal.IsStatFlag(StatFlag.Ridden) || !destination.HasValue)
            return false;
        if (ch.IsGmMode)
        {
            if (animal.PrivLevel > ch.PrivLevel)
            {
                sink.SysMessage($"{animal.GetName()} {ServerMessages.Get(Msg.HerdingPlayer)}");
                return false;
            }
            sink.MoveCharacter(animal, destination.Value);   // Spell_Teleport
        }
        else
        {
            if (animal.IsPlayer || animal.NpcBrain != NpcBrainType.Animal)
            {
                sink.SysMessage($"{animal.GetName()} {ServerMessages.Get(Msg.HerdingPlayer)}");
                return false;
            }
            animal.ActP = destination.Value;
            animal.Action = (SkillType)NpcAction.GoTo;
            animal.NextNpcActionTime = 0;
            Character.WakeNpc?.Invoke(animal);
        }
        sink.ObjectMessage(animal, ServerMessages.Get(Msg.HerdingSuccess));
        return true;
    }

    // ------------------------------------------------------------ Poisoning

    /// <summary>Source-X CChar::Skill_Poisoning. Apply potion poison level to target weapon.</summary>
    public static bool Poisoning(IActiveSkillSink sink, Item? weapon, Item? selectedPotion = null)
    {
        var ch = sink.Self;
        if (weapon == null || !IsBladeOrFood(weapon.ItemType))
        {
            sink.SysMessage(ServerMessages.Get(Msg.PoisoningWitem));
            return false;
        }
        var potion = selectedPotion ?? sink.FindBackpackItem(ItemType.Potion);
        if (potion == null ||
            !CanReachItem(ch, potion, sink.World, SkillType.Poisoning, 2,
                requirePossession: true) ||
            !IsPoisonPotion(potion))
        {
            sink.SysMessage(ServerMessages.Get(Msg.PoisoningSelect1));
            return false;
        }
        if (!CanReachItem(ch, weapon, sink.World, SkillType.Poisoning, 2,
                requirePossession: true))
        {
            sink.SysMessage("You must have that item in your possession.");
            return false;
        }

        // Difficulty rand(60) (CCharSkill.cpp:2176) - not the potion's quality.
        bool success = SkillEngine.UseQuick(ch, SkillType.Poisoning, sink.Random.Next(60));
        if (success)
        {
            if (!SkillEngine.HasFlag(SkillType.Poisoning, SkillFlag.NoSfx))
                sink.Sound(0x247); // powdering (:2193)
            // Source-X Skill_Poisoning: m_poison_skill (MOREZ) = (byte)(the potion's
            // m_dwSkillQuality (MORE2) / 10), for food and blades alike (:2201/2207).
            Combat.CombatEngine.SetWeaponPoisonSkill(weapon, (int)Math.Min(potion.More2 / 10u, 255u));
            sink.ConsumeAmount(potion);
            sink.SysMessage(ServerMessages.Get(Msg.PoisoningSuccess));
        }
        return success;
    }

    // ------------------------------------------------------------- Tracking

    /// <summary>The category pick of Source-X Cmd_Skill_Tracking (CClientUse.cpp:1123-1252):
    /// the creatures of that kind within reach, with a Skill_UseQuick only for the gain
    /// (20 + 0..29 when something was found, 10 + 0..29 when not). An empty search says
    /// so by category. There is no success roll: SphereNet rolled one and could refuse a
    /// list it had found, and answered with a count instead of the list.</summary>
    public static bool Tracking(IActiveSkillSink sink, TrackingCategory category) =>
        Tracking(sink, category, out _);

    public static bool Tracking(IActiveSkillSink sink, TrackingCategory category, out List<Character> targets)
    {
        var ch = sink.Self;
        targets = FindTrackingTargets(sink, category, TrackingDistance(ch));
        if (targets.Count > 0)
        {
            SkillEngine.UseQuick(ch, SkillType.Tracking, 20 + sink.Random.Next(30));
            return true;
        }
        SkillEngine.UseQuick(ch, SkillType.Tracking, 10 + sink.Random.Next(30));
        sink.SysMessage(ServerMessages.Get(category switch
        {
            TrackingCategory.Animals => Msg.TrackingFailAnimal,
            TrackingCategory.Monsters => Msg.TrackingFailMonster,
            _ => Msg.TrackingFailHuman,
        }));
        return false;
    }

    /// <summary>m_atTracking.m_dwDistMax: ACTEFFECT when the skill names an EFFECT, else
    /// Tracking / 10 + 10 - a human counting at least 20.0 under RACIALF_HUMAN_JACKOFTRADES
    /// (CClientUse.cpp:1162-1172). A RANGE stood in for it here.</summary>
    public static int TrackingDistance(Character ch)
    {
        if (ch.ActionEffect >= 0)
            return ch.ActionEffect;
        int skill = SkillEngine.GetAdjustedSkill(ch, SkillType.Tracking);
        if ((Character.RacialFlags & (int)RacialFlags.HumanJackOfTrades) != 0 && ch.IsHuman)
            skill = Math.Max(skill, 200);
        return skill / 10 + 10;
    }

    /// <summary>The search of Cmd_Skill_Tracking (CClientUse.cpp:1173-1222). A kind is a
    /// brain group (GetNPCBrainGroup, dragons and berserkers counting as monsters), so
    /// Humans holds townsfolk of every trade and players too. Players is the one search
    /// with a difficulty: no ghosts, players only, and each found with
    /// 50 * (tracking + detecting hidden + 10 * 0..19) / (hiding + stealth) percent - or,
    /// under FEATURESE's update, 50 * (2 * tracking + detecting hidden) / (hiding +
    /// stealth); an elf tracks at half under RACIALF_ELF_DIFFTRACK. A staff member's
    /// insubstantial body is never found by a lesser one. At most 63 (MAX_MENU_ITEMS - 1).</summary>
    public static List<Character> FindTrackingTargets(IActiveSkillSink sink, TrackingCategory category, int distMax)
    {
        var ch = sink.Self;
        NpcBrainType wanted = category switch
        {
            TrackingCategory.Animals => NpcBrainType.Animal,
            TrackingCategory.Monsters => NpcBrainType.Monster,
            TrackingCategory.Humans => NpcBrainType.Human,
            _ => NpcBrainType.None,
        };
        var found = new List<Character>();
        foreach (var c in sink.World.GetCharsInRange(ch.Position, distMax)
                     .OrderBy(c => ch.Position.GetDistanceTo(c.Position)).ThenBy(c => c.Uid.Value))
        {
            if (c == ch || c.IsDeleted)
                continue;
            if (ch.PrivLevel < c.PrivLevel && c.IsStatFlag(StatFlag.Insubstantial))
                continue;
            var group = c.GetNpcBrainGroup();
            if (group is NpcBrainType.Dragon or NpcBrainType.Berserk)
                group = NpcBrainType.Monster;
            if (group != wanted)
            {
                if (category != TrackingCategory.Players || c.IsDead || !c.IsPlayer)
                    continue;
                int tracking = ch.GetSkill(SkillType.Tracking);
                int detect = ch.GetSkill(SkillType.DetectingHidden);
                if ((Character.RacialFlags & (int)RacialFlags.ElfDifficultTracking) != 0 &&
                    c.BodyId is 0x025D or 0x025E or 0x025F or 0x0260)
                    tracking /= 2;
                int divisor = Math.Max(c.GetSkill(SkillType.Hiding) + c.GetSkill(SkillType.Stealth), 1);
                int chance = (Character.FeatureSE & 0x02) != 0
                    ? 50 * (tracking * 2 + detect) / divisor
                    : 50 * (tracking + detect + 10 * sink.Random.Next(20)) / divisor;
                if (sink.Random.Next(100) > chance)
                    continue;
            }
            found.Add(c);
            if (found.Count >= 63)
                break;
        }
        return found;
    }

    /// <summary>One stroke of an active Tracking (CChar::Skill_Tracking(uid, dir, dist),
    /// CCharSkill.cpp:1289-1343): over the tracker, where the quarry is - right here, near
    /// (under 16), at (under 32), far (under 100) or very far, and which way, or that it
    /// has disconnected. False - the tracking ends - when it is gone, out of reach, or a
    /// staff member's insubstantial body.</summary>
    public static bool TrackingStroke(IActiveSkillSink sink, Character? quarry, int distMax)
    {
        var ch = sink.Self;
        if (quarry == null || quarry.IsDeleted || quarry.MapIndex != ch.MapIndex)
            return false;
        // GetTopDist3D: the 2D distance or the height difference in half-heights.
        int dist = Math.Max(ch.Position.GetDistanceTo(quarry.Position), Math.Abs(ch.Z - quarry.Z) / 8);
        if (dist > distMax)
            return false;
        if (quarry.IsStatFlag(StatFlag.Insubstantial) && quarry.PrivLevel > ch.PrivLevel)
            return false;

        string key = dist <= 0 ? Msg.TrackingResult0
            : dist < 16 ? Msg.TrackingResult1
            : dist < 32 ? Msg.TrackingResult2
            : dist < 100 ? Msg.TrackingResult3
            : Msg.TrackingResult4;
        if (ServerMessages.Get(key).Length > 0)
        {
            string where = quarry.IsLoggedOut
                ? ServerMessages.Get(Msg.TrackingResultDisc)
                : ServerMessages.Get(s_mapDirs[(int)ch.Position.GetDirectionTo(quarry.Position) & 7]);
            sink.ObjectMessage(ch, ServerMessages.GetFormatted(key, quarry.GetName(), where));
        }
        return true;
    }

    private static readonly string[] s_mapDirs =
    {
        Msg.MapDir0, Msg.MapDir1, Msg.MapDir2, Msg.MapDir3,
        Msg.MapDir4, Msg.MapDir5, Msg.MapDir6, Msg.MapDir7,
    };

    public enum TrackingCategory { Animals, Monsters, Humans, Players }

    // ----------------------------------------------------------- primitives

    private static bool CanReachPoint(Character ch, Point3D point, GameWorld world,
        int range, bool requireLos = true)
    {
        if (point.Map != ch.MapIndex || ch.Position.GetDistanceTo(point) > range)
            return false;
        return !requireLos || world.CanSeeLOSFor(ch, ch.Position, point);
    }

    private static bool TryResolveItemTop(Item item, GameWorld world,
        out Point3D position, out Character? owner)
    {
        owner = null;
        var seen = new HashSet<uint>();
        for (int depth = 0; depth < 32; depth++)
        {
            if (!seen.Add(item.Uid.Value))
                break;
            if (!item.ContainedIn.IsValid)
            {
                position = item.Position;
                return true;
            }

            var holder = world.FindObject(item.ContainedIn);
            if (holder is Character ch)
            {
                owner = ch;
                position = ch.Position;
                return true;
            }
            if (holder is Item parent)
            {
                item = parent;
                continue;
            }
            break;
        }

        position = default;
        return false;
    }

    private static bool CanReachItem(Character ch, Item item, GameWorld world,
        SkillType skill, int fallbackRange, bool requirePossession = false)
    {
        if (item.IsDeleted || !TryResolveItemTop(item, world, out Point3D top, out Character? owner))
            return false;
        if (requirePossession && owner != ch)
            return false;
        return CanReachPoint(ch, top, world, SkillEngine.GetUseRange(skill, fallbackRange));
    }

    private static int GetDistance(Point3D a, Point3D b)
    {
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>A poison potion: IT_POTION whose m_itPotion.m_Type (MORE1) is
    /// SPELL_Poison (CCharSkill.cpp:2185, CItem.h:279), or a bottle carrying the
    /// older POTION_SPELL=Poison tag.</summary>
    /// <summary>A potion of s_poison (ResGetIndex(m_itPotion.m_Type) == SPELL_Poison,
    /// CCharSkill.cpp:2181). A pack's potion names its spell by defname in @Create
    /// (MORE1=s_poison), which only the shared resolver reads; the numeric MORE1
    /// alone refused every scripted poison potion.</summary>
    private static bool IsPoisonPotion(Item potion) =>
        potion.ItemType == ItemType.Potion &&
        (Combat.ItemDamageEngine.ResolvePotionSpell(potion) == SpellType.Poison ||
         (potion.TryGetTag("POTION_SPELL", out string? spell) &&
          string.Equals(spell, "Poison", StringComparison.OrdinalIgnoreCase)));

    /// <summary>What Skill_Poisoning will coat (CCharSkill.cpp:2196-2209): fruit, food,
    /// raw food and raw meat, and the sharp mace, sword and fencing weapons. An axe
    /// is not on the list ("can't be used to chop trees").</summary>
    private static bool IsBladeOrFood(ItemType t) => t is
        ItemType.WeaponMaceSharp or ItemType.WeaponSword or ItemType.WeaponFence or
        ItemType.Food or ItemType.FoodRaw or ItemType.MeatRaw or ItemType.Fruit;

    private static Character? ResolveItemOwner(Item it, World.GameWorld world, int maxDepth = 16)
    {
        for (int i = 0; i < maxDepth; i++)
        {
            if (!it.ContainedIn.IsValid) return null;
            var holder = world.FindObject(it.ContainedIn);
            if (holder is Character c) return c;
            if (holder is Item parent) { it = parent; continue; }
            return null;
        }
        return null;
    }

    // --------------------------------------------------------------- Mining

    /// <summary>Source-X CChar::Skill_Mining's SUCCESS stage (CCharSkill.cpp:1383-1481):
    /// the checks every stage repeats, then Skill_NaturalResource_Create. A failed skill
    /// check says nothing here - the pack's @Fail speaks for it.</summary>
    public static bool Mining(IActiveSkillSink sink, Point3D target, GatheringEngine? gatheringEngine, World.GameWorld world)
    {
        var ch = sink.Self;
        string? refusal = CheckGatherPreconditions(sink, SkillType.Mining, target, world, out _);
        if (refusal != null)
        {
            sink.SysMessage(ServerMessages.Get(refusal));
            return false;
        }
        FaceSkillTarget(ch, target);
        // No sound or animation here: this is the SUCCESS stage, and upstream plays
        // both from Skill_Start and from every Skill_Stroke before it, never from the
        // result (CCharSkill.cpp:4543-4555/3615-3618).

        var result = gatheringEngine?.TryGatherForSink(ch, SkillType.Mining, target) ?? default;
        return FinishGather(sink, SkillType.Mining, result, dagger: false);
    }

    /// <summary>What a gathering stage says before its resource check: the tool, the
    /// -1 point, the region, the distance and the line of sight, in the order and with
    /// the messages Skill_Mining (CCharSkill.cpp:1402-1446), Skill_Fishing (:1507-1545)
    /// and Skill_Lumberjack (:1606-1644) use. Null when the stage may go on.</summary>
    internal static string? CheckGatherPreconditions(IActiveSkillSink sink, SkillType skill,
        Point3D target, World.GameWorld world, out Item? tool)
    {
        var ch = sink.Self;
        tool = ResolveGatherTool(sink, skill);
        switch (skill)
        {
            case SkillType.Mining:
            {
                if (tool == null)
                    return Msg.MiningTool;
                if (target.X < 0)
                    return Msg.Mining4;
                // The miner's own region, not the target's (GetRegion(), :1416).
                if (world.FindRegion(ch.Position) is { } area && area.IsFlag(RegionFlag.NoMining))
                    return Msg.Mining2;
                return CheckGatherReach(ch, SkillType.Mining, target, world, 2, alwaysLos: true,
                    Msg.MiningClose, Msg.MiningReach, Msg.MiningLos);
            }
            case SkillType.Fishing:
            {
                // Skill_Fishing checks no tool (:1483-1590): the pole is how the skill is
                // reached, not something the stage asks for. SphereNet also offers the
                // skill from the skill list, so a pole is still required there.
                if (tool == null)
                    return FishingPoleMissing;
                if (target.X < 0)
                    return Msg.Fishing4;
                // In a house, but not on a ship (:1509-1514); never down through a
                // multi's floor (:1515-1519).
                var standingIn = FindMultiRegion(world, ch.Position);
                if (standingIn != null && !standingIn.IsFlag(RegionFlag.Ship))
                    return Msg.Fishing3;
                if (FindMultiRegion(world, target) != null)
                    return Msg.Fishing4;
                return CheckGatherReach(ch, SkillType.Fishing, target, world, 4, alwaysLos: false,
                    Msg.FishingClose, Msg.FishingReach, Msg.FishingLos);
            }
            case SkillType.Lumberjacking:
            {
                if (tool == null)
                    return Msg.LumberjackingTool;
                if (target.X < 0)
                    return Msg.Lumberjacking6;
                return CheckGatherReach(ch, SkillType.Lumberjacking, target, world, 2, alwaysLos: true,
                    Msg.LumberjackingClose, Msg.LumberjackingReach, Msg.LumberjackingLos);
            }
        }
        return null;
    }

    /// <summary>The message for a fishing attempt with no pole at all, reachable only
    /// through SphereNet's skill-list path.</summary>
    private const string FishingPoleMissing = "You need a fishing pole to fish.";

    /// <summary>The gathering distance rules: under 1 tile refuses unless the skill
    /// has SKF_NOMINDIST, beyond the [SKILL] RANGE (default 2 for mining and
    /// lumberjacking, 4 for fishing) refuses, and the line of sight is asked -
    /// always for mining and lumberjacking, for fishing only when ADVANCEDLOS covers
    /// this kind of character, and then with LOS_FISHING (:1536-1544).</summary>
    private static string? CheckGatherReach(Character ch, SkillType skill, Point3D target,
        World.GameWorld world, int defaultRange, bool alwaysLos, string close, string reach, string los)
    {
        int range = SkillEngine.GetUseRange(skill, defaultRange);
        int dist = target.Map != ch.MapIndex ? int.MaxValue : ch.Position.GetDistanceTo(target);
        if (dist < 1 && !SkillEngine.HasFlag(skill, SkillFlag.NoMinDist))
            return close;
        if (dist > range)
            return reach;
        if (alwaysLos)
        {
            if (!world.CanSeeLOSFor(ch, ch.Position, target))
                return los;
        }
        else if (world.UsesAdvancedLos(ch) &&
                 !world.CanSeeLOSFor(ch, ch.Position, target, LosFlags.Fishing, range))
        {
            return los;
        }
        return null;
    }

    /// <summary>A house or ship region at the point (Source-X REGION_TYPE_MULTI).</summary>
    private static World.Regions.Region? FindMultiRegion(World.GameWorld world, Point3D pt)
    {
        foreach (var (region, _) in world.FindAllRegions(pt))
        {
            if (region.IsFlag(RegionFlag.House) || region.IsFlag(RegionFlag.Ship))
                return region;
        }
        return null;
    }

    /// <summary>The tool a gathering stage works with: m_Act_Prv_UID, the pickaxe,
    /// blade or pole whose target cursor started it (CClientTarg.cpp:1809/1890/2264).
    /// A skill reached without one - SphereNet's skill-list path - takes the first
    /// such tool in hand or pack.</summary>
    private static Item? ResolveGatherTool(IActiveSkillSink sink, SkillType skill)
    {
        var ch = sink.Self;
        ItemType[] types = skill switch
        {
            SkillType.Mining => [ItemType.WeaponMacePick],
            SkillType.Fishing => [ItemType.FishPole],
            SkillType.Lumberjacking => [ItemType.WeaponAxe, ItemType.WeaponSword, ItemType.WeaponFence,
                ItemType.WeaponMaceSharp, ItemType.CarpentryChop],
            _ => [],
        };
        if (ch.ActPrv.IsValid && sink.World.FindItem(ch.ActPrv) is { IsDeleted: false } used &&
            Array.IndexOf(types, used.ItemType) >= 0)
            return used;
        return types.Length > 0 ? FindGatherTool(sink, types[0]) : null;
    }

    /// <summary>What the gathering SUCCESS stage says and does with the engine's
    /// answer (Skill_Mining :1449-1481, Skill_Fishing :1548-1589, Skill_Lumberjack
    /// :1647-1692): no bit is DEFMSG_*_1, an empty bit DEFMSG_*_2, a create that made
    /// nothing is the stage's failure message, and a failed skill check is silent.
    /// Mining and lumberjacking announce no catch; fishing names it.</summary>
    private static bool FinishGather(IActiveSkillSink sink, SkillType skill, GatherResult result, bool dagger)
    {
        if (!result.Handled)
        {
            sink.SysMessage(ServerMessages.Get(skill switch
            {
                SkillType.Fishing => Msg.Fishing1,
                SkillType.Lumberjacking => dagger ? Msg.Lumberjacking3 : Msg.Lumberjacking1,
                _ => Msg.Mining1,
            }));
            return false;
        }
        if (result.Depleted)
        {
            sink.SysMessage(ServerMessages.Get(skill switch
            {
                SkillType.Fishing => Msg.Fishing2,
                SkillType.Lumberjacking => dagger ? Msg.Lumberjacking4 : Msg.Lumberjacking2,
                _ => Msg.Mining2,
            }));
            return false;
        }
        if (result.Success && result.Item != null)
        {
            if (skill == SkillType.Fishing)
                // SysMessagef(DEFMSG_FISHING_SUCCESS, name) - CCharSkill.cpp:1581
                sink.SysMessage(ServerMessages.GetFormatted(Msg.FishingSuccess, result.Item.GetName()));
            else if (dagger)
                sink.SysMessage(ServerMessages.Get(Msg.Lumberjacking5));   // :1674
            sink.DeliverItem(result.Item);
            return true;
        }
        if (result.CreateFailed)
        {
            sink.SysMessage(ServerMessages.Get(skill switch
            {
                SkillType.Fishing => Msg.Fishing2,             // :1578
                SkillType.Lumberjacking => Msg.Lumberjacking2, // :1682
                _ => Msg.Mining3,                              // :1471
            }));
        }
        return false;
    }

    /// <summary>Find a gathering tool of the given type in the actor's hands or
    /// backpack (Source-X requires the tool to be present to mine/chop/fish).</summary>
    private static Item? FindGatherTool(IActiveSkillSink sink, ItemType toolType)
    {
        var ch = sink.Self;
        var hand = ch.GetEquippedItem(Layer.OneHanded) ?? ch.GetEquippedItem(Layer.TwoHanded);
        if (hand != null && !hand.IsDeleted && hand.ItemType == toolType)
            return hand;
        return sink.FindBackpackItem(toolType);
    }

    /// <summary>Source-X EF_DamageTools (CServerConfig.h:53, EXPERIMENTAL 0x2000).
    /// Off by default, as upstream's is.</summary>
    public const int EfDamageTools = 0x0002000;

    /// <summary>Whether the shard's EXPERIMENTAL flags set EF_DamageTools. Fed from
    /// sphere.ini at startup.</summary>
    public static bool DamageToolsEnabled { get; set; }

    /// <summary>LOCAL.ITEMDAMAGECHANCE / LOCAL.ITEMDAMAGEAMOUNT seeded for
    /// @SkillSuccess / @Success (CCharSkill.cpp:3931-3932: 25 and 1).</summary>
    public const string ItemDamageChanceLocal = "ITEMDAMAGECHANCE";
    public const string ItemDamageAmountLocal = "ITEMDAMAGEAMOUNT";

    /// <summary>The locals Skill_Done hands @SkillSuccess and @Success
    /// (CCharSkill.cpp:3930-3932), for a script to change before the tool wear.</summary>
    public static SphereNet.Scripting.Variables.VarMap NewSkillSuccessLocals()
    {
        var locals = new SphereNet.Scripting.Variables.VarMap();
        locals.SetInt(ItemDamageChanceLocal, 25);
        locals.SetInt(ItemDamageAmountLocal, 1);
        return locals;
    }

    /// <summary>Tool wear, the only way upstream has it (Skill_Done,
    /// CCharSkill.cpp:3945-3961): after @SkillSuccess / @Success of a SKF_GATHER
    /// skill, and only with EF_DamageTools, the weapon in hand (LAYER_HAND1, else
    /// HAND2) takes LOCAL.ITEMDAMAGEAMOUNT (bounded to 0..its hit points) with
    /// LOCAL.ITEMDAMAGECHANCE percent (clamped 0-100), both read back from the
    /// triggers (null locals = the seeded 25 / 1). Wear on every attempt, on crooks
    /// and instruments, and a uses counter were all invented.</summary>
    public static void DamageGatherToolOnSuccess(Character ch, SkillType skill,
        SphereNet.Scripting.Variables.VarMap? locals, Random random)
    {
        long rawChance = locals?.GetInt(ItemDamageChanceLocal, 25) ?? 25;
        int chance = (int)Math.Clamp(rawChance, 0L, 100L);
        if (!DamageToolsEnabled || !SkillEngine.HasFlag(skill, SkillFlag.Gather) || chance <= 0)
            return;
        var tool = ch.GetEquippedItem(Layer.OneHanded);
        if (tool == null || !ObjBase.IsTypeWeapon(tool.ItemType))
            tool = ch.GetEquippedItem(Layer.TwoHanded);
        if (tool == null || tool.IsDeleted || !ObjBase.IsTypeWeapon(tool.ItemType))
            return;
        if (random.Next(100) >= chance)
            return;
        long rawAmount = locals?.GetInt(ItemDamageAmountLocal, 1) ?? 1;
        int amount = (int)Math.Max(Math.Min(rawAmount, tool.GetHitsCur()), 0L);
        // OnTakeDamage(iAmount <= 0) does nothing (CItem.cpp:5805).
        if (amount > 0)
            Combat.CombatEngine.ApplyDirectItemDamage(tool, amount);
    }

    // -------------------------------------------------------------- Fishing

    /// <summary>Source-X CChar::Skill_Fishing's SUCCESS stage (CCharSkill.cpp:1483-1590).</summary>
    public static bool Fishing(IActiveSkillSink sink, Point3D target, GatheringEngine? gatheringEngine, World.GameWorld world)
    {
        var ch = sink.Self;
        string? refusal = CheckGatherPreconditions(sink, SkillType.Fishing, target, world, out _);
        if (refusal != null)
        {
            sink.SysMessage(ServerMessages.Get(refusal));
            return false;
        }
        FaceSkillTarget(ch, target);

        var result = gatheringEngine?.TryGatherForSink(ch, SkillType.Fishing, target) ?? default;
        return FinishGather(sink, SkillType.Fishing, result, dagger: false);
    }

    // --------------------------------------------------------- Lumberjacking

    /// <summary>Source-X CChar::Skill_Lumberjack's SUCCESS stage (CCharSkill.cpp:
    /// 1592-1692). A fencing weapon (a dagger) hacks kindling instead: one
    /// ITEMID_KINDLING1 and one unit off the bit (:1672-1678).</summary>
    public static bool Lumberjacking(IActiveSkillSink sink, Point3D target, GatheringEngine? gatheringEngine, World.GameWorld world)
    {
        var ch = sink.Self;
        string? refusal = CheckGatherPreconditions(sink, SkillType.Lumberjacking, target, world, out var tool);
        if (refusal != null)
        {
            sink.SysMessage(ServerMessages.Get(refusal));
            return false;
        }
        FaceSkillTarget(ch, target);

        bool dagger = tool?.ItemType == ItemType.WeaponFence;
        var result = gatheringEngine?.TryGatherForSink(ch, SkillType.Lumberjacking, target, kindling: dagger) ?? default;
        return FinishGather(sink, SkillType.Lumberjacking, result, dagger);
    }

    /// <summary>Face the point the skill is working on.
    ///
    /// Upstream turns the character toward m_Act_p on every stroke
    /// (CChar::Skill_Stroke, CCharSkill.cpp:3608) - the same call the forge, the
    /// campfire and the fishing spot get. Nothing here did, so a miner swung at a
    /// rock face while looking somewhere else entirely, and the pick animation
    /// played across the wrong axis.</summary>
    internal static void FaceSkillTarget(Character ch, Point3D target)
    {
        if (target.X == ch.X && target.Y == ch.Y)
            return;
        var dir = ch.Position.GetDirectionTo(target);
        if (ch.Direction == dir)
            return;
        ch.FaceToward(target); // UpdateDir: watchers and the player's own client
    }

    // ---------------------------------------------------------- Musicianship

    /// <summary>The START stage of a bard skill - Skill_Musicianship, Skill_Peacemaking,
    /// Skill_Enticement and Skill_Provocation each play the instrument at START
    /// (CCharSkill.cpp:1787, 1830, 1956, 2071), when the skill begins, and what it
    /// answers is m_Act_Difficulty for the roll after the DELAY. Negative is a refusal
    /// (no instrument, a poor tune, a creature out of sight or not to be provoked).
    /// <paramref name="act"/> is ACT (the instrument for musicianship, the creature
    /// for enticement, the target for provocation), <paramref name="provoked"/> ACTPRV.
    /// The whole skill used to run after the DELAY, so the tune came seconds late and
    /// a missing instrument was only said at the end.</summary>
    public static int StartStage(IActiveSkillSink sink, SkillType skill, ObjBase? act, Character? provoked,
        Point3D? point = null) =>
        skill switch
        {
            SkillType.Herding => HerdingStart(sink, act as Character, point),
            SkillType.Musicianship => MusicianshipStart(sink, act as Item),
            SkillType.Peacemaking => PeacemakingStart(sink),
            SkillType.Enticement => EnticementStart(sink, act as Character),
            SkillType.Provocation => ProvocationStart(sink, provoked, act as Character),
            SkillType.RemoveTrap => RemoveTrapStart(sink, act as Item),
            SkillType.Lockpicking => LockpickingStart(sink, act as Item),
            SkillType.Snooping => SnoopingStart(sink, act as Item),
            SkillType.Stealing => StealingStart(sink, act),
            SkillType.Taming => TamingStart(sink, act as Character),
            SkillType.Hiding => HidingStart(sink),
            SkillType.Meditation => MeditationStart(sink),
            SkillType.SpiritSpeak => sink.Random.Next(90),
            SkillType.DetectingHidden => 10,
            SkillType.Healing or SkillType.Veterinary =>
                HealingStart(sink, HealPatientOf(sink.World, act), skill, act as Item),
            _ => 0,
        };

    /// <summary>The patient of a bandage: a character itself, or the owner of a corpse
    /// (m_uidLink) while still dead.</summary>
    public static Character? HealPatientOf(GameWorld world, ObjBase? act)
    {
        if (act is Character c) return c;
        if (act is Item corpse && corpse.ItemType == ItemType.Corpse &&
            corpse.TryGetTag("OWNER_UID", out string? o) && ScriptNumber.TryParseUInt(o, out uint uid))
        {
            var owner = world.FindChar(new Serial(uid));
            if (owner != null && owner.IsDead && !owner.IsDeleted)
                return owner;
        }
        return null;
    }

    /// <summary>The skills whose START stage runs at Skill_Start here: the bard skills,
    /// which play the instrument, herding, which judges the creature and the spot,
    /// remove trap, which judges the trap, and healing and veterinary, which judge the
    /// patient and apply the bandage.</summary>
    public static bool HasStartStage(SkillType skill) =>
        skill is SkillType.Musicianship or SkillType.Peacemaking or SkillType.Enticement or SkillType.Provocation
            or SkillType.Herding or SkillType.RemoveTrap or SkillType.Healing or SkillType.Veterinary
            or SkillType.Lockpicking or SkillType.Snooping or SkillType.Stealing or SkillType.Taming
            or SkillType.Hiding or SkillType.DetectingHidden or SkillType.Meditation or SkillType.SpiritSpeak;

    /// <summary>Skill_Musicianship START (CCharSkill.cpp:1786-1787): play ACT, the
    /// instrument double-clicked, or any carried, against rand(90).</summary>
    private static int MusicianshipStart(IActiveSkillSink sink, Item? instrument) =>
        UsePlayMusic(sink, RandVal(sink.Random, 90), musicianshipActive: true,
            instrument?.ItemType == ItemType.Musical ? instrument : null);

    /// <summary>Source-X CChar::Skill_Musicianship (CCharSkill.cpp:1772-1790): START
    /// plays the instrument against rand(90) through Use_PlayMusic, and what that
    /// returns is the skill's difficulty. The fixed 40 was invented.
    /// <paramref name="startDifficulty"/> is what START answered when it already ran.</summary>
    public static bool Musicianship(IActiveSkillSink sink, int? startDifficulty = null)
    {
        int difficulty = startDifficulty ?? MusicianshipStart(sink, null);
        if (difficulty < 0)
            return false;
        return SkillEngine.UseQuick(sink.Self, SkillType.Musicianship, difficulty);
    }

    /// <summary>Source-X CChar::Use_PlayMusic (CCharUse.cpp:707-738): find an
    /// instrument (DEFMSG_MUSICANSHIP_NOTOOL and -2 without one), roll Musicianship
    /// against <paramref name="difficulty"/> - gaining only when Musicianship is not
    /// itself the skill being used, which instead takes the failure's experience
    /// by hand - and play the instrument's good or poor tune. Returns the difficulty
    /// on success, -1 (DEFMSG_MUSICANSHIP_POOR) on failure.</summary>
    private static int UsePlayMusic(IActiveSkillSink sink, int difficulty, bool musicianshipActive,
        Item? instrument = null)
    {
        instrument ??= FindMusicalInstrument(sink);
        if (instrument == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.MusicanshipNotool));
            return -2;
        }

        var ch = sink.Self;
        bool played = SkillEngine.UseQuick(ch, SkillType.Musicianship, difficulty,
            allowGain: !musicianshipActive);
        PlayInstrument(sink, instrument, played);
        if (played)
            return difficulty;

        if (musicianshipActive)
            SkillEngine.GainExperience(ch, SkillType.Musicianship, -difficulty);
        sink.SysMessage(ServerMessages.Get(Msg.MusicanshipPoor));
        return -1;
    }

    /// <summary>The instrument's own tune - Source-X Use_PlayMusic plays
    /// CItem::Use_Music after the Musicianship roll: the definition's TDATA1 when it
    /// was played well, TDATA2 when poorly (CCharUse.cpp:728-729, CItem.cpp:4620,
    /// CItemBase.h:141). Every bard skill goes through it - musicianship,
    /// peacemaking, enticement and provocation (CCharSkill.cpp:1787/1830/1956/2071).
    /// A fixed drum-beat on musicianship alone, and silence for the other three, is
    /// what this used to play.</summary>
    private static void PlayInstrument(IActiveSkillSink sink, Item instrument, bool playedWell)
    {
        uint sound = playedWell ? instrument.TData1 : instrument.TData2;
        if (sound is > 0 and <= ushort.MaxValue)
            sink.Sound((ushort)sound);
    }

    // ----------------------------------------------------------- Peacemaking

    /// <summary>Skill_Peacemaking START (CCharSkill.cpp:1828-1838): play against
    /// rand(40); a zero difficulty is re-rolled as rand(40).</summary>
    private static int PeacemakingStart(IActiveSkillSink sink)
    {
        int difficulty = UsePlayMusic(sink, RandVal(sink.Random, 40), musicianshipActive: false);
        if (difficulty < 0)
            return difficulty;
        return difficulty == 0 ? RandVal(sink.Random, 40) : difficulty;
    }

    /// <summary>Source-X CChar::Skill_Peacemaking (CCharSkill.cpp:1792-1899).
    ///
    /// START plays against rand(40) (no instrument aborts, a poor tune fails) and a
    /// zero difficulty is re-rolled as rand(40). SUCCESS listens around the bard -
    /// RANGE, or Peacemaking/100 + 2 tiles - and settles on the FIRST creature it
    /// can see: one whose own Peacemaking (averaged with TAG.BARDING.DIFF) beats
    /// the bard's ignores the song, one whose Provocation does disobeys it (and an
    /// evil one turns on the bard), and anything else stops fighting. Nobody in
    /// earshot is a failure. Calming every NPC in eight tiles was invented.</summary>
    public static bool Peacemaking(IActiveSkillSink sink, Character? target = null, int? startDifficulty = null)
    {
        var ch = sink.Self;
        int difficulty = startDifficulty ?? PeacemakingStart(sink);
        if (difficulty < 0)
            return false;
        if (!SkillEngine.UseQuick(ch, SkillType.Peacemaking, difficulty))
            return false;

        int peace = SkillEngine.GetAdjustedSkill(ch, SkillType.Peacemaking);
        int radius = SkillEngine.GetUseRange(SkillType.Peacemaking, peace / 100 + 2);
        foreach (var creature in sink.World.GetCharsInRange(ch.Position, radius).ToList())
        {
            if (creature == ch || creature.IsDeleted || !CanSeeChar(sink.World, ch, creature))
                continue;

            int barding = BardingDiff(creature);
            int peaceDiff = SkillEngine.GetAdjustedSkill(creature, SkillType.Peacemaking);
            if (barding != 0)
                peaceDiff = (peaceDiff + barding) / 2;

            if (peaceDiff > peace)
            {
                sink.SysMessage($"{creature.GetName()} {ServerMessages.Get(Msg.PeacemakingIgnore)}.");
            }
            else
            {
                int provoDiff = SkillEngine.GetAdjustedSkill(creature, SkillType.Provocation);
                if (barding != 0)
                    provoDiff = (provoDiff + barding) / 2;
                if (provoDiff > peace)
                {
                    sink.SysMessage($"{creature.GetName()} {ServerMessages.Get(Msg.PeacemakingDisobey)}.");
                    if (AI.NpcAI.NotoIsEvil(creature, sink.World))
                        FightAttack(creature, ch);
                }
                else
                {
                    FightClearAll(creature);
                }
            }
            return true;
        }
        return false;
    }

    // ----------------------------------------------------------- Enticement

    /// <summary>Every Skill_Enticement stage first asks for ACT and that the bard can
    /// see it (CCharSkill.cpp:1914-1916); there is no reach beyond sight.</summary>
    private static bool EnticementSees(IActiveSkillSink sink, Character? target) =>
        target != null && !target.IsDead && !target.IsDeleted && CanSeeChar(sink.World, sink.Self, target);

    /// <summary>Skill_Enticement START (CCharSkill.cpp:1920-1965): play against
    /// TAG.BARDING.DIFF / 18 (default 40); a zero difficulty is re-rolled as rand(40).</summary>
    private static int EnticementStart(IActiveSkillSink sink, Character? target)
    {
        if (!EnticementSees(sink, target))
            return -1;
        int baseDiff = BardingDiff(target!) is var tagDiff && tagDiff != 0 ? tagDiff / 18 : 40;
        int difficulty = UsePlayMusic(sink, RandVal(sink.Random, baseDiff), musicianshipActive: false);
        if (difficulty < 0)
            return difficulty;
        return difficulty == 0 ? RandVal(sink.Random, 40) : difficulty;
    }

    /// <summary>Source-X CChar::Skill_Enticement (CCharSkill.cpp:1901): lure a
    /// creature to the bard. The tune is played against TAG.BARDING.DIFF (divided by
    /// 18, default 40) and needs an instrument; on success a player cannot be lured
    /// and neither can a creature at war, and anything else walks to where the bard
    /// stands - running when it is further than the skill's RANGE (default 3).
    /// The creature need only be in sight: an invented 8-tile reach refused it with
    /// an English line of its own.</summary>
    public static bool Enticement(IActiveSkillSink sink, Character? target, int? startDifficulty = null)
    {
        var ch = sink.Self;
        if (!EnticementSees(sink, target))
            return false;

        int difficulty = startDifficulty ?? EnticementStart(sink, target);
        if (difficulty < 0)
            return false;
        if (!SkillEngine.UseQuick(ch, SkillType.Enticement, difficulty))
            return false;

        if (target!.IsPlayer)
        {
            sink.SysMessage(ServerMessages.Get(Msg.EnticementPlayer));
            return false;
        }
        if (target.IsStatFlag(StatFlag.War))
        {
            sink.SysMessage($"{target.GetName()} {ServerMessages.Get(Msg.EnticementBattle)}.");
            return false;
        }

        int maxRange = SkillEngine.GetUseRange(SkillType.Enticement, 3);
        if (maxRange <= 0) maxRange = 3;
        target.ActP = ch.Position;
        target.Action = (SkillType)(target.Position.GetDistanceTo(ch.Position) > maxRange
            ? NpcAction.RunTo : NpcAction.GoTo);
        target.NextNpcActionTime = 0;
        return true;
    }

    // ----------------------------------------------------------- Provocation

    /// <summary>What every Skill_Provocation stage asks first (CCharSkill.cpp:2014-2032):
    /// neither creature may be the bard, the other, a pet, conjured, stone, dead or
    /// invulnerable (DEFMSG_PROVOCATION_UPSET), nor a player (DEFMSG_PROVOCATION_PLAYER),
    /// and the bard must see both.</summary>
    private static bool ProvocationAllowed(IActiveSkillSink sink, Character? prov, Character? targ)
    {
        var ch = sink.Self;
        const StatFlag untouchable = StatFlag.Pet | StatFlag.Conjured | StatFlag.Stone |
            StatFlag.Dead | StatFlag.Invul;
        if (prov == null || targ == null || prov == ch || targ == ch || prov == targ ||
            prov.IsDeleted || targ.IsDeleted || prov.IsDead || targ.IsDead ||
            prov.IsStatFlag(untouchable) || targ.IsStatFlag(untouchable))
        {
            sink.SysMessage(ServerMessages.Get(Msg.ProvocationUpset));
            return false;
        }
        if (prov.IsPlayer || targ.IsPlayer)
        {
            sink.SysMessage(ServerMessages.Get(Msg.ProvocationPlayer));
            return false;
        }
        return CanSeeChar(sink.World, ch, prov) && CanSeeChar(sink.World, ch, targ);
    }

    /// <summary>Skill_Provocation START (:2045-2090): play against the TARGET's
    /// TAG.BARDING.DIFF / 18 (default 40); no instrument refuses, a zero difficulty
    /// becomes the provoked creature's INT, and one at least as good a provoker as
    /// the bard makes it 0. A poor tune stays -1, bound to fail.</summary>
    private static int ProvocationStart(IActiveSkillSink sink, Character? prov, Character? targ)
    {
        if (!ProvocationAllowed(sink, prov, targ))
            return -3;
        return ProvocationStartStage(sink, prov!, targ!);
    }

    private static int ProvocationStartStage(IActiveSkillSink sink, Character prov, Character targ)
    {
        var ch = sink.Self;
        int baseDiff = BardingDiff(targ) is var tagDiff && tagDiff != 0 ? tagDiff / 18 : 40;
        int difficulty = UsePlayMusic(sink, RandVal(sink.Random, baseDiff), musicianshipActive: false);
        if (difficulty < -1)
            return difficulty;
        if (difficulty == 0)
            difficulty = Combat.CombatEngine.EffectiveInt(prov);
        if (SkillEngine.GetAdjustedSkill(prov, SkillType.Provocation) >=
            SkillEngine.GetAdjustedSkill(ch, SkillType.Provocation))
            difficulty = 0; // "cannot provoke more experienced provoker" (:2082)
        return difficulty;
    }

    /// <summary>Source-X CChar::Skill_Provocation (CCharSkill.cpp:1999-2157).
    /// <paramref name="target"/> is the creature being provoked (m_Act_Prv_UID) and
    /// <paramref name="provokeAgainst"/> the one it is turned on (m_Act_UID).
    ///
    /// Neither may be the bard, each other, dead, a pet, conjured, stone or
    /// invulnerable (DEFMSG_PROVOCATION_UPSET), nor a player
    /// (DEFMSG_PROVOCATION_PLAYER). START plays against the TARGET's
    /// TAG.BARDING.DIFF/18 (default 40); a zero difficulty becomes the provoked
    /// creature's INT, and one at least as good a provoker as the bard makes it 0.
    /// FAIL turns the provoked creature on the bard. SUCCESS: a creature of karma
    /// rand(1000..10000) or better refuses; the target is attacked by the bard's
    /// doing; out of RANGE (default 14) the provoked evil turns on the bard; two of
    /// the same ally group turn on the bard together (DEFMSG_PROVOCATION_KIND); a
    /// good provoked creature is a crime and refuses; a good target is a crime but
    /// the fight goes ahead.</summary>
    public static bool Provocation(IActiveSkillSink sink, Character? target,
        Character? provokeAgainst = null, int? startDifficulty = null)
    {
        var ch = sink.Self;
        var prov = target;
        var targ = provokeAgainst;
        if (!ProvocationAllowed(sink, prov, targ))
            return false;

        int difficulty;
        if (startDifficulty.HasValue)
            difficulty = startDifficulty.Value;
        else
        {
            difficulty = ProvocationStartStage(sink, prov!, targ!);
            if (difficulty < -1)
                return false;
        }
        bool isAlly = AI.NpcAI.GetAllyGroup(prov!.BodyId) == AI.NpcAI.GetAllyGroup(targ!.BodyId);

        // A START that came back negative is bound to fail (Skill_Done :3928).
        if (difficulty < 0 || !SkillEngine.UseQuick(ch, SkillType.Provocation, difficulty))
        {
            FightAttack(prov, ch); // FAIL (:2092-2096)
            return false;
        }

        // SUCCESS (:2098-2150).
        if (prov.Karma >= RandVal2(sink.Random, 1000, 10000))
        {
            prov.EmoteObject(ServerMessages.Get(Msg.ProvocationEmote1));
            return false;
        }
        prov.EmoteObject(ServerMessages.Get(Msg.ProvocationEmote2));

        // "He realizes that you are the real bad guy as well" (:2104): recorded as an
        // ordered attack (fCommandPet), so the target does not turn on the bard.
        if (!targ.OnAttackedBy(ch, commandPet: true))
            return false;
        prov.Memory_AddObjTypes(ch.Uid, MemoryType.Aggreived | MemoryType.IrritatedBy);

        int maxRange = SkillEngine.GetUseRange(SkillType.Provocation, 14); // UO_MAP_VIEW_SIGHT
        bool provEvil = AI.NpcAI.NotoIsEvil(prov, sink.World);
        if (prov.Position.GetDistanceTo(targ.Position) > maxRange ||
            prov.Position.GetDistanceTo(ch.Position) > maxRange)
        {
            if (provEvil)
                FightAttack(prov, ch);
            return false;
        }

        if (isAlly)
        {
            if (provEvil)
            {
                FightAttack(prov, ch);
                FightAttack(targ, ch);
            }
            sink.SysMessage(ServerMessages.Get(Msg.ProvocationKind));
            return false;
        }

        // A good provoked creature is a crime and refuses; a good target is a crime
        // but the fight goes ahead. Both run CheckCrimeSeen(SKILL_PROVOCATION,
        // nullptr, ...) (CCharSkill.cpp:2135-2144): no mark, and each witness rolls
        // the non-thief perception chance for the skill (Calc_CrimeSeen,
        // CResourceCalc.cpp:476) instead of noticing unconditionally.
        if (IsNotoGood(sink.World, prov, ch))
        {
            CrimeWitnessService.CheckCrimeSeen(sink.World, ch, null, SkillType.Provocation, sink.Random);
            return false; // "can't provoke a good target!"
        }
        if (IsNotoGood(sink.World, targ, ch))
            CrimeWitnessService.CheckCrimeSeen(sink.World, ch, null, SkillType.Provocation, sink.Random);

        FightAttack(prov, targ);
        return true;
    }

    /// <summary>TAG.BARDING.DIFF, 0 when unset.</summary>
    private static int BardingDiff(Character ch) =>
        ch.TryGetTag("BARDING.DIFF", out string? raw) && ScriptNumber.TryParseInt(raw, out int v) ? v : 0;

    /// <summary>CSRand::GetVal2: min..max inclusive (CSRand.cpp:47).</summary>
    private static int RandVal2(Random random, int min, int max)
    {
        if (min > max) (min, max) = (max, min);
        return random.Next(min, max + 1);
    }

    /// <summary>The bard seeing a creature (CChar::CanSee, CCharStatus.cpp:1074-1120): not
    /// hidden from them (a GM sees all) and within their sight range. CanSee asks no
    /// line of sight; a wall between them refused enticement and provocation.</summary>
    private static bool CanSeeChar(GameWorld world, Character viewer, Character other)
    {
        if (other.IsStatFlag(StatFlag.Hidden | StatFlag.Invisible) && viewer.PrivLevel < PrivLevel.GM)
            return false;
        return other.MapIndex == viewer.MapIndex && viewer.Position.GetDistSight(other.Position) <= viewer.VisualRange;
    }

    /// <summary>pSubject->Noto_GetFlag(pViewer) == NOTO_GOOD (CCharSkill.cpp:2135/2142),
    /// through the engine's own notoriety computation.</summary>
    private static bool IsNotoGood(GameWorld world, Character subject, Character viewer) =>
        Clients.GameClient.ComputeNotoriety(world, viewer, subject) == 1;

    /// <summary>Source-X CChar::Fight_Attack for a creature a bard turned: it goes on
    /// the attacker list through @Attack - Attacker_Add records the fight in memory
    /// (Memory_Fight_Start, CCharAttacker.cpp:58) - the target remembers being
    /// irritated by it (CCharFight.cpp:1472), and it takes the target. Without the
    /// fight memory, a monster set on another monster weighed only MONSTERFIGHT's
    /// -50 hostility and fled from it instead of fighting.</summary>
    private static void FightAttack(Character attacker, Character victim)
    {
        if (attacker == victim || attacker.IsDead || victim.IsDead)
            return;
        bool newAttacker = attacker.CombatState.IndexOfAttacker(victim.Uid) < 0;
        if (!attacker.CombatState.BeginFightWith(victim, toldByMaster: false))
            return;
        if (newAttacker)
            attacker.Memory_Fight_Start(victim);
        victim.Memory_AddObjTypes(attacker.Uid, MemoryType.IrritatedBy);
        attacker.FightTarget = victim.Uid;
        attacker.SetStatFlag(StatFlag.War);
        attacker.NextNpcActionTime = 0;
        Character.WakeNpc?.Invoke(attacker);
    }

    /// <summary>Source-X CChar::Fight_ClearAll (CCharFight.cpp:1340): drop the fight
    /// target and the attacker list, and leave war mode.</summary>
    private static void FightClearAll(Character ch)
    {
        ch.FightTarget = Serial.Invalid;
        ch.ClearAttackers();
        ch.ClearStatFlag(StatFlag.War);
    }

    private static Item? FindMusicalInstrument(IActiveSkillSink sink)
    {
        var packed = sink.FindBackpackItem(ItemType.Musical);
        if (packed != null) return packed;
        var hand = sink.Self.GetEquippedItem(Layer.OneHanded);
        return hand?.ItemType == ItemType.Musical ? hand : null;
    }

    // --------------------------------------------------------------- Tinkering

    /// <summary>Source-X weapon-dclick repair via tinkering skill.</summary>
    public static bool RepairItem(IActiveSkillSink sink, Item? target)
    {
        var ch = sink.Self;
        if (target == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairUnk));
            return false;
        }

        if (!CanReachItem(ch, target, sink.World, SkillType.Tinkering, 2))
        {
            sink.SysMessage("You must have that item in your possession.");
            return false;
        }

        if (target.IsEquipped)
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairWorn));
            return false;
        }

        // ATTR_CANNOTREPAIR fails Armor_IsRepairable first (CItem.cpp:4851, Use_Repair
        // CCharUse.cpp:748).
        var def = DefinitionLoader.GetItemDef(target.BaseId);
        if (target.IsAttr(ObjAttributes.CannotRepair) || (def != null && !def.Repair))
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairNot));
            return false;
        }

        // Arms Lore comes first and is its own stage: the smith has to recognise the
        // piece before working on it, and failing that ends the repair before the
        // craft skill is ever rolled (Use_Repair, CCharUse.cpp:764). SphereNet went
        // straight to the craft skill, so a smith with no Arms Lore repaired anyway.
        // The gain is applied afterwards, as the reference does - negative on the
        // failure that stops here.
        int armsLoreDiff = sink.Random.Next(30);
        if (!SkillEngine.UseQuick(ch, SkillType.ArmsLore, armsLoreDiff, allowGain: false))
        {
            SkillEngine.GainExperience(ch, SkillType.ArmsLore, -armsLoreDiff);
            sink.SysMessage(ServerMessages.Get(Msg.RepairUnk));
            return false;
        }

        // Source-X Use_Repair: only items that actually have hitpoints can be
        // repaired. Inventing (and persisting) a 50-point pool here permanently
        // turned a never-wearing item into breakable gear.
        int maxHits = target.GetHitsMax();
        if (maxHits <= 0)
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairFull));
            return false;
        }

        int curHits = target.GetHitsCur();
        if (curHits >= maxHits)
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairFull));
            return false;
        }

        // Source-X Use_Repair (CCharUse.cpp:792-852): difficulty is the item's
        // SKILLMAKE main-skill level scaled by the damage percent (floor: a
        // quarter of that level), rolled against THAT craft skill. Success is
        // a FULL repair; failure has a 1/6 chance to lower max durability and
        // otherwise a 1/3 chance to chip a point.
        // Work needs an anvil within two tiles (:781) - the reference looks for a
        // dynamic item, a static or the terrain of that type.
        if (!HasAnvilNearby(sink, ch))
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairAnvil));
            return false;
        }

        int damagePercent = (maxHits - curHits) * 100 / Math.Max(1, maxHits);

        // Raw materials: half the damage percentage of what the piece is made of,
        // tested before any work is done and spent afterwards (:794/:854). SphereNet
        // repaired out of thin air and charged nothing at all.
        var recipe = sink.Crafting?.TryGetRecipe(target.BaseId)
            ?? (def != null ? sink.Crafting?.GetRecipe(def.Id.Index) : null);
        if (recipe != null &&
            !Crafting.CraftingEngine.TryConsumeResourcePart(ch, recipe, damagePercent / 2, test: true))
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairLack2));
            return false;
        }

        var (repairSkill, skillLevel) = ResolveRepairSkill(def);
        int difficulty = Math.Max(skillLevel * damagePercent / 100, skillLevel / 4);

        SkillEngine.GainExperience(ch, SkillType.ArmsLore, armsLoreDiff);

        if (!SkillEngine.UseQuick(ch, repairSkill, difficulty))
        {
            if (sink.Random.Next(6) == 0)
            {
                target.HitsMax = Math.Max(1, maxHits - 1);
                target.HitsCur = Math.Max(0, curHits - 1);
                sink.SysMessage(ServerMessages.Get(Msg.Repair2));
            }
            else if (sink.Random.Next(3) == 0)
            {
                target.HitsCur = Math.Max(0, curHits - 1);
                sink.SysMessage(ServerMessages.Get(Msg.Repair3));
            }
            else
            {
                sink.SysMessage(ServerMessages.Get(Msg.Repair4));
            }

            // A botched repair still costs materials, on a random part of the damage
            // (:850/:854).
            if (recipe != null)
                Crafting.CraftingEngine.TryConsumeResourcePart(
                    ch, recipe, sink.Random.Next(damagePercent) / 2, test: false);
            return false;
        }

        target.HitsCur = maxHits; // Source-X: success restores to full
        if (recipe != null)
            Crafting.CraftingEngine.TryConsumeResourcePart(ch, recipe, damagePercent / 2, test: false);
        sink.SysMessage(ServerMessages.GetFormatted(Msg.RepairMsg, "You repair", target.Name ?? "the item"));
        return true;
    }

    /// <summary>An anvil within two tiles - dynamic item, static or terrain, as
    /// Source-X FindItemTypeNearby searches (CWorldMap.cpp:663).</summary>
    private static bool HasAnvilNearby(IActiveSkillSink sink, Character ch)
    {
        foreach (var near in sink.World.GetItemsInRange(ch.Position, 2))
        {
            if (near.ItemType == ItemType.Anvil)
                return true;
        }

        var md = sink.World.MapData;
        if (md == null) return false;

        for (short dx = -2; dx <= 2; dx++)
        {
            for (short dy = -2; dy <= 2; dy++)
            {
                short x = (short)(ch.X + dx), y = (short)(ch.Y + dy);
                foreach (var st in md.GetStatics(ch.MapIndex, x, y))
                {
                    if (DefinitionLoader.GetItemDef(st.TileId)?.Type == ItemType.Anvil)
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>The item's SKILLMAKE main craft skill and its level on the
    /// 0-100 scale (Source-X Use_Repair reads m_SkillMake). No SKILLMAKE →
    /// Tinkering at 50, the pre-parity blanket.</summary>
    private static (SkillType Skill, int Level) ResolveRepairSkill(SphereNet.Scripting.Definitions.ItemDef? def)
    {
        if (def != null && !string.IsNullOrWhiteSpace(def.SkillMakeRaw))
        {
            foreach (var part in SphereNet.Scripting.Resources.ResourceQtyList.Parse(def.SkillMakeRaw))
            {
                if (SphereNet.Game.Definitions.SkillNames.TryResolve(part.Name, out var sk))
                    return (sk, SphereNet.Scripting.Resources.ResourceQtyList.SkillValue(part) / 10);
            }
        }
        return (SkillType.Tinkering, 50);
    }
}
