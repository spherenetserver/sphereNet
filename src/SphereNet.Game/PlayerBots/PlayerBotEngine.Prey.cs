using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Sizing up a fight. A bot judges a creature by what a player would see of it - the
/// damage it deals, its hit points and armour, whether it casts or poisons - against
/// what it can bring itself: the damage of the weapon it holds at its skill (or of the
/// spells its mana pays for) and how much of a beating it takes with what it carries
/// to heal. The time it needs to bring the creature down is set against the time the
/// creature needs for it; how even a fight a bot takes is its nerve.
///
/// It remembers. The kind of creature that killed it is kept (PLAYERBOT.KILLERS:
/// defname, time, its own strength then - the last <see cref="KillerMemory"/>) and left
/// alone for <see cref="KillerGrudgeMinutes"/> unless it has grown clearly stronger;
/// the kinds it has beaten (PLAYERBOT.BEATEN) are what it goes for first.
///
/// A bold bot does not walk past a fight: it joins a bot or a player hard pressed by a
/// monster, and stands up for an innocent a monster has set on. A timid one keeps away.
/// </summary>
public sealed partial class PlayerBotEngine
{
    public const string KillersTag = "PLAYERBOT.KILLERS";
    public const string BeatenTag = "PLAYERBOT.BEATEN";

    /// <summary>Kinds of killer a bot remembers.</summary>
    private const int KillerMemory = 6;
    /// <summary>Kinds of beaten prey a bot remembers.</summary>
    private const int BeatenMemory = 8;
    /// <summary>How long the kind that killed it is left alone.</summary>
    public int KillerGrudgeMinutes { get; set; } = 7 * 24 * 60;
    /// <summary>How much stronger than when it died a bot must be to try that kind again.</summary>
    private const double StrongerBy = 1.25;
    /// <summary>Nerve from which a bot joins a fight that is not its own, and under
    /// which it keeps clear of one.</summary>
    internal const double BoldNerve = 1.45;
    internal const double TimidNerve = 1.05;

    public long WindowAssists { get; private set; }
    public long WindowPreyRefused { get; private set; }

    // ------------------------------------------------------------------
    //  What each side brings
    // ------------------------------------------------------------------

    /// <summary>The chance a blow lands, from the skill behind it (tenths), as someone
    /// who has watched fights would guess it.</summary>
    private static double HitGuess(int skill) => Math.Clamp(0.35 + skill / 2000.0, 0.35, 0.85);

    /// <summary>Damage a spell does at a skill: the definition's EFFECT curve when the
    /// spell engine is there, else the classic figure for its circle.</summary>
    private double SpellDamage(SpellType spell, int skill)
    {
        if (Spells?.GetSpellDef(spell) is { } def && def.GetEffect(skill) > 0) return def.GetEffect(skill);
        return spell switch
        {
            SpellType.MagicArrow => 5,
            SpellType.Harm => 8,
            SpellType.Fireball => 11,
            SpellType.Lightning => 15,
            SpellType.MindBlast => 16,
            SpellType.EnergyBolt => 24,
            SpellType.Explosion => 24,
            SpellType.Flamestrike => 34,
            _ => 6,
        };
    }

    /// <summary>Seconds a cast takes at a skill, with the breath between two casts.</summary>
    private double SpellSeconds(SpellType spell, int skill)
    {
        double cast = Spells?.GetSpellDef(spell) is { } def
            ? def.GetCastTime(skill) / 10.0
            : 0.5 + SpellCircle(spell) * 0.25;
        return cast + 0.6;
    }

    /// <summary>What the bot brings, worked out again every ten seconds (its weapon,
    /// mana and supplies change slowly).</summary>
    internal BotPower PowerOf(BotMind mind, long now)
    {
        var tac = mind.Tac;
        if (tac.Power != null && now - tac.PowerMs < 10_000) return tac.Power;
        var ch = mind.Char;
        var weapon = ch.FightWeapon();
        var (min, max) = CombatEngine.CalcWeaponDamage(ch, weapon);
        int delay = Math.Max(250, CombatEngine.GetSwingDelayMs(ch, weapon));
        int skill = ch.GetSkill(CombatEngine.GetWeaponSkill(ch, weapon));
        double dps = (min + max) / 2.0 * HitGuess(skill) * 1000.0 / delay;
        // A bow with nothing to shoot is a stick.
        if (CombatHelper.IsRangedWeapon(weapon) && !HasAmmoFor(ch, weapon)) dps = 0.5;

        double manaDamage = 0;
        if (mind.IsCaster && BestNuke(mind, null, ignoreMana: true) is { } nuke)
        {
            int magery = ch.GetSkill(SkillType.Magery);
            double dmg = SpellDamage(nuke, magery);
            double spellDps = dmg * 0.85 / SpellSeconds(nuke, magery);
            manaDamage = Math.Max(0, (int)ch.Mana) / (double)Math.Max(1, ManaFor(nuke)) * dmg * 0.85;
            dps = Math.Max(dps, spellDps);
        }

        int armor = Math.Max(0, CombatEngine.CalcArmorDefense(ch));
        double heal = 0;
        if (mind.IsCaster && (HasSpell(ch, SpellType.GreaterHeal) || HasSpell(ch, SpellType.Heal))) heal += 0.45;
        if (CanBandageSelf(ch)) heal += 0.45;
        if (FindPotion(ch, SpellType.GreaterHeal, SpellType.Heal) != null) heal += 0.15;
        double staying = Math.Max(1, (int)ch.MaxHits) * (1 + armor / 120.0) * (1 + Math.Min(0.8, heal));
        tac.PowerMs = now;
        return tac.Power = new BotPower(dps, staying, manaDamage, armor);
    }

    /// <summary>What <paramref name="c"/> looks like to someone sizing it up. Read from
    /// the live character: a creature's own damage rating (or the weapon it holds), its
    /// hit points and armour, the spells it casts, whether it poisons.</summary>
    internal BotThreat ThreatOf(Character c)
    {
        var weapon = c.FightWeapon();
        var (min, max) = CombatEngine.CalcWeaponDamage(c, weapon);
        int delay = Math.Max(250, CombatEngine.GetSwingDelayMs(c, weapon));
        int skill = c.GetSkill(CombatEngine.GetWeaponSkill(c, weapon));
        // A creature with no skill written on it is taken for an average hand.
        double dps = Math.Max(1, (min + max) / 2.0) * (skill > 0 ? HitGuess(skill) : 0.55) * 1000.0 / delay;
        int magery = c.GetSkill(SkillType.Magery);
        bool casts = c.NpcSpells.Count > 0 || (magery >= 300 && c.MaxMana >= 20);
        if (casts) dps += (10 + magery / 50.0) / 3.0;
        bool poisons = c.GetSkill(SkillType.Poisoning) > 0 || c.NpcSpells.Contains(SpellType.Poison);
        if (poisons) dps *= 1.25;
        int armor = Math.Max(0, CombatEngine.CalcArmorDefense(c));
        double toughness = Math.Max(1, (int)c.MaxHits) * (1 + armor / 120.0);
        return new BotThreat(dps, toughness, casts, poisons, CombatHelper.IsRangedWeapon(weapon));
    }

    /// <summary>The foe of the fight sized up, kept for a second and a half: what it
    /// holds and wears does not change from one act to the next.</summary>
    private BotThreat ThreatNow(BotMind mind, Character foe, long now)
    {
        var tac = mind.Tac;
        if (tac.FoeThreatUid == foe.Uid.Value && now - tac.FoeThreatMs < 1500) return tac.FoeThreat;
        tac.FoeThreatUid = foe.Uid.Value;
        tac.FoeThreatMs = now;
        return tac.FoeThreat = ThreatOf(foe);
    }

    /// <summary>Whether the bot takes <paramref name="c"/> on: it brings the creature
    /// down sooner than the creature brings it down, by a margin its nerve sets (a
    /// careful bot wants twice the time, a bold one takes an even fight), and a caster's
    /// mana pays for most of it. <paramref name="odds"/> stretches the margin: a second
    /// fighter at it, a kind beaten before.</summary>
    internal bool TakesOn(BotMind mind, Character c, long now, double odds = 1.0)
    {
        var ch = mind.Char;
        var me = PowerOf(mind, now);
        var it = ThreatOf(c);
        double foeLeft = c.MaxHits > 0 ? Math.Clamp(c.Hits / (double)c.MaxHits, 0.05, 1.0) : 1.0;
        double ownLeft = ch.MaxHits > 0 ? Math.Clamp(ch.Hits / (double)ch.MaxHits, 0.05, 1.0) : 1.0;
        double kill = it.Toughness * foeLeft / Math.Max(0.1, me.Dps);
        double die = me.Staying * ownLeft / Math.Max(0.1, it.Dps);
        if (me.ManaDamage > 0 && me.ManaDamage < it.Toughness * foeLeft * 0.7 && !CanBrawl(mind))
            return false;
        return kill <= die * mind.Nerve * 0.6 * odds;
    }

    // ------------------------------------------------------------------
    //  Memory
    // ------------------------------------------------------------------

    /// <summary>The kind of creature <paramref name="c"/> is: its CHARDEF defname, else
    /// its name. Empty for a player.</summary>
    internal static string KindOf(Character c)
    {
        if (c.IsPlayer) return "";
        string? def = DefinitionLoader.GetCharDef(c.CharDefIndex)?.DefName;
        // As the definition writes it: kinds are compared without regard to case.
        if (!string.IsNullOrWhiteSpace(def)) return def;
        string name = c.GetName().Trim().ToLowerInvariant().Replace(' ', '_');
        foreach (char bad in ":;|,")
            name = name.Replace(bad, '_');
        return name.Length > 0 ? name : $"body_{c.BodyId:x}";
    }

    private List<(string Kind, long Minute, int Strength)> Killers(BotMind mind)
    {
        var tac = mind.Tac;
        if (tac.Killers != null) return tac.Killers;
        var list = new List<(string, long, int)>();
        if (mind.Char.TryGetTag(KillersTag, out string? raw) && !string.IsNullOrEmpty(raw))
            foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split(':');
                if (f.Length == 3 && f[0].Length > 0 && long.TryParse(f[1], out long minute) &&
                    int.TryParse(f[2], out int strength))
                    list.Add((f[0], minute, strength));
            }
        return tac.Killers = list;
    }

    private Dictionary<string, int> Beaten(BotMind mind)
    {
        var tac = mind.Tac;
        if (tac.Beaten != null) return tac.Beaten;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (mind.Char.TryGetTag(BeatenTag, out string? raw) && !string.IsNullOrEmpty(raw))
            foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = part.LastIndexOf(':');
                if (colon > 0 && int.TryParse(part.AsSpan(colon + 1), out int n)) map[part[..colon]] = n;
            }
        return tac.Beaten = map;
    }

    /// <summary>A kind that killed the bot not long ago, while it is not clearly
    /// stronger than it was then.</summary>
    internal bool Dreads(BotMind mind, string kind, long now)
    {
        if (kind.Length == 0) return false;
        var killers = Killers(mind);
        if (killers.Count == 0) return false;
        long minute = UnixMinute();
        foreach (var (k, at, strength) in killers)
        {
            if (!k.Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (minute - at > KillerGrudgeMinutes) continue;
            if (PowerOf(mind, now).Strength >= strength * StrongerBy) continue;
            return true;
        }
        return false;
    }

    internal bool HasBeaten(BotMind mind, string kind) => kind.Length > 0 && Beaten(mind).ContainsKey(kind);

    /// <summary>The bot died: the kind it was fighting (or that struck the last blow)
    /// goes into its memory, the newest of <see cref="KillerMemory"/>.</summary>
    internal void RememberKiller(BotMind mind, Character? killer, long now)
    {
        var tac = mind.Tac;
        string kind = killer is { IsPlayer: false } ? KindOf(killer)
            : killer == null && tac.FoeKind != null && now - tac.FoeSeenMs < 15_000 ? tac.FoeKind : "";
        if (kind.Length == 0) return;
        var list = Killers(mind);
        list.RemoveAll(e => e.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        // Its strength as it was in the fight: the cached figure, not a dead character's.
        int strength = (tac.Power ?? PowerOf(mind, now)).Strength;
        list.Add((kind, UnixMinute(), strength));
        while (list.Count > KillerMemory) list.RemoveAt(0);
        mind.Char.SetTag(KillersTag, string.Join(';', list.Select(e => $"{e.Kind}:{e.Minute}:{e.Strength}")));
        // No longer something it has the better of.
        if (Beaten(mind).Remove(kind)) WriteBeaten(mind);
        mind.Note(now, $"killed by {kind}; will keep away from that kind");
    }

    private void RememberBeaten(BotMind mind, Character foe)
    {
        string kind = KindOf(foe);
        if (kind.Length == 0) return;
        var map = Beaten(mind);
        map[kind] = Math.Min(999, map.GetValueOrDefault(kind) + 1);
        // The least beaten kind makes room.
        while (map.Count > BeatenMemory)
            map.Remove(map.Where(kv => !kv.Key.Equals(kind, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Value).First().Key);
        WriteBeaten(mind);
    }

    private void WriteBeaten(BotMind mind)
    {
        var map = Beaten(mind);
        if (map.Count == 0) mind.Char.RemoveTag(BeatenTag);
        else mind.Char.SetTag(BeatenTag, string.Join(';', map.Select(kv => $"{kv.Key}:{kv.Value}")));
    }

    /// <summary>A bot died (the death hook): what killed it is remembered.</summary>
    private void NoteBotDeath(Character victim, Character? killer, long now)
    {
        if (!_minds.TryGetValue(victim.Uid.Value, out var mind)) return;
        // A summoned or owned creature's blow is its master's doing; the kind is the creature's.
        RememberKiller(mind, killer, now);
        // The hunting ground it died at counts against the place, and it dreads it.
        NoteHuntDeath(mind);
    }

    // ------------------------------------------------------------------
    //  Choosing prey, and other people's fights
    // ------------------------------------------------------------------

    private static bool IsMonster(Character c) =>
        !c.IsPlayer && !c.OwnerSerial.IsValid &&
        c.NpcBrain is NpcBrainType.Monster or NpcBrainType.Berserk or NpcBrainType.Dragon;

    /// <summary>The best of the monsters about that the bot would hunt: one it takes on
    /// (<see cref="TakesOn"/>), not a kind it dreads, in sight; a kind it has beaten
    /// before a stranger, the nearer first. Sight is tested last, on the few left.</summary>
    private Character? PickPrey(BotMind mind, List<Character> monsters, long now)
    {
        if (monsters.Count == 0) return null;
        var ch = mind.Char;
        // An empty bow hunts nothing (with a blade in hand for want of arrows it may).
        var held = ch.FightWeapon();
        if (held != null && CombatHelper.IsRangedWeapon(held) && !HasAmmoFor(ch, held)) return null;
        // The nearest few are weighed; a crowd is not gone through one by one.
        monsters.Sort((a, b) => ch.Position.GetDistanceTo(a.Position).CompareTo(ch.Position.GetDistanceTo(b.Position)));
        Character? best = null;
        int bestRank = int.MaxValue;
        int weighed = 0;
        foreach (var c in monsters)
        {
            if (weighed >= 4) break;
            int d = ch.Position.GetDistanceTo(c.Position);
            string kind = KindOf(c);
            bool beaten = HasBeaten(mind, kind);
            int rank = d - (beaten ? 4 : 0);
            if (rank >= bestRank) continue;
            weighed++;
            if (Dreads(mind, kind, now) || !TakesOn(mind, c, now, beaten ? 1.25 : 1.0))
            {
                WindowPreyRefused++;
                continue;
            }
            // Prey is what it can see: a monster behind a fence is not.
            if (!_world.CanSeeLOS(ch.Position, c.Position)) continue;
            best = c;
            bestRank = rank;
        }
        return best;
    }

    /// <summary>Somebody else's fight with a monster near the bot. A bold bot joins it:
    /// another bot's always, a player's when the player is hard pressed (under 70% hit
    /// points) or is an innocent who did not pick the fight - not to take a kill from
    /// someone who has it in hand. Never for a criminal or a murderer, never a kind it
    /// dreads, and only a fight two can win.</summary>
    private Character? PickAssist(BotMind mind, List<(Character Monster, Character Victim)> fights, long now)
    {
        if (fights.Count == 0 || mind.Nerve < BoldNerve || IsPk(mind)) return null;
        var ch = mind.Char;
        Character? best = null;
        int bestDist = int.MaxValue;
        foreach (var (monster, victim) in fights)
        {
            int d = ch.Position.GetDistanceTo(monster.Position);
            if (d >= bestDist) continue;
            if (victim.IsFlaggedAsCriminal || victim.IsMurderer) continue;
            if (victim.HasClient)
            {
                int pct = victim.MaxHits > 0 ? victim.Hits * 100 / victim.MaxHits : 100;
                bool picked = victim.FightTarget == monster.Uid && victim.IsInWarMode;
                if (pct >= 70 && picked) continue;
            }
            if (Dreads(mind, KindOf(monster), now) || !TakesOn(mind, monster, now, odds: 2.0)) continue;
            if (!_world.CanSeeLOS(ch.Position, monster.Position)) continue;
            best = monster;
            bestDist = d;
        }
        return best;
    }

    /// <summary>A timid bot with a fight near it that it would not take on itself
    /// steps away from it for a few seconds rather than stand and watch.</summary>
    private bool ShyAway(BotMind mind, List<(Character Monster, Character Victim)> fights, long now)
    {
        var tac = mind.Tac;
        if (fights.Count == 0 || mind.Nerve >= TimidNerve || now < tac.NextShyMs || IsPk(mind)) return false;
        if (mind.Mode is not (BotMode.Linger or BotMode.Travel) || mind.Favor != null || mind.Companion != null ||
            mind.Scene != null || mind.Trade != null)
            return false;
        var ch = mind.Char;
        // In a party it stands by its party; that fight is the party code's business.
        if (Parties?.FindParty(ch.Uid) != null) return false;
        foreach (var (monster, _) in fights)
        {
            if (ch.Position.GetDistanceTo(monster.Position) > 6 || TakesOn(mind, monster, now)) continue;
            tac.NextShyMs = now + 30_000;
            tac.ShyUntilMs = now + _rng.Next(4000, 7000);
            mind.ResumeMode = mind.Mode;
            mind.Mode = BotMode.Flee;
            mind.Foe = null;
            mind.FleeFrom = monster.Position;
            mind.FleeUntilMs = tac.ShyUntilMs;
            mind.FleeTarget = null;
            tac.Escape = BotEscape.Run;
            tac.EscapeSinceMs = now;
            mind.Note(now, $"keeps clear of the fight with {monster.GetName()}");
            mind.NextActMs = now + 200;
            return true;
        }
        return false;
    }
}
