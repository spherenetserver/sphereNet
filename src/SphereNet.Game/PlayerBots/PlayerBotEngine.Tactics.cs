using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Reading a fight. Every <see cref="LookEveryMs"/> a fighting bot looks about it
/// (ten tiles, one sight test on the one candidate it would turn to): who else has
/// set on it, how hard each of them is hitting it, how fast it and its foe are losing
/// hit points. From that it changes its foe - to finish one nearly down, to turn on a
/// new attacker that hurts more, to leave one it cannot get at for one it can - and
/// decides when to break off: not at a fixed hit-point mark alone, but on the trend
/// (which side goes down first at the present rate), on how many are on it and on
/// whether it has anything left to heal with, each weighed by the bot's nerve.
///
/// Breaking off is more than running for the road: toward the guards when a guarded
/// region is near, out by Recall (the spell, or a scroll) when it has a rune and the
/// room to cast, unseen by Invisibility, or into hiding once the pursuer has lost
/// sight of it.
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>How often a fighting bot looks over the fight.</summary>
    public int LookEveryMs { get; set; } = 1500;
    /// <summary>A foe just turned to is kept this long before another change.</summary>
    private const int SwitchHoldMs = 3000;
    /// <summary>Hit-point share under which a foe is "nearly down".</summary>
    private const int NearlyDownPct = 25;
    /// <summary>How far a guarded region may be for a bot to run to it.</summary>
    private const int GuardRunRange = 40;

    public long WindowTargetSwitches { get; private set; }
    public long WindowRetreats { get; private set; }
    public long WindowTrendRetreats { get; private set; }
    public long WindowGuardRuns { get; private set; }
    public long WindowEscapeRecalls { get; private set; }
    public long WindowVanishes { get; private set; }
    public long WindowHides { get; private set; }

    private void ResetCombatWindow()
    {
        WindowTargetSwitches = WindowRetreats = WindowTrendRetreats = WindowGuardRuns = WindowEscapeRecalls = 0;
        WindowVanishes = WindowHides = WindowAssists = WindowPreyRefused = 0;
        WindowCasts = WindowWards = WindowSummons = WindowCombos = WindowManaRests = 0;
        WindowWeaponSwaps = WindowShieldsUp = WindowPotions = WindowBandages = 0;
    }

    /// <summary>The fighting of the telemetry window, for the host's periodic line.</summary>
    public string CombatReport() =>
        $"fights={WindowFights} switches={WindowTargetSwitches} retreats={WindowRetreats} (trend {WindowTrendRetreats}) " +
        $"guardruns={WindowGuardRuns} escaperecalls={WindowEscapeRecalls} vanishes={WindowVanishes} hides={WindowHides} " +
        $"assists={WindowAssists} preyrefused={WindowPreyRefused} casts={WindowCasts} wards={WindowWards} " +
        $"summons={WindowSummons} combos={WindowCombos} manarests={WindowManaRests} potions={WindowPotions} " +
        $"bandages={WindowBandages} weaponswaps={WindowWeaponSwaps} shields={WindowShieldsUp}";

    private static int Pct(Character c) => c.MaxHits > 0 ? c.Hits * 100 / c.MaxHits : 100;

    /// <summary>Whether <paramref name="c"/> has set on the bot: a creature that targets
    /// it, a player who does so in war mode.</summary>
    private static bool IsOn(Character c, Character bot) =>
        c.FightTarget == bot.Uid && (!c.IsPlayer || c.IsInWarMode);

    // ------------------------------------------------------------------
    //  The look about
    // ------------------------------------------------------------------

    /// <summary>The periodic look over the fight: the trend, who is on the bot, and a
    /// better foe when there is one. Returns the foe to fight from here on.</summary>
    private Character LookAbout(BotMind mind, Character foe, long now)
    {
        var tac = mind.Tac;
        if (now < tac.NextLookMs) return foe;
        tac.NextLookMs = now + LookEveryMs;
        var ch = mind.Char;
        Trend(mind, foe, now);

        // What each attacker has done lately (the attacker log's totals, as differences).
        foreach (var rec in ch.Attackers)
        {
            uint uid = rec.Uid.Value;
            var seen = tac.Hurt.GetValueOrDefault(uid);
            int fresh = Math.Max(0, rec.TotalDamage - seen.Total);
            tac.Hurt[uid] = (rec.TotalDamage, seen.Recent / 2 + fresh);
        }

        int reach = Math.Max(1, CombatHelper.GetWeaponRange(ch.FightWeapon()).Max);
        bool distant = mind.IsCaster && !tac.Brawling || ShootsNow(mind);
        if (distant) reach = 10;
        int foePct = Pct(foe);
        bool foeOn = IsOn(foe, ch);
        bool foeFar = now - mind.FightInReachMs > 6000;
        int foeRecent = tac.Hurt.GetValueOrDefault(foe.Uid.Value).Recent;
        var party = Parties?.FindParty(ch.Uid);

        int count = 0;
        Character? pick = null;
        int pickRank = 0, pickWeight = 0;
        string pickWhy = "";
        foreach (var c in _world.GetCharsInRange(ch.Position, 10))
        {
            if (c == ch || c.IsDeleted || c.IsDead || c.MapIndex != ch.MapIndex || !IsOn(c, ch)) continue;
            if (c.IsInvisible || c.IsStatFlag(StatFlag.Hidden)) continue;
            if (party != null && party.IsMember(c.Uid)) continue;
            count++;
            if (c == foe || mind.Avoids(c, now)) continue;
            int d = ch.Position.GetDistanceTo(c.Position);
            int recent = tac.Hurt.GetValueOrDefault(c.Uid.Value).Recent;
            int rank;
            string why;
            if (Pct(c) <= NearlyDownPct && d <= reach + 2) { rank = 3; why = "nearly down"; }
            else if (recent > Math.Max(foeRecent * 3 / 2, foeRecent + 4)) { rank = 2; why = "hurts more"; }
            else if (!foeOn || foeFar) { rank = 1; why = foeFar ? "the other is out of reach" : "it is the one attacking"; }
            else continue;
            int weight = rank == 2 ? recent : -d;
            if (rank < pickRank || (rank == pickRank && weight <= pickWeight)) continue;
            pick = c;
            pickRank = rank;
            pickWeight = weight;
            pickWhy = why;
        }
        tac.Attackers = count;

        if (pick == null || now - tac.SwitchedMs < SwitchHoldMs) return foe;
        // The one it has is nearly down and in reach: finished first, unless the
        // other is nearly down too.
        if (foePct <= NearlyDownPct && !foeFar && pickRank < 3) return foe;
        // One sight test, on the one it would turn to.
        if (Math.Abs(pick.Z - ch.Z) > ReachZ && !distant) return foe;
        if (!_world.CanSeeLOS(ch.Position, pick.Position)) return foe;
        // A foe out of reach that is not even the one attacking is given up for good.
        if (foeFar && !foeOn) mind.AvoidFoes[foe.Uid.Value] = now + FoeAvoidMs;
        SwitchFoe(mind, pick, now, pickWhy);
        return pick;
    }

    private void SwitchFoe(BotMind mind, Character to, long now, string why)
    {
        var tac = mind.Tac;
        mind.Note(now, $"turns on {to.GetName()} ({why}) from {mind.Foe?.GetName() ?? "nobody"}");
        mind.Foe = to;
        mind.FightStartedMs = now;
        mind.FightInReachMs = now;
        mind.TilePath = null;
        tac.SwitchedMs = now;
        tac.Combo = SpellType.None;
        tac.SampleFoe = 0;
        tac.FoeLossRate = 0;
        NoteFoe(mind, to, now);
        WindowTargetSwitches++;
        mind.Client?.HandleAttack(to.Uid.Value);
    }

    /// <summary>The kind it fights, kept for its memory should it die of it.</summary>
    private static void NoteFoe(BotMind mind, Character foe, long now)
    {
        var tac = mind.Tac;
        if (tac.FoeUid != foe.Uid.Value)
        {
            tac.FoeUid = foe.Uid.Value;
            tac.FoeKind = KindOf(foe);
        }
        tac.FoeSeenMs = now;
    }

    /// <summary>How fast each side loses hit points: a running mean over the looks,
    /// healing counted against the loss.</summary>
    private static void Trend(BotMind mind, Character foe, long now)
    {
        var tac = mind.Tac;
        var ch = mind.Char;
        if (tac.SampleHits >= 0 && now > tac.SampleMs)
        {
            double secs = (now - tac.SampleMs) / 1000.0;
            double loss = (tac.SampleHits - ch.Hits) / secs;
            tac.LossRate = Math.Max(0, tac.LossRate * 0.5 + loss * 0.5);
            if (tac.SampleFoe == foe.Uid.Value && tac.SampleFoeHits >= 0)
            {
                double foeLoss = (tac.SampleFoeHits - foe.Hits) / secs;
                tac.FoeLossRate = Math.Max(0, tac.FoeLossRate * 0.5 + foeLoss * 0.5);
            }
            else
                tac.FoeLossRate = 0;
        }
        tac.SampleMs = now;
        tac.SampleHits = ch.Hits;
        tac.SampleFoe = foe.Uid.Value;
        tac.SampleFoeHits = foe.Hits;
    }

    // ------------------------------------------------------------------
    //  When to break off
    // ------------------------------------------------------------------

    /// <summary>Why the bot breaks off now, or null to fight on. The hit-point mark it
    /// runs at is its own (<see cref="BotMind.FleeAt"/>), raised by every attacker
    /// beyond the first and by having nothing to heal with, and scaled by its nerve; a
    /// bold bot under it still stays for a foe that falls within seconds. Above the
    /// mark it leaves when the trend says it goes down first by more than its nerve
    /// accepts, or when too many are on it.</summary>
    internal string? WhyRetreat(BotMind mind, Character foe, long now, int hp)
    {
        var tac = mind.Tac;
        var ch = mind.Char;
        double bold = Math.Clamp((mind.Nerve - 0.8) / 1.2, 0, 1);
        int extra = Math.Max(0, tac.Attackers - 1);
        // What it has to mend with changes slowly: looked at as often as the fight is.
        if (now - tac.HealMeansMs >= LookEveryMs || tac.HealMeansMs == 0)
        {
            tac.HealMeans = HasHealMeans(mind);
            tac.HealMeansMs = now;
        }
        bool heal = tac.HealMeans;
        double mark = mind.FleeAt + extra * 8 + (heal ? 0 : 10);
        // The kind that killed it before is given less rope.
        if (tac.FoeKind is { Length: > 0 } kind && Dreads(mind, kind, now)) mark += 15;
        mark *= 1.25 - 0.5 * bold;
        double mine = tac.LossRate > 0.05 ? ch.Hits / tac.LossRate : double.MaxValue;
        double its = tac.FoeLossRate > 0.05 ? foe.Hits / tac.FoeLossRate : double.MaxValue;
        if (hp < mark)
        {
            // One more blow ends it: a bold bot stays for that.
            if (bold >= 0.5 && extra == 0 && its < 4 && its < mine * 0.5) return null;
            return "badly hurt";
        }
        // Too many on it for its nerve.
        if (tac.Attackers >= 3 + (int)(bold * 2) && hp < 70) return "outnumbered";
        // The trend: it goes down first, and soon. A timid bot leaves unless it is
        // clearly winning; a bold one stays unless it is clearly losing.
        if (hp < 80 && mine < 15 && !(heal && hp >= 55))
        {
            double margin = 1.3 - 0.6 * bold;
            if (mine * margin < its) return "losing the fight";
        }
        return null;
    }

    /// <summary>Break off the fight: war mode down, and away - how, the flight decides.</summary>
    private void StartRetreat(BotMind mind, Character foe, long now, string why)
    {
        var tac = mind.Tac;
        mind.Client?.HandleWarMode(false);
        mind.Mode = BotMode.Flee;
        mind.FleeFrom = foe.Position;
        mind.FleeUntilMs = now + _rng.Next(10_000, 20_000);
        mind.FleeTarget = null;
        mind.TilePath = null;
        tac.Escape = BotEscape.Run;
        tac.EscapeSinceMs = now;
        tac.GuardPoint = null;
        tac.NextGuardLookMs = 0;
        tac.Combo = SpellType.None;
        tac.RestUntilMs = 0;
        tac.Brawling = false;
        WindowRetreats++;
        if (why == "losing the fight") WindowTrendRetreats++;
        mind.Note(now, $"breaks off from {foe.GetName()}: {why}");
        if (_rng.Next(100) < 50) Speak(mind, "combat_flee", fallback: null);
        mind.NextActMs = now + 100;
    }

    // ------------------------------------------------------------------
    //  Getting away
    // ------------------------------------------------------------------

    /// <summary>The nearest road node in a guarded region within
    /// <see cref="GuardRunRange"/> that does not lie past the threat.</summary>
    internal Point3D? GuardedPointNear(Point3D here, Point3D threat)
    {
        int fromThreat = here.GetDistanceTo(threat);
        Point3D? best = null;
        int bestDist = int.MaxValue;
        foreach (int i in Nav.NodesNear(here, GuardRunRange))
        {
            var p = Nav.Waypoints[i].Pos;
            int d = here.GetDistanceTo(p);
            if (d >= bestDist || p.GetDistanceTo(threat) < Math.Min(fromThreat, d) - 1) continue;
            if (!IsGuardedAt(p)) continue;
            best = p;
            bestDist = d;
        }
        return best;
    }

    /// <summary>Recall out of the fight on a rune of its own (its home town's first,
    /// else any that leads away from here): the spell with the mana for it, else a
    /// Recall scroll. Not for a player killer, whose runes lead to the guards.</summary>
    private bool EscapeByRecall(BotMind mind, Character? foe, long now)
    {
        var ch = mind.Char;
        var client = mind.Client;
        if (client == null || IsPk(mind) || now < mind.NextRecallMs || ch.IsCasting) return false;
        Item? rune = HomeRune(mind);
        if (rune == null || !LeadsAway(rune, ch))
        {
            rune = null;
            foreach (var r in OwnRunes(ch))
                if (IsUsableRune(r) && LeadsAway(r, ch)) { rune = r; break; }
        }
        if (rune == null) return false;
        string from = foe?.GetName() ?? "the fight";
        if (KnowsSpell(mind, SpellType.Recall) && CanCast(mind, SpellType.Recall))
        {
            if (!Recall(mind, rune, now, $"away from {from}")) return false;
            mind.EndFightState();
            mind.Tac.EndFight();
            WindowEscapeRecalls++;
            return true;
        }
        var scroll = FindInPack(ch, i => i.ItemType == ItemType.Scroll && HeldFor(ch, i) == 0 &&
                                         Spells?.GetScrollSpell(i)?.Id == SpellType.Recall);
        if (scroll == null) return false;
        client.HandleDoubleClick(scroll.Uid.Value);
        if (!client.HasPendingTarget) return false;
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, rune.Uid.Value, rune.X, rune.Y, rune.Z, 0);
        if (!ch.IsCasting) return false;
        mind.NextRecallMs = now + 60_000;
        mind.RecallSinceMs = now;
        WindowRecalls++;
        WindowEscapeRecalls++;
        mind.Note(now, $"recalls by scroll away from {from}");
        mind.EndFightState();
        mind.Tac.EndFight();
        mind.Goal = null;
        ch.RemoveTag(GoalTag);
        mind.BeginLinger(null, now, _rng, short_: true);
        mind.NextActMs = now + 5000;
        return true;
    }

    private static bool LeadsAway(Item rune, Character ch) =>
        rune.TryGetRuneMark(out var mark) && (mark.Map != ch.MapIndex || mark.GetDistanceTo(ch.Position) >= 64);

    /// <summary>One act of a bot getting away.</summary>
    private void ActFlee(BotMind mind, long now)
    {
        var ch = mind.Char;
        var tac = mind.Tac;
        var client = mind.Client;
        var foe = mind.Foe;
        if (foe is { IsDeleted: true }) foe = null;
        var from = foe?.Position ?? mind.FleeFrom;
        int dist = from.Map == ch.MapIndex ? ch.Position.GetDistanceTo(from) : int.MaxValue;
        bool clear = mind.Foe != null
            ? foe == null || foe.IsDead || foe.MapIndex != ch.MapIndex || dist >= 16
            : dist >= 12;
        int hp = Pct(ch);
        if (now >= mind.FleeUntilMs || clear)
        {
            EndFlight(mind, foe, clear, now, hp);
            return;
        }
        AnswerGroundCursor(mind, now);
        if (ch.IsCasting)
        {
            // A spell to get away with is being said: it stands for it.
            tac.WasCasting = true;
            mind.NextActMs = now + 200;
            return;
        }
        // In hiding: a step would give it away.
        if (ch.IsStatFlag(StatFlag.Hidden))
        {
            mind.NextActMs = now + 1000;
            return;
        }
        bool unseen = ch.IsInvisible;

        if (client != null)
        {
            if (ch.IsPoisoned && now >= mind.NextCureMs && TryCure(mind, now))
            {
                mind.NextActMs = now + 300;
                return;
            }
            // A heal potion when it is bad, a refresh when it is out of breath.
            if (TryDrink(mind, now, hp, null))
            {
                mind.NextActMs = now + 300;
                return;
            }
            // Room to mend: a spell with the foe some steps behind, a bandage on the run.
            if (hp < 70 && now >= mind.NextHealMs && (dist >= 4 || unseen) && TryHeal(mind, now, hp))
            {
                mind.NextActMs = now + 300;
                return;
            }

            if (foe != null)
            {
                // Unseen: Invisibility, once, when not too many have it in their sights.
                if (!tac.TriedVanish && !unseen && mind.IsCaster && tac.Attackers <= 2 && CanCast(mind, SpellType.Invisibility))
                {
                    tac.TriedVanish = true;
                    if (CastOn(mind, SpellType.Invisibility, ch, now))
                    {
                        tac.Escape = BotEscape.Vanish;
                        WindowVanishes++;
                        mind.Note(now, "casts Invisibility to get away");
                        mind.NextActMs = now + 200;
                        return;
                    }
                }
                // Out by rune, with the room to say the words.
                if (!tac.TriedRecall && (dist >= 5 || unseen || IsHeld(foe)))
                {
                    tac.TriedRecall = true;
                    if (EscapeByRecall(mind, foe, now))
                    {
                        tac.Escape = BotEscape.Recall;
                        return;
                    }
                }
                // The pursuer has lost sight of it: into hiding.
                if (!unseen && now >= tac.NextHideMs && dist >= 4 && ch.GetSkill(SkillType.Hiding) >= 300 &&
                    !ch.IsMounted && !_world.CanSeeLOS(foe.Position, ch.Position))
                {
                    tac.NextHideMs = now + 8000;
                    client.HandleUseSkill((int)SkillType.Hiding);
                    tac.Escape = BotEscape.Hide;
                    WindowHides++;
                    mind.Note(now, "tries to hide from its pursuer");
                    mind.NextActMs = now + 1500;
                    return;
                }
            }
        }

        // Toward the guards when there are some near; a murderer or a criminal has
        // nothing to hope for from them.
        if (foe != null && !IsPk(mind) && !ch.IsFlaggedAsCriminal)
        {
            if (IsGuardedAt(ch.Position))
            {
                // Among the guards with the thing still after it: it calls them.
                if (!tac.CalledGuards && dist <= 12 && (!foe.IsPlayer || foe.IsFlaggedAsCriminal || foe.IsMurderer))
                {
                    tac.CalledGuards = true;
                    Speak(mind, "guards_call", fallback: null, force: true);
                }
            }
            else if (tac.GuardPoint == null && tac.Escape == BotEscape.Run && now >= tac.NextGuardLookMs)
            {
                // Looked for now and then, not at every step.
                tac.NextGuardLookMs = now + 4000;
                tac.GuardPoint = GuardedPointNear(ch.Position, from);
                if (tac.GuardPoint != null)
                {
                    tac.Escape = BotEscape.Guards;
                    WindowGuardRuns++;
                    // The run there may take longer than a plain flight.
                    mind.FleeUntilMs = Math.Max(mind.FleeUntilMs, now + 15_000);
                    mind.Note(now, $"runs for the guards at {tac.GuardPoint}");
                }
            }
        }

        Point3D target;
        if (tac.Escape == BotEscape.Guards && tac.GuardPoint is { } guards && !SameTile(ch.Position, guards))
            target = guards;
        else
        {
            // Run for the road, away from the threat, rather than straight into a wall.
            if (mind.FleeTarget == null || now - mind.FleeTargetMs > 3000)
            {
                mind.FleeTarget = FleePoint(ch.Position, from) ?? WideFleePoint(ch.Position, from);
                mind.FleeTargetMs = now;
            }
            target = mind.FleeTarget ?? Away(ch.Position, from, 8);
        }
        bool stepped = StepToward(mind, target, run: true, searchWithin: 30);
        mind.NextActMs = now + (stepped ? MovementEngine.GetMoveDelay(ch.IsMounted, true) : 300);
    }

    /// <summary>The flight is over: back at the foe when the reasons for leaving are
    /// gone (mended, no longer swamped, something to heal or fight with), else on with
    /// what the bot was doing.</summary>
    private void EndFlight(BotMind mind, Character? foe, bool clear, long now, int hp)
    {
        var tac = mind.Tac;
        var ch = mind.Char;
        if (!clear && foe != null && !foe.IsDead && !mind.Avoids(foe, now) && hp >= Math.Max(mind.FleeAt + 25, 55) &&
            tac.Attackers <= 1 && !ch.IsStatFlag(StatFlag.Hidden) && !ch.IsInvisible &&
            (HasHealMeans(mind) || Pct(foe) < 30) && CanStillFight(mind))
        {
            Engage(mind, foe, now);
            return;
        }
        mind.EndFightState();
        tac.EndFight();
        mind.Mode = mind.ResumeMode is BotMode.Fight or BotMode.Flee ? BotMode.Linger : mind.ResumeMode;
        mind.ResetLeg(now);
        mind.NextActMs = now + 500;
    }

    /// <summary>Whether the bot has something to fight with: arrows for its bow or a
    /// blade, mana for a spell or the skill to brawl.</summary>
    private bool CanStillFight(BotMind mind)
    {
        var ch = mind.Char;
        if (mind.IsCaster) return BestNuke(mind, null) != null || CanBrawl(mind);
        var weapon = ch.FightWeapon();
        return weapon == null || !CombatHelper.IsRangedWeapon(weapon) || HasAmmoFor(ch, weapon) ||
               FindMeleeWeapon(ch) != null;
    }

    // ------------------------------------------------------------------
    //  Keeping the range
    // ------------------------------------------------------------------

    /// <summary>The distance the bot wants to its foe now: a blade's reach, a bow's
    /// range short of its limit, a caster's five tiles (more while it rests).</summary>
    private int WantedRange(BotMind mind, bool shoots)
    {
        var ch = mind.Char;
        var tac = mind.Tac;
        if (shoots)
            return Math.Clamp(CombatHelper.GetWeaponRange(ch.FightWeapon()).Max - 2, 3, 7);
        if (mind.IsCaster && !tac.Brawling) return tac.RestUntilMs != 0 ? 8 : 5;
        return Math.Max(1, CombatHelper.GetWeaponRange(ch.FightWeapon()).Max);
    }

    /// <summary>An archer's footwork against a foe that closes on it: back off while
    /// the bow is not ready, stand and loose when it is, never move with an arrow on
    /// the string. Returns false when the foe is not one to be kited (it shoots or
    /// casts itself, is held fast, or is after somebody else).</summary>
    private bool Kite(BotMind mind, Character foe, long now, int dist, bool sight, int want)
    {
        var ch = mind.Char;
        if (!sight || dist > want) return false;
        // The shot is on its way: a step now could spoil it.
        if (ch.HasPendingHit)
        {
            mind.NextActMs = now + 150;
            return true;
        }
        var threat = ThreatNow(mind, foe, now);
        if (!IsOn(foe, ch) || threat.Ranged || threat.Casts || IsHeld(foe)) return false;
        // The client combat timer uses TickCount64 too; the bot planning clock
        // can be simulated independently and must not be compared to this deadline.
        bool ready = Environment.TickCount64 >= ch.NextAttackTime;
        bool breath = ch.MaxStam <= 0 || ch.Stam * 100 / ch.MaxStam >= 15;
        // Nearer than the bow shoots at all, or near with the bow not ready yet: back
        // off - the shot, the step back while the next arrow is nocked, the shot.
        int tooNear = CombatHelper.GetWeaponRange(ch.FightWeapon()).Min;
        if (breath && (dist < tooNear || (dist <= 3 && !ready)))
        {
            bool stepped = StepToward(mind, Away(ch.Position, foe.Position, 5), run: true);
            mind.NextActMs = now + (stepped ? MovementEngine.GetMoveDelay(ch.IsMounted, true) : 250);
            return true;
        }
        // In range with the bow ready (or the foe not yet near): stand, face it and shoot.
        mind.Client?.FaceTarget(foe);
        mind.NextActMs = now + 200;
        return true;
    }
}
