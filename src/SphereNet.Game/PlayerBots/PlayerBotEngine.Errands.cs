using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Errands players run: the bank, a rune home, company on the road.
/// <list type="bullet">
/// <item><b>Bank.</b> A bot at a bank opens its box from the banker's context menu
/// ("Open Bankbox", as a player does) and moves gold by hand: carrying more than
/// <see cref="BankDepositAbove"/> it puts the surplus in, carrying less than
/// <see cref="BankWithdrawBelow"/> it takes up to <see cref="BankWithdrawTo"/> out.</item>
/// <item><b>A rune home.</b> A caster with the magery for it marks a blank rune at its
/// home town's bank (the Mark spell through its client: mana, reagents and skill as for
/// anyone) and later recalls on it to get home from where no road leads (an island, a
/// gate's landing) or when stranded off the roads.</item>
/// <item><b>Company.</b> A bot setting off sometimes takes another idle bot standing by
/// along; the companion goes to the same place, waits when it gets ahead and runs when
/// it falls behind, until the leader arrives.</item>
/// </list>
/// Nothing is made out of nothing except the blank rune itself (a few coins' worth).
/// </summary>
public sealed partial class PlayerBotEngine
{
    public const string RuneTag = "PLAYERBOT.RUNE";

    /// <summary>Gold carried above which the surplus goes into the bank.</summary>
    public int BankDepositAbove { get; set; } = 1500;
    /// <summary>Gold carried below which some is taken out of the bank.</summary>
    public int BankWithdrawBelow { get; set; } = 200;
    /// <summary>How much a withdrawal brings the purse up to.</summary>
    public int BankWithdrawTo { get; set; } = 500;
    /// <summary>Percent of trips a bot asks an idle bot standing by to come along on.</summary>
    public int CompanyPercent { get; set; } = 15;

    public long WindowDeposits { get; private set; }
    public long WindowWithdrawals { get; private set; }
    public long WindowMarks { get; private set; }
    /// <summary>Marks that left the rune marked, and those that did not (fizzle, no
    /// reagents, no mana).</summary>
    public long WindowMarksTaken { get; private set; }
    public long WindowMarksFailed { get; private set; }
    public long WindowRecalls { get; private set; }
    public long WindowCompanions { get; private set; }

    private const ushort BankboxEntry = 120; // the banker's "Open Bankbox" context entry

    // ------------------------------------------------------------------
    //  Bank
    // ------------------------------------------------------------------

    /// <summary>One act of banking at a bank; false when there is nothing to do.</summary>
    private bool ActBank(BotMind mind, long now)
    {
        var ch = mind.Char;
        var client = mind.Client;
        if (client == null || mind.At == null || mind.Scene != null ||
            !mind.At.Type.Equals("Bank", StringComparison.OrdinalIgnoreCase))
            return false;
        if (mind.BankStage == 0)
        {
            if (now < mind.NextBankMs) return false;
            mind.NextBankMs = now + 600_000;
            // The purse is the pack (the bank box is worn too, and does not count).
            long carried = ch.Backpack != null ? GoldIn(ch.Backpack) : 0;
            var box = ch.GetEquippedItem(Layer.BankBox);
            long banked = box != null ? GoldIn(box) : 0;
            mind.BankDeposit = carried > BankDepositAbove ? carried - _rng.Next(300, 600) : 0;
            mind.BankWithdraw = carried < BankWithdrawBelow && banked > 0 ? Math.Min(banked, BankWithdrawTo - carried) : 0;
            if (mind.BankDeposit <= 0 && mind.BankWithdraw <= 0) return false;
            mind.Banker = FindBanker(ch);
            if (mind.Banker == null) return false;
            mind.BankStage = 1;
            mind.BankUntilMs = now + 40_000;
        }
        var banker = mind.Banker;
        if (banker == null || banker.IsDeleted || banker.IsDead || now > mind.BankUntilMs)
        {
            EndBanking(mind);
            return false;
        }
        int dist = ch.Position.GetDistanceTo(banker.Position);
        if (dist > 3)
        {
            bool walked = StepToward(mind, banker.Position, run: false, searchWithin: 16);
            if (mind.NoGainMs("banker", banker.Position, dist, now) > 15_000)
            {
                WindowGiveUps++;
                EndBanking(mind);
                return false;
            }
            mind.NextActMs = now + StepDelay(mind, walked, false, now);
            return true;
        }
        switch (mind.BankStage)
        {
            case 1:
                // The banker's context menu, "Open Bankbox".
                client.HandleContextMenuResponse(banker.Uid.Value, BankboxEntry);
                mind.BankStage = 2;
                mind.NextActMs = now + _rng.Next(800, 1600);
                return true;
            default:
                var box = ch.GetEquippedItem(Layer.BankBox);
                if (box != null && ch.Backpack is { } pack)
                {
                    if (mind.BankDeposit > 0 && FindGold(pack) is { } coins)
                    {
                        int amount = (int)Math.Min(mind.BankDeposit, coins.Amount);
                        client.HandleItemPickup(coins.Uid.Value, (ushort)amount);
                        client.HandleItemDrop(coins.Uid.Value, (short)_rng.Next(44, 140), (short)_rng.Next(65, 140), 0, box.Uid.Value);
                        if (coins.ContainedIn == box.Uid)
                        {
                            WindowDeposits++;
                            mind.Note(now, $"banked {amount} gold");
                        }
                        else client.ReleaseDraggedItem(toGround: false);
                    }
                    else if (mind.BankWithdraw > 0 && FindGold(box) is { } savings)
                    {
                        int amount = (int)Math.Min(mind.BankWithdraw, savings.Amount);
                        client.HandleItemPickup(savings.Uid.Value, (ushort)amount);
                        client.HandleItemDrop(savings.Uid.Value, (short)_rng.Next(44, 140), (short)_rng.Next(65, 140), 0, pack.Uid.Value);
                        if (savings.ContainedIn == pack.Uid)
                        {
                            WindowWithdrawals++;
                            mind.Note(now, $"took {amount} gold out of the bank");
                        }
                        else client.ReleaseDraggedItem(toGround: false);
                    }
                }
                EndBanking(mind);
                mind.NextActMs = now + _rng.Next(1500, 4000);
                return true;
        }
    }

    private void EndBanking(BotMind mind)
    {
        mind.BankStage = 0;
        mind.Banker = null;
        mind.BankDeposit = mind.BankWithdraw = 0;
    }

    private Character? FindBanker(Character ch)
    {
        Character? best = null;
        int bestD = int.MaxValue;
        foreach (var c in _world.GetCharsInRange(ch.Position, 12))
        {
            if (c.IsDeleted || c.IsDead || c.IsPlayer || c.NpcBrain != NpcBrainType.Banker || c.MapIndex != ch.MapIndex) continue;
            int d = ch.Position.GetDistanceTo(c.Position);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    private static Item? FindGold(Item container)
    {
        Item? best = null;
        foreach (var item in container.Contents)
            if (!item.IsDeleted && item.ItemType == ItemType.Gold && (best == null || item.Amount > best.Amount))
                best = item;
        return best;
    }

    private static long GoldIn(Item container)
    {
        long n = 0;
        foreach (var item in container.Contents)
            if (!item.IsDeleted && item.ItemType == ItemType.Gold) n += item.Amount;
        return n;
    }

    // ------------------------------------------------------------------
    //  A rune home
    // ------------------------------------------------------------------

    /// <summary>The bot's own rune, marked or still blank, if it is in its pack.</summary>
    private Item? OwnRune(Character ch)
    {
        if (!ch.TryGetTag(RuneTag, out string? tag) || !uint.TryParse(tag, out uint uid)) return null;
        var rune = _world.FindItem(new Serial(uid));
        if (rune == null || rune.IsDeleted || rune.ItemType != ItemType.Rune || rune.GetTopLevelObj() != ch)
        {
            ch.RemoveTag(RuneTag);
            return null;
        }
        return rune;
    }

    private static bool KnowsTravel(BotMind mind) =>
        mind.IsCaster && mind.Char.GetSkill(SkillType.Magery) >= 500;

    /// <summary>At its home town's bank, a caster with no marked rune marks one there.</summary>
    private bool TryMarkHomeRune(BotMind mind, long now)
    {
        var ch = mind.Char;
        if (mind.MarkCheckMs != 0 && now >= mind.MarkCheckMs)
        {
            mind.MarkCheckMs = 0;
            if (OwnRune(ch) is { HasRuneMark: true })
            {
                WindowMarksTaken++;
                mind.Note(now, "its rune is marked");
            }
            else
            {
                WindowMarksFailed++;
                mind.Note(now, "the mark did not take");
                if (WindowMarksFailed <= 5)
                    _logger.LogInformation("[playerbots] {Name} mark did not take: {Trace}", ch.Name,
                        string.Join(" | ", mind.Trace().TakeLast(8).Select(t => t.Text)));
            }
        }
        if (mind.Client == null || !KnowsTravel(mind) || now < mind.NextMarkMs || mind.At == null ||
            !mind.At.Type.Equals("Bank", StringComparison.OrdinalIgnoreCase) ||
            !mind.At.City.Equals(mind.Home, StringComparison.OrdinalIgnoreCase) ||
            ch.Mana < 20 || ch.TryGetCastingSpell(out _) || ch.Backpack is not { } pack)
            return false;
        var rune = OwnRune(ch);
        if (rune != null && rune.HasRuneMark && rune.More1 > 0) return false;
        mind.NextMarkMs = now + 300_000;
        // Only a spell its book holds.
        if (!HasSpell(ch, SpellType.Mark) || !HasSpell(ch, SpellType.Recall)) return false;
        if (rune == null || rune.More1 <= 0 && rune.HasRuneMark)
        {
            // A blank rune, as one buys for a few coins.
            if (rune != null) _world.DeleteObject(rune);
            rune = _world.CreateItem();
            rune.BaseId = 0x1F14;
            rune.ItemType = ItemType.Rune;
            rune.Name = "a recall rune";
            if (!pack.TryAddItem(rune))
            {
                _world.DeleteObject(rune);
                return false;
            }
            ch.SetTag(RuneTag, rune.Uid.Value.ToString());
        }
        mind.Client.HandleCastSpell(SpellType.Mark, rune.Uid.Value);
        mind.MarkCheckMs = now + 8000;
        WindowMarks++;
        mind.Note(now, "marks its rune at the home bank");
        mind.NextActMs = now + 4000;
        return true;
    }

    /// <summary>A caster with a marked rune recalls home. False when it cannot.</summary>
    private bool TryRecallHome(BotMind mind, long now, string why)
    {
        var ch = mind.Char;
        if (mind.Client == null || !KnowsTravel(mind) || now < mind.NextRecallMs || ch.Mana < 11 ||
            ch.IsDead || ch.TryGetCastingSpell(out _))
            return false;
        var rune = OwnRune(ch);
        if (rune == null || !rune.TryGetRuneMark(out var mark) || rune.More1 <= 0 || !HasSpell(ch, SpellType.Recall)) return false;
        if (mark.Map == ch.MapIndex && mark.GetDistanceTo(ch.Position) < 64) return false; // walk it
        mind.NextRecallMs = now + 60_000;
        mind.RecallSinceMs = now;
        mind.Client.HandleCastSpell(SpellType.Recall, rune.Uid.Value);
        WindowRecalls++;
        mind.Note(now, $"recalls home ({why})");
        mind.Goal = null;
        ch.RemoveTag(GoalTag);
        mind.BeginLinger(null, now, _rng, short_: true);
        mind.NextActMs = now + 5000;
        return true;
    }

    // ------------------------------------------------------------------
    //  Company on the road
    // ------------------------------------------------------------------

    /// <summary>Setting off: now and then an idle bot standing by comes along.</summary>
    private void MaybeTakeCompany(BotMind mind, BotDestination goal, long now)
    {
        if (CompanyPercent <= 0 || _rng.Next(100) >= CompanyPercent || mind.Haunting || mind.ShoppingFor != null ||
            goal.IsPortalType || IsRogueBot(mind.Char) || Parties?.FindParty(mind.Char.Uid) != null)
            return;
        BotMind? mate = null;
        foreach (var m in FreeBotsNear(mind.Char.Position, 6))
            if (m != mind && !IsRogueBot(m.Char) && m.Client != null && m.Home.Equals(mind.Home, StringComparison.OrdinalIgnoreCase))
            {
                mate = m;
                break;
            }
        if (mate == null) return;
        mate.Goal = goal;
        mate.Char.SetTag(GoalTag, goal.Name);
        mate.Mode = BotMode.Travel;
        mate.Route = null;
        mate.Leader = mind;
        mate.NextActMs = now + _rng.Next(600, 1500);
        WindowCompanions++;
        mind.Note(now, $"takes {BaseName(mate.Char)} along to {goal.Name}");
        mate.Note(now, $"goes along with {BaseName(mind.Char)} to {goal.Name}");
        if (_rng.Next(100) < 60) Speak(mind, "travel_together", fallback: null, to: mate.Char);
    }

    /// <summary>A companion keeps pace with its leader. True when it waits this act.</summary>
    private bool PaceWithLeader(BotMind mind, Point3D target, long now)
    {
        var lead = mind.Leader;
        if (lead == null) return false;
        if (lead.Char.IsDeleted || lead.Char.IsDead || lead.Mode != BotMode.Travel || lead.Goal != mind.Goal ||
            lead.Char.MapIndex != mind.Char.MapIndex || lead.Char.Position.GetDistanceTo(mind.Char.Position) > 30)
        {
            mind.Leader = null;
            return false;
        }
        int apart = lead.Char.Position.GetDistanceTo(mind.Char.Position);
        if (apart <= 4) { mind.Running = lead.Running; return false; }
        // Ahead of the leader: wait for it. Behind: catch up.
        bool ahead = mind.Char.Position.GetDistanceTo(mind.FinalSpot) < lead.Char.Position.GetDistanceTo(lead.FinalSpot);
        if (ahead)
        {
            mind.NextActMs = now + 600;
            return true;
        }
        mind.Running = true;
        return false;
    }
}
