using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>Fighting. A bot defends itself against anything that attacks it; a
/// fighter out of a guarded town also picks fights with monsters it rates itself
/// against - ones it can see (a monster behind a fence or a wall is no prey). Every
/// blow, spell and bandage goes through the bot's client, so the combat rules a
/// connected player meets (swing timing, crime and notoriety, spell fizzles, reagents,
/// bandage delay) apply unchanged. A foe the bot cannot get at - it never stands in
/// reach of it for <see cref="FoeGiveUpMs"/> - is given up and left alone for
/// <see cref="FoeAvoidMs"/>; one that keeps striking from out of reach is run from.</summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>Time a bot keeps at a foe it cannot reach.</summary>
    public int FoeGiveUpMs { get; set; } = 20_000;
    /// <summary>How long a foe given up is left alone.</summary>
    public int FoeAvoidMs { get; set; } = 300_000;

    private bool TryStartFight(BotMind mind, long now)
    {
        if (mind.Client == null || now < mind.NextScanMs) return false;
        mind.NextScanMs = now + 1500;
        var ch = mind.Char;

        Character? threat = null, prey = null;
        int threatDist = int.MaxValue, preyDist = int.MaxValue;
        bool guarded = _world.FindRegion(ch.Position)?.IsGuarded == true;
        foreach (var c in _world.GetCharsInRange(ch.Position, 10))
        {
            if (c == ch || c.IsDeleted || c.IsDead || c.MapIndex != ch.MapIndex) continue;
            if (c.IsInvisible || c.IsStatFlag(StatFlag.Hidden)) continue;
            int d = ch.Position.GetDistanceTo(c.Position);
            // A foe found out of reach earlier is left alone, unless it is right here.
            bool avoided = mind.Avoids(c, now);
            // Anything set on this bot is fought back: a monster, a player or another
            // bot. A player is a threat once in war mode against it, as a player would
            // read the attack; striking back at an aggressor is not a crime.
            if (c.FightTarget == ch.Uid && (!c.IsPlayer || c.IsInWarMode))
            {
                if (avoided && d > 1) continue;
                if (d < threatDist) { threat = c; threatDist = d; }
                continue;
            }
            if (avoided || guarded || !mind.IsFighter || c.IsPlayer || c.OwnerSerial.IsValid) continue;
            if (c.NpcBrain is not (NpcBrainType.Monster or NpcBrainType.Berserk or NpcBrainType.Dragon)) continue;
            // A monster a player is already fighting is that player's: no kill stealing.
            if (c.FightTarget.IsValid && _world.FindChar(c.FightTarget) is { HasClient: true }) continue;
            if (d > 8 || d >= preyDist || c.MaxHits > ch.MaxHits * mind.Nerve) continue;
            // Prey is what it can see: a monster behind a fence is not.
            if (!_world.CanSeeLOS(ch.Position, c.Position)) continue;
            prey = c;
            preyDist = d;
        }
        var victim = threat == null ? PickVictim(mind, now, guarded) : null;
        var foe = threat ?? victim ?? (prey != null && _rng.Next(100) < 70 ? prey : null);
        if (foe == null) return false;
        Engage(mind, foe, now);
        if (victim != null && _rng.Next(100) < 70) Speak(mind, "pk_taunt", fallback: null);
        // Set on in a guarded town by a criminal or a murderer: call the guards, as
        // anyone would (the words reach the guard call like a player's).
        if (threat != null && threat.IsPlayer && guarded && (threat.IsFlaggedAsCriminal || threat.IsMurderer))
            Speak(mind, "guards_call", fallback: null, force: true);
        return true;
    }

    private void Engage(BotMind mind, Character foe, long now)
    {
        if (mind.Mode != BotMode.Fight) mind.ResumeMode = mind.Mode == BotMode.Flee ? BotMode.Travel : mind.Mode;
        mind.Mode = BotMode.Fight;
        mind.Foe = foe;
        mind.FightStartedMs = now;
        mind.FightInReachMs = now;
        mind.TilePath = null;
        mind.Note(now, $"fights {foe.GetName()} at {foe.Position}");
        WindowFights++;
        mind.Client!.HandleAttack(foe.Uid.Value);
        if (_rng.Next(100) < 30) Speak(mind, "combat_engage", fallback: null);
        mind.NextActMs = now + 300;
    }

    private void ActFight(BotMind mind, long now)
    {
        var ch = mind.Char;
        var foe = mind.Foe;
        var client = mind.Client;
        if (client == null || foe == null || foe.IsDeleted || foe.IsDead || foe.MapIndex != ch.MapIndex ||
            ch.Position.GetDistanceTo(foe.Position) > 18 || now - mind.FightStartedMs > 180_000)
        {
            EndFight(mind, now, won: foe != null && foe.IsDead);
            return;
        }

        int hp = ch.MaxHits > 0 ? ch.Hits * 100 / ch.MaxHits : 100;
        if (hp < mind.FleeAt)
        {
            client.HandleWarMode(false);
            mind.Mode = BotMode.Flee;
            mind.FleeFrom = foe.Position;
            mind.FleeUntilMs = now + _rng.Next(10_000, 20_000);
            mind.TilePath = null;
            if (_rng.Next(100) < 50) Speak(mind, "combat_flee", fallback: null);
            mind.NextActMs = now + 100;
            return;
        }

        if (ch.IsPoisoned && now >= mind.NextCureMs && TryCure(mind, now))
        {
            mind.NextActMs = now + 400;
            return;
        }

        if (hp < 60 && now >= mind.NextHealMs && TryHeal(mind, now, hp))
        {
            mind.NextActMs = now + 400;
            return;
        }

        int dist = ch.Position.GetDistanceTo(foe.Position);
        // Keep the range the weapon wants: blades close in, bows and casters hang back.
        int want = mind.IsArcher ? 6 : mind.IsCaster ? 5 : Math.Max(1, CombatHelper.GetWeaponRange(EquippedWeapon(ch)).Max);
        bool sight = dist <= 1 || (dist <= 12 && _world.CanSeeLOS(ch.Position, foe.Position));
        if (dist <= want && sight)
            mind.FightInReachMs = now;
        else if (now - mind.FightInReachMs > FoeGiveUpMs)
        {
            GiveUpFoe(mind, foe, now);
            return;
        }

        if (mind.IsCaster && sight && now >= mind.NextCastMs && ch.Mana >= 20 && dist <= 10 && !ch.TryGetCastingSpell(out _))
        {
            client.HandleCastSpell(PickAttackSpell(ch), foe.Uid.Value);
            mind.NextCastMs = now + _rng.Next(3000, 4500);
            mind.NextActMs = now + 500;
            return;
        }

        bool stepped = false;
        bool run = dist > want + 2;
        if (dist > want || !sight)
            stepped = StepToward(mind, foe.Position, run, searchWithin: 14);
        else if ((mind.IsArcher || mind.IsCaster) && dist < 3)
        {
            run = true;
            stepped = StepToward(mind, Away(ch.Position, foe.Position, 4), run: true);
        }
        mind.NextActMs = now + (stepped ? MovementEngine.GetMoveDelay(ch.IsMounted, run) : Math.Min(StepDelay(mind, false, run, now), 350));
    }

    /// <summary>A foe the bot has not stood in reach of for a while: behind a fence,
    /// over water, up on a ledge. It is left alone for a time; if it is the one doing
    /// the attacking, the bot gets out of its way.</summary>
    private void GiveUpFoe(BotMind mind, Character foe, long now)
    {
        var ch = mind.Char;
        mind.AvoidFoes[foe.Uid.Value] = now + FoeAvoidMs;
        if (mind.AvoidFoes.Count > 32)
            foreach (uint old in mind.AvoidFoes.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
                mind.AvoidFoes.Remove(old);
        WindowUnreachableFoes++;
        mind.Note(now, $"cannot get at {foe.GetName()} at {foe.Position}; leaves it alone");
        if (foe.FightTarget == ch.Uid)
        {
            mind.Client?.HandleWarMode(false);
            mind.Mode = BotMode.Flee;
            mind.FleeFrom = foe.Position;
            mind.FleeUntilMs = now + _rng.Next(8_000, 14_000);
            mind.FleeTarget = null;
            mind.TilePath = null;
            mind.NextActMs = now + 100;
            return;
        }
        EndFight(mind, now, won: false);
    }

    private void EndFight(BotMind mind, long now, bool won)
    {
        mind.Client?.HandleWarMode(false);
        if (won && _rng.Next(100) < 40) Speak(mind, "combat_victory", fallback: null);
        var foe = mind.Foe;
        mind.EndFightState();
        mind.Mode = mind.ResumeMode;
        mind.ResetLeg(now);
        mind.NextActMs = now + _rng.Next(800, 2500);
        // The body of what it killed: supplies off a monster, anything off a victim.
        if (won && foe != null && !mind.Char.IsDead)
            StartLoot(mind, foe, now);
    }

    private void ActFlee(BotMind mind, long now)
    {
        var ch = mind.Char;
        var foe = mind.Foe;
        bool clear = foe == null || foe.IsDead || foe.IsDeleted || ch.Position.GetDistanceTo(foe.Position) >= 16;
        if (now >= mind.FleeUntilMs || clear)
        {
            int hp = ch.MaxHits > 0 ? ch.Hits * 100 / ch.MaxHits : 100;
            if (!clear && foe != null && hp >= mind.FleeAt + 15 && !mind.Avoids(foe, now))
            {
                Engage(mind, foe, now);
                return;
            }
            mind.EndFightState();
            mind.Mode = mind.ResumeMode;
            mind.ResetLeg(now);
            mind.NextActMs = now + 500;
            return;
        }
        if (now >= mind.NextHealMs && TryHeal(mind, now, ch.MaxHits > 0 ? ch.Hits * 100 / ch.MaxHits : 100))
        {
            mind.NextActMs = now + 300;
            return;
        }
        var from = foe?.Position ?? mind.FleeFrom;
        if (ch.IsPoisoned && now >= mind.NextCureMs && TryCure(mind, now))
        {
            mind.NextActMs = now + 300;
            return;
        }
        // Run for the road, away from the threat, rather than straight into a wall.
        if (mind.FleeTarget == null || now - mind.FleeTargetMs > 3000)
        {
            mind.FleeTarget = FleePoint(ch.Position, from) ?? WideFleePoint(ch.Position, from);
            mind.FleeTargetMs = now;
        }
        bool stepped = StepToward(mind, mind.FleeTarget ?? Away(ch.Position, from, 8), run: true, searchWithin: 30);
        mind.NextActMs = now + (stepped ? MovementEngine.GetMoveDelay(ch.IsMounted, true) : 300);
    }

    /// <summary>Greater Heal for a caster with the mana, else a bandage on itself,
    /// else a heal potion when it is going badly.</summary>
    private bool TryHeal(BotMind mind, long now, int hp)
    {
        var ch = mind.Char;
        var client = mind.Client!;
        if (mind.IsCaster && ch.Mana >= 11 && !ch.TryGetCastingSpell(out _))
        {
            client.HandleCastSpell(SpellType.GreaterHeal, ch.Uid.Value);
            mind.NextHealMs = now + 4000;
            return true;
        }
        var bandage = FindInPack(ch, i => i.ItemType == ItemType.Bandage);
        if (bandage != null)
        {
            client.HandleDoubleClick(bandage.Uid.Value);
            if (client.HasPendingTarget)
                client.HandleTargetResponse(0, client.ActiveTargetCursorId, ch.Uid.Value, ch.X, ch.Y, ch.Z, 0);
            mind.NextHealMs = now + 9000;
            return true;
        }
        if (hp < 40)
        {
            var potion = FindInPack(ch, i => i.ItemType == ItemType.Potion &&
                i.GetName().Contains("heal", StringComparison.OrdinalIgnoreCase));
            if (potion != null)
            {
                client.HandleDoubleClick(potion.Uid.Value);
                mind.NextHealMs = now + 10_000;
                return true;
            }
        }
        mind.NextHealMs = now + 5000;
        return false;
    }

    /// <summary>A road node within 30 tiles that is farther from the threat than the
    /// bot is: the one that gains the most distance for the least running. Null when
    /// none does (then the bot runs straight away).</summary>
    internal Point3D? FleePoint(Point3D here, Point3D threat)
    {
        int now = here.GetDistanceTo(threat);
        Point3D? best = null;
        int bestScore = int.MinValue;
        foreach (int i in Nav.NodesNear(here, 30))
        {
            var p = Nav.Waypoints[i].Pos;
            int gain = p.GetDistanceTo(threat) - now;
            if (gain <= 2) continue;
            int score = gain * 2 - here.GetDistanceTo(p);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    /// <summary>No road node near: the nearest one within 60 tiles on any nearby floor,
    /// if running there gains anything.</summary>
    private Point3D? WideFleePoint(Point3D here, Point3D threat)
    {
        int node = Nav.FindNearest(here, 60, null, zBand: 40);
        if (node < 0) return null;
        var p = Nav.Waypoints[node].Pos;
        return p.GetDistanceTo(threat) > here.GetDistanceTo(threat) ? p : null;
    }

    /// <summary>Poisoned: Cure for a caster with the mana, else a cure potion.</summary>
    private bool TryCure(BotMind mind, long now)
    {
        var ch = mind.Char;
        var client = mind.Client;
        mind.NextCureMs = now + 5000;
        if (client == null) return false;
        if (mind.IsCaster && ch.Mana >= 6 && !ch.TryGetCastingSpell(out _))
        {
            client.HandleCastSpell(SpellType.Cure, ch.Uid.Value);
            return true;
        }
        var potion = FindInPack(ch, i => i.ItemType == ItemType.Potion &&
            i.GetName().Contains("cure", StringComparison.OrdinalIgnoreCase));
        if (potion == null) return false;
        client.HandleDoubleClick(potion.Uid.Value);
        mind.NextCureMs = now + 10_000;
        return true;
    }

    /// <summary>Out of a fight with little mana: a caster stops to meditate.</summary>
    private bool TryMeditate(BotMind mind, long now)
    {
        var ch = mind.Char;
        if (!mind.IsCaster || mind.Client == null || now < mind.NextMeditateMs || ch.MaxMana <= 0 ||
            ch.Mana * 100 / ch.MaxMana >= 40 || ch.TryGetCastingSpell(out _))
            return false;
        mind.NextMeditateMs = now + 15_000;
        mind.Client.HandleUseSkill((int)SkillType.Meditation);
        mind.NextActMs = now + 8000; // still while it works
        WindowMeditations++;
        return true;
    }

    public long WindowMeditations { get; private set; }

    private SpellType PickAttackSpell(Character ch)
    {
        int magery = ch.GetSkill(SkillType.Magery);
        int roll = _rng.Next(100);
        if (magery >= 800 && roll < 45) return SpellType.EnergyBolt;
        if (magery >= 600 && roll < 75) return SpellType.Lightning;
        if (magery >= 300 && roll < 90) return SpellType.Fireball;
        return SpellType.MagicArrow;
    }

    private Item? FindInPack(Character ch, Func<Item, bool> match)
    {
        var pack = ch.Backpack;
        if (pack == null) return null;
        foreach (var item in _world.GetContainerContents(pack.Uid))
            if (!item.IsDeleted && match(item))
                return item;
        return null;
    }

    private static Item? EquippedWeapon(Character ch) =>
        ch.GetEquippedItem(Layer.OneHanded) ?? ch.GetEquippedItem(Layer.TwoHanded);

    private static Point3D Away(Point3D from, Point3D threat, int distance)
    {
        int dx = Math.Sign(from.X - threat.X), dy = Math.Sign(from.Y - threat.Y);
        if (dx == 0 && dy == 0) dx = 1;
        return new Point3D((short)(from.X + dx * distance), (short)(from.Y + dy * distance), from.Z, from.Map);
    }
}
