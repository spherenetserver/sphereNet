using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// A mage's fight. What it casts is picked by the situation, from what it can really
/// cast: the spell in its book, the magery for its circle, and the engine's own cast
/// test (mana for the real cost, reagents, the region). In order: a cure when
/// poisoned, a heal when it is time (Greater Heal for a deep wound when the mana for
/// the fight is still there, Heal for a top-up), a way out from under a melee foe
/// (Teleport, Paralyze, a Wall of Stone), wards while the foe is still at a distance
/// (Magic Reflection against a caster, Reactive Armor, Protection, Bless), something
/// summoned against prey too tough to face alone, an opener on a dangerous melee foe
/// (Paralyze, else a curse), Poison on one not poisoned, then damage by circle:
/// Flamestrike with the skill for it, Explosion followed by Energy Bolt, Lightning,
/// Fireball, Harm up close, Magic Arrow - and the cheapest that will do to finish.
///
/// Mana is not thrown away: with none left for anything useful it backs off and
/// meditates when the foe lets it, else fights on with staff or fists, else leaves.
/// It never just stands there. Casts follow each other as the engine allows (never a
/// new one while one is under way) with the short hesitation of a hand on the keys.
/// </summary>
public sealed partial class PlayerBotEngine
{
    public long WindowCasts { get; private set; }
    public long WindowWards { get; private set; }
    public long WindowSummons { get; private set; }
    public long WindowCombos { get; private set; }
    public long WindowManaRests { get; private set; }

    /// <summary>Magery (tenths) from which a circle is cast with a fair chance.</summary>
    private static readonly int[] CircleSkill = [0, 0, 0, 100, 250, 400, 550, 700, 850];

    /// <summary>The magery circle of a spell (1-8), 0 for anything else.</summary>
    internal static int SpellCircle(SpellType spell) =>
        spell is >= SpellType.Clumsy and <= SpellType.MageryQty ? ((int)spell - 1) / 8 + 1 : 0;

    /// <summary>Whether the bot can cast <paramref name="spell"/> now: in its book, the
    /// magery for its circle, and castable as the engine judges it (skill requirement,
    /// reagents, the region's magic rules, mana for the real cost). Without a spell
    /// engine: the mana of its circle.</summary>
    internal bool CanCast(BotMind mind, SpellType spell)
    {
        var ch = mind.Char;
        if (!KnowsSpell(mind, spell)) return false;
        if (_spellBlocked.Count > 0 && _spellBlocked.TryGetValue((ch.Uid.Value, spell), out long until))
        {
            if (NowMs() < until) return false;
            _spellBlocked.Remove((ch.Uid.Value, spell));
        }
        if (Spells != null) return Spells.CanCastTest(ch, spell, checkAntiMagic: true);
        return ch.Mana >= ManaFor(spell);
    }

    /// <summary>The spell is in the book and the bot has the magery for its circle.</summary>
    private static bool KnowsSpell(BotMind mind, SpellType spell)
    {
        var ch = mind.Char;
        int circle = SpellCircle(spell);
        if (circle > 0 && ch.GetSkill(SkillType.Magery) < CircleSkill[circle]) return false;
        return HasSpell(ch, spell);
    }

    // Spells a cast start refused (a region rule the test does not see, a target the
    // spell does not take): left alone for a while, per bot.
    private readonly Dictionary<(uint Bot, SpellType Spell), long> _spellBlocked = [];

    /// <summary>Cast on a character through the client. False when the engine did not
    /// start the cast; that spell is then left alone for a few seconds.</summary>
    private bool CastOn(BotMind mind, SpellType spell, Character target, long now)
    {
        var ch = mind.Char;
        var client = mind.Client;
        // Never on top of a cast under way: that would abort it.
        if (client == null || ch.IsCasting) return false;
        client.HandleCastSpell(spell, target.Uid.Value);
        return CastStarted(mind, spell, now);
    }

    /// <summary>Cast at a spot: the cast, then its target cursor answered with the tile.
    /// A spell that asks for no spot is cast where the bot stands; one that asks once
    /// its words are said (a precast) is answered when the cursor comes.</summary>
    private bool CastAt(BotMind mind, SpellType spell, Point3D at, long now)
    {
        var ch = mind.Char;
        var client = mind.Client;
        var tac = mind.Tac;
        if (client == null || ch.IsCasting) return false;
        client.HandleCastSpell(spell, 0);
        if (client.HasPendingTarget)
            client.HandleTargetResponse(1, client.ActiveTargetCursorId, 0, at.X, at.Y, at.Z, 0);
        else if (ch.IsCasting)
        {
            tac.GroundSpell = spell;
            tac.GroundAt = at;
            tac.GroundUntilMs = now + 8000;
        }
        return CastStarted(mind, spell, now);
    }

    private bool CastStarted(BotMind mind, SpellType spell, long now)
    {
        var ch = mind.Char;
        var tac = mind.Tac;
        tac.LastSpell = spell;
        if (!ch.IsCasting && tac.GroundSpell == SpellType.None)
        {
            // Refused at the start (out of sight, a rule of the place): not again at once.
            _spellBlocked[(ch.Uid.Value, spell)] = now + 6000;
            if (_spellBlocked.Count > 512)
                foreach (var old in _spellBlocked.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
                    _spellBlocked.Remove(old);
            tac.NextSpellMs = now + _rng.Next(300, 700);
            return false;
        }
        tac.WasCasting = true;
        WindowCasts++;
        return true;
    }

    /// <summary>The cursor of a spot spell that came after its words: answered now.</summary>
    private void AnswerGroundCursor(BotMind mind, long now)
    {
        var tac = mind.Tac;
        if (tac.GroundSpell == SpellType.None) return;
        var client = mind.Client;
        if (client == null || now > tac.GroundUntilMs)
        {
            tac.GroundSpell = SpellType.None;
            return;
        }
        if (!client.HasPendingTarget)
        {
            if (!mind.Char.IsCasting) tac.GroundSpell = SpellType.None;
            return;
        }
        var at = tac.GroundAt;
        tac.GroundSpell = SpellType.None;
        client.HandleTargetResponse(1, client.ActiveTargetCursorId, 0, at.X, at.Y, at.Z, 0);
    }

    private static bool IsHeld(Character c) => c.IsStatFlag(StatFlag.Freeze | StatFlag.Stone | StatFlag.Sleeping);

    private bool HasWard(Character ch, SpellType spell) =>
        ch.GetEquippedItem(SpellLayers.ForSpell(spell, Spells?.GetSpellDef(spell))) is { IsDeleted: false };

    /// <summary>The damage spell for the moment: the cheapest that finishes a foe
    /// nearly down, else the strongest the bot can cast - Flamestrike with high magery
    /// and mana to spare, Explosion when Energy Bolt can follow it, then down the
    /// circles. <paramref name="ignoreMana"/> asks what its book and skill allow at
    /// all (to size itself up). Null when it has nothing.</summary>
    internal SpellType? BestNuke(BotMind mind, Character? foe, bool ignoreMana = false)
    {
        var ch = mind.Char;
        bool Can(SpellType s) => ignoreMana ? KnowsSpell(mind, s) : CanCast(mind, s);
        int magery = ch.GetSkill(SkillType.Magery);
        if (foe != null && !ignoreMana)
        {
            // A foe nearly down: the smallest spell that ends it, to save mana.
            foreach (var s in FinisherOrder)
                if (SpellDamage(s, magery) * 0.8 >= foe.Hits && Can(s)) return s;
        }
        int dist = foe != null ? ch.Position.GetDistanceTo(foe.Position) : 10;
        bool rich = ch.MaxMana > 0 && ch.Mana * 100 / ch.MaxMana >= 50;
        if (magery >= 900 && (ignoreMana || rich) && (foe == null || foe.Hits >= 40) && Can(SpellType.Flamestrike))
            return SpellType.Flamestrike;
        if ((foe == null || foe.Hits >= 30) && Can(SpellType.Explosion) && Can(SpellType.EnergyBolt) &&
            (ignoreMana || ch.Mana >= ManaFor(SpellType.Explosion) + ManaFor(SpellType.EnergyBolt)))
            return SpellType.Explosion;
        if (Can(SpellType.EnergyBolt)) return SpellType.EnergyBolt;
        if (Can(SpellType.Lightning)) return SpellType.Lightning;
        if (Can(SpellType.Fireball)) return SpellType.Fireball;
        if (dist <= 2 && Can(SpellType.Harm)) return SpellType.Harm;
        if (Can(SpellType.MagicArrow)) return SpellType.MagicArrow;
        if (Can(SpellType.MindBlast)) return SpellType.MindBlast;
        if (Can(SpellType.Harm)) return SpellType.Harm;
        return null;
    }

    private static readonly SpellType[] FinisherOrder =
        [SpellType.MagicArrow, SpellType.Harm, SpellType.Fireball, SpellType.Lightning];

    /// <summary>The heal for the moment, or none: nothing while the foe is all but down
    /// and the bot is not; Greater Heal for a deep wound (or when it is the only one);
    /// Heal for a top-up, and when Greater Heal would leave no mana to fight on with.</summary>
    internal SpellType? HealChoice(BotMind mind, int hp, Character? foe)
    {
        var ch = mind.Char;
        if (hp >= 65) return null;
        if (foe is { MaxHits: > 0 } && foe.Hits * 100 / foe.MaxHits < 15 && hp >= 30) return null;
        bool greater = CanCast(mind, SpellType.GreaterHeal);
        bool lesser = CanCast(mind, SpellType.Heal);
        if (!greater && !lesser) return null;
        if (!lesser) return SpellType.GreaterHeal;
        if (!greater) return SpellType.Heal;
        if (hp < 40) return SpellType.GreaterHeal;
        // A lighter wound: the big heal only if a spell to fight with is still paid for after it.
        int after = ch.Mana - ManaFor(SpellType.GreaterHeal);
        int nuke = BestNuke(mind, null, ignoreMana: true) is { } n ? ManaFor(n) : 0;
        return after >= nuke && hp < 50 ? SpellType.GreaterHeal : SpellType.Heal;
    }

    /// <summary>A foe that would bring the bot down within twenty seconds if nothing
    /// were done about it (its hit points as they are, no healing counted).</summary>
    private bool IsDangerous(BotMind mind, Character foe, long now)
    {
        var it = ThreatNow(mind, foe, now);
        return Math.Max(1, (int)mind.Char.Hits) / Math.Max(0.1, it.Dps) < 20;
    }

    /// <summary>One act of a mage's fight. True when it did something (a cast, a
    /// rest); false leaves the moving - keeping its distance - to the fight.</summary>
    private bool ActMage(BotMind mind, Character foe, long now, int dist, bool sight, int hp, bool guarded)
    {
        var ch = mind.Char;
        var tac = mind.Tac;
        AnswerGroundCursor(mind, now);
        if (ch.IsCasting)
        {
            // The words are being said: it stands and lets them finish.
            tac.WasCasting = true;
            mind.NextActMs = now + 200;
            return true;
        }
        if (tac.WasCasting)
        {
            // A cast just ended: a moment before the next, as a hand finds the key.
            tac.WasCasting = false;
            tac.NextSpellMs = now + _rng.Next(150, 650);
        }
        if (tac.SummonOrderMs != 0 && now >= tac.SummonOrderMs) OrderSummons(mind, foe, now);
        if (now < tac.NextSpellMs) return false;

        var threat = ThreatNow(mind, foe, now);
        bool onMe = foe.FightTarget == ch.Uid;
        bool melee = !threat.Ranged && !threat.Casts;
        bool held = IsHeld(foe);

        // Poison undoes every heal: cured first.
        if (ch.IsPoisoned && now >= mind.NextCureMs && CanCast(mind, SpellType.Cure))
        {
            mind.NextCureMs = now + 4000;
            if (Cast(mind, SpellType.Cure, ch, now)) return true;
        }
        if (now >= mind.NextHealMs && HealChoice(mind, hp, foe) is { } heal)
        {
            mind.NextHealMs = now + 1500;
            if (Cast(mind, heal, ch, now)) return true;
        }

        // A blade at its throat: out from under it.
        if (onMe && melee && !held && dist <= 1 && hp < 75 && now >= tac.NextBreakMs && BreakAway(mind, foe, now))
            return true;
        // Coming at it and it is already hurt: a wall across the way.
        if (onMe && melee && !held && dist is >= 3 and <= 5 && hp < 60 && now >= tac.NextBreakMs && sight)
        {
            tac.NextBreakMs = now + 8000;
            if (WallOff(mind, foe, now)) return true;
        }

        // Wards, while the foe is still on its way.
        if (!tac.WardsDone)
        {
            if (dist >= 4 && tac.Wards < 2 && now - mind.FightStartedMs < 12_000 && Ward(mind, threat, now) is { } ward)
            {
                tac.Wards++;
                WindowWards++;
                if (Cast(mind, ward, ch, now)) return true;
            }
            else
                tac.WardsDone = true;
        }

        if (!sight || dist > 10) return false;

        // Too tough to face alone - more than its mana brings down, or deadly - and
        // room to cast: something to stand in front.
        if (!guarded && tac.SummonedFor != foe.Uid.Value && (dist >= 3 || held) &&
            (OutlastsItsMana(mind, foe, threat, now) || IsDangerous(mind, foe, now)) && SummonFor(mind) is { } summon)
        {
            tac.SummonedFor = foe.Uid.Value;
            if (CastAt(mind, summon, StandBy(foe.Position, ch.Position), now))
            {
                tac.SummonOrderMs = now + 400;
                WindowSummons++;
                mind.Note(now, $"summons ({summon}) against {foe.GetName()}");
                mind.NextActMs = now + 200;
                return true;
            }
        }

        // An opener on a dangerous melee foe: held fast while it is still coming, else weakened.
        if (tac.OpenedOn != foe.Uid.Value && melee && onMe && IsDangerous(mind, foe, now))
        {
            tac.OpenedOn = foe.Uid.Value;
            SpellType? opener = dist >= 3 && !held && CanCast(mind, SpellType.Paralyze) ? SpellType.Paralyze
                : CanCast(mind, SpellType.Curse) ? SpellType.Curse
                : CanCast(mind, SpellType.Weaken) ? SpellType.Weaken
                : CanCast(mind, SpellType.Clumsy) ? SpellType.Clumsy : null;
            if (opener is { } o && Cast(mind, o, foe, now)) return true;
        }

        // Poison on one not poisoned: it works while the bot does something else.
        if (!foe.IsPoisoned && !held && foe.MaxHits > 0 && foe.Hits * 100 / foe.MaxHits > 35 && now >= tac.NextPoisonMs &&
            (tac.PoisonedFoe != foe.Uid.Value || tac.PoisonTries < 2) && CanCast(mind, SpellType.Poison))
        {
            if (tac.PoisonedFoe != foe.Uid.Value) { tac.PoisonedFoe = foe.Uid.Value; tac.PoisonTries = 0; }
            tac.PoisonTries++;
            tac.NextPoisonMs = now + 8000;
            if (Cast(mind, SpellType.Poison, foe, now)) return true;
        }

        // Damage: the second half of a combination, else the best for the moment.
        if (tac.Combo != SpellType.None)
        {
            var follow = tac.Combo;
            tac.Combo = SpellType.None;
            if (CanCast(mind, follow) && Cast(mind, follow, foe, now))
            {
                WindowCombos++;
                return true;
            }
        }
        if (BestNuke(mind, foe) is { } nuke)
        {
            tac.RestUntilMs = 0;
            tac.Brawling = false;
            if (Cast(mind, nuke, foe, now))
            {
                if (nuke == SpellType.Explosion) tac.Combo = SpellType.EnergyBolt;
                return true;
            }
            return false;
        }

        // Nothing it can cast. A spell the engine refused a moment ago (the foe slipped
        // out of sight, a rule of the place) is no empty mana pool: it moves and tries again.
        var known = CheapestNuke(mind);
        if (known is { } cheap && ch.Mana >= ManaFor(cheap) && IsBlocked(ch, cheap, now)) return false;
        bool dry = known is not { } any || ch.Mana < ManaFor(any);
        return OutOfMana(mind, foe, now, dist, onMe, held, hp, dry);
    }

    /// <summary>The foe takes more bringing down than the bot's mana pays for.</summary>
    private bool OutlastsItsMana(BotMind mind, Character foe, BotThreat threat, long now)
    {
        var me = PowerOf(mind, now);
        double left = foe.MaxHits > 0 ? Math.Clamp(foe.Hits / (double)foe.MaxHits, 0.05, 1.0) : 1.0;
        return me.ManaDamage > 0 && threat.Toughness * left > me.ManaDamage * 0.8;
    }

    /// <summary>The cheapest damage spell its book and skill allow, whatever its mana.</summary>
    private SpellType? CheapestNuke(BotMind mind)
    {
        SpellType? best = null;
        int bestCost = int.MaxValue;
        foreach (var s in Nukes)
        {
            if (!KnowsSpell(mind, s)) continue;
            int cost = ManaFor(s);
            if (cost < bestCost) { best = s; bestCost = cost; }
        }
        return best;
    }

    private static readonly SpellType[] Nukes =
    [
        SpellType.MagicArrow, SpellType.Harm, SpellType.Fireball, SpellType.Lightning, SpellType.MindBlast,
        SpellType.EnergyBolt, SpellType.Explosion, SpellType.Flamestrike,
    ];

    private bool IsBlocked(Character ch, SpellType spell, long now) =>
        _spellBlocked.TryGetValue((ch.Uid.Value, spell), out long until) && now < until;

    /// <summary>A cast in the fight, with the pause to the next act.</summary>
    private bool Cast(BotMind mind, SpellType spell, Character target, long now)
    {
        if (!CastOn(mind, spell, target, now)) return false;
        mind.NextCastMs = now;
        mind.NextActMs = now + 200;
        return true;
    }

    /// <summary>The ward to put up against this foe, or none left: Magic Reflection
    /// when it casts, Reactive Armor when it strikes, Protection, then Bless.</summary>
    private SpellType? Ward(BotMind mind, BotThreat threat, long now)
    {
        var ch = mind.Char;
        if (threat.Casts && !ch.IsStatFlag(StatFlag.Reflection) && !HasWard(ch, SpellType.MagicReflect) &&
            CanCast(mind, SpellType.MagicReflect))
            return SpellType.MagicReflect;
        if (!threat.Casts && !ch.IsStatFlag(StatFlag.Reactive) && !HasWard(ch, SpellType.ReactiveArmor) &&
            CanCast(mind, SpellType.ReactiveArmor))
            return SpellType.ReactiveArmor;
        if (!HasWard(ch, SpellType.Protection) && CanCast(mind, SpellType.Protection)) return SpellType.Protection;
        if (!HasWard(ch, SpellType.Bless) && CanCast(mind, SpellType.Bless)) return SpellType.Bless;
        return null;
    }

    /// <summary>What it can summon: an Energy Vortex, Blade Spirits, else an elemental.</summary>
    private SpellType? SummonFor(BotMind mind)
    {
        foreach (var s in Summons)
            if (CanCast(mind, s)) return s;
        return null;
    }

    private static readonly SpellType[] Summons =
    [
        SpellType.EnergyVortex, SpellType.BladeSpirit, SpellType.EarthElemental, SpellType.AirElemental,
        SpellType.FireElemental, SpellType.WaterElemental,
    ];

    /// <summary>The tile beside <paramref name="foe"/> on the side of <paramref name="from"/>.</summary>
    private static Point3D StandBy(Point3D foe, Point3D from)
    {
        int dx = Math.Sign(from.X - foe.X), dy = Math.Sign(from.Y - foe.Y);
        return new Point3D((short)(foe.X + dx), (short)(foe.Y + dy), foe.Z, foe.Map);
    }

    /// <summary>What it summoned is told whom to fight, as its owner would: "all kill"
    /// and the target. A creature that obeys nobody (a vortex) needs no telling.</summary>
    private void OrderSummons(BotMind mind, Character foe, long now)
    {
        var ch = mind.Char;
        var tac = mind.Tac;
        var client = mind.Client;
        if (ch.IsCasting) { tac.SummonOrderMs = now + 300; return; }
        tac.SummonOrderMs = 0;
        if (client == null || client.HasPendingTarget) return;
        bool any = false;
        foreach (var c in _world.GetCharsInRange(ch.Position, 10))
            if (!c.IsDeleted && !c.IsDead && c.IsSummoned && c.OwnerSerial == ch.Uid) { any = true; break; }
        if (!any) return;
        client.HandleSpeech(0, ch.SpeechColor != 0 ? ch.SpeechColor : (ushort)0x03B2, 3, "all kill");
        if (client.HasPendingTarget)
            client.HandleTargetResponse(0, client.ActiveTargetCursorId, foe.Uid.Value, foe.X, foe.Y, foe.Z, 0);
    }

    /// <summary>Out from under a melee foe: Teleport to a spot it can see some tiles
    /// off, else Paralyze, else nothing (the fight then steps back on foot).</summary>
    private bool BreakAway(BotMind mind, Character foe, long now)
    {
        var ch = mind.Char;
        var tac = mind.Tac;
        tac.NextBreakMs = now + 8000;
        if (CanCast(mind, SpellType.Teleport) && TeleportSpot(ch.Position, foe.Position) is { } spot &&
            CastAt(mind, SpellType.Teleport, spot, now))
        {
            mind.Note(now, $"teleports away from {foe.GetName()}");
            mind.NextActMs = now + 200;
            return true;
        }
        if (CanCast(mind, SpellType.Paralyze) && Cast(mind, SpellType.Paralyze, foe, now)) return true;
        return false;
    }

    /// <summary>A wall across the way of a foe that is coming (three to five tiles off).</summary>
    private bool WallOff(BotMind mind, Character foe, long now)
    {
        var ch = mind.Char;
        if (!CanCast(mind, SpellType.WallOfStone)) return false;
        var mid = new Point3D((short)((ch.X + foe.X) / 2), (short)((ch.Y + foe.Y) / 2), ch.Z, ch.MapIndex);
        if (mid.X == ch.X && mid.Y == ch.Y) return false;
        if (!CastAt(mind, SpellType.WallOfStone, mid, now)) return false;
        mind.Note(now, $"walls off {foe.GetName()}");
        mind.NextActMs = now + 200;
        return true;
    }

    /// <summary>A standable tile six to nine away from the threat, in sight of the
    /// bot (a teleport needs its line of sight), or none.</summary>
    private Point3D? TeleportSpot(Point3D here, Point3D threat)
    {
        for (int far = 9; far >= 6; far -= 3)
        {
            var raw = Away(here, threat, far);
            if (!BotNavGraph.TrySeatNear(_world.MapData, raw, out var spot) || Math.Abs(spot.Z - here.Z) > 12) continue;
            if (spot.GetDistanceTo(threat) <= here.GetDistanceTo(threat) + 2) continue;
            if (FindTeleport(spot) != null || !_world.CanSeeLOS(here, spot)) continue;
            return spot;
        }
        return null;
    }

    /// <summary>Nothing useful left to cast. Out of mana (<paramref name="dry"/>) with
    /// the foe held, busy with someone else or far off, the bot keeps its distance and
    /// meditates; else - and when it is reagents it lacks, which no rest brings back - it
    /// fights on with staff or fists when it has the skill and the health; else it
    /// leaves the fight.</summary>
    private bool OutOfMana(BotMind mind, Character foe, long now, int dist, bool onMe, bool held, int hp, bool dry)
    {
        var ch = mind.Char;
        var tac = mind.Tac;
        var client = mind.Client!;
        bool lets = dist > 1 && (held || !onMe || dist >= 7);
        if (dry && lets && ch.GetSkill(SkillType.Meditation) >= 300)
        {
            tac.Brawling = false;
            if (now >= tac.RestUntilMs)
            {
                tac.RestUntilMs = now + 6000;
                mind.NextMeditateMs = now + 12_000;
                client.HandleUseSkill((int)SkillType.Meditation);
                WindowMeditations++;
                WindowManaRests++;
                mind.Note(now, "out of mana; meditates while the foe lets it");
            }
            mind.NextActMs = now + 700;
            return true;
        }
        tac.RestUntilMs = 0;
        if (CanBrawl(mind) && hp >= 50 && (foe.MaxHits <= 0 || foe.Hits * 100 / foe.MaxHits <= 60 || !IsDangerous(mind, foe, now)))
        {
            if (!tac.Brawling)
            {
                tac.Brawling = true;
                // The staff back in hand, or fists when it wrestles better than it wields.
                ArmToBrawl(mind);
                client.HandleAttack(foe.Uid.Value);
                mind.Note(now, dry ? "out of mana; fights on hand to hand" : "nothing left to cast; fights on hand to hand");
            }
            return false; // the fight closes in
        }
        tac.Brawling = false;
        StartRetreat(mind, foe, now, dry ? "out of mana" : "nothing left to cast");
        return true;
    }
}
