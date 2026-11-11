using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>Fighting. A bot defends itself against anything that attacks it; a
/// fighter out of a guarded town also picks fights with monsters it sizes itself up
/// against (see the Prey part) - ones it can see (a monster behind a fence or a wall is
/// no prey) - and a bold one joins a fight a monster has forced on somebody else. Every
/// blow, spell, potion and bandage goes through the bot's client, so the combat rules a
/// connected player meets (swing timing, crime and notoriety, spell fizzles, reagents,
/// bandage and potion delay) apply unchanged.
///
/// One act of a fight: look the fight over (the Tactics part: other attackers, a better
/// foe, the trend) and break off when it is going badly; look after itself (a potion, a
/// bandage with the skill for it); then fight the way its kind does - a mage by its
/// repertoire (the Spellcraft part), an archer keeping its distance and stepping back
/// from a foe that closes while the bow is not ready, a warrior at arm's length - and an
/// archer out of arrows with the blade it carries, or not at all.
///
/// A foe the bot cannot get at - it never stands in reach of it for
/// <see cref="FoeGiveUpMs"/> - is given up and left alone for <see cref="FoeAvoidMs"/>;
/// one that keeps striking from out of reach is run from.</summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>Time a bot keeps at a foe it cannot reach.</summary>
    public int FoeGiveUpMs { get; set; } = 20_000;
    /// <summary>How long a foe given up is left alone.</summary>
    public int FoeAvoidMs { get; set; } = 300_000;
    /// <summary>Height apart beyond which a foe right beside the bot is still out of its
    /// reach: up on a ledge, down a cliff.</summary>
    private const int ReachZ = 16;

    private bool TryStartFight(BotMind mind, long now)
    {
        if (mind.Client == null || now < mind.NextScanMs) return false;
        mind.NextScanMs = now + 1500;
        var ch = mind.Char;

        Character? threat = null;
        int threatDist = int.MaxValue;
        bool guarded = _world.FindRegion(ch.Position)?.IsGuarded == true;
        // It looks for a fight of its own choosing only when it has nothing in hand:
        // not on the way back to its body, at a shop counter or over a kill.
        bool free = mind.IsFighter && mind.Mode is BotMode.Linger or BotMode.Travel;
        List<Character>? monsters = null;
        List<(Character Monster, Character Victim)>? fights = null;
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
            if (IsOn(c, ch))
            {
                if (avoided && (d > 1 || Math.Abs(c.Z - ch.Z) > ReachZ)) continue;
                if (d < threatDist) { threat = c; threatDist = d; }
                continue;
            }
            if (avoided || !free || !IsMonster(c)) continue;
            // A monster somebody else is fighting: theirs, unless they need a hand.
            if (c.FightTarget.IsValid && _world.FindChar(c.FightTarget) is { IsDead: false, IsPlayer: true } other)
            {
                (fights ??= []).Add((c, other));
                continue;
            }
            // Prey: out of the guards' reach, near, and for a blade not up on a ledge
            // it cannot climb to.
            if (guarded || d > 8) continue;
            if (!mind.IsArcher && !mind.IsCaster && Math.Abs(c.Z - ch.Z) > ReachZ) continue;
            (monsters ??= []).Add(c);
        }
        // A real player set on it: the bond with them sours.
        if (threat is { HasClient: true }) OnAttackedByPlayer(mind, threat, now);
        var victim = threat == null ? PickVictim(mind, now, guarded) : null;
        var foe = threat ?? victim;
        bool assisting = false;
        if (foe == null && fights != null && PickAssist(mind, fights, now) is { } helped)
        {
            foe = helped;
            assisting = true;
        }
        if (foe == null && monsters != null && _rng.Next(100) < 70)
            foe = PickPrey(mind, monsters, now);
        if (foe == null)
            return fights != null && !guarded && ShyAway(mind, fights, now);
        Engage(mind, foe, now);
        if (assisting)
        {
            mind.Tac.Assisting = true;
            WindowAssists++;
            mind.Note(now, $"joins the fight against {foe.GetName()}");
            if (_rng.Next(100) < 60) Speak(mind, "combat_assist", fallback: null);
        }
        if (victim != null && _rng.Next(100) < 70) Speak(mind, "pk_taunt", fallback: null);
        // Set on in a guarded town by a criminal or a murderer: call the guards, as
        // anyone would (the words reach the guard call like a player's).
        if (threat != null && threat.IsPlayer && guarded && (threat.IsFlaggedAsCriminal || threat.IsMurderer))
            Speak(mind, "guards_call", fallback: null, force: true);
        return true;
    }

    private void Engage(BotMind mind, Character foe, long now)
    {
        var tac = mind.Tac;
        if (mind.Mode != BotMode.Fight)
        {
            mind.ResumeMode = mind.Mode == BotMode.Flee ? BotMode.Travel : mind.Mode;
            // A new fight: nothing is known of it yet.
            tac.EndFight();
        }
        mind.Mode = BotMode.Fight;
        mind.Foe = foe;
        mind.FightStartedMs = now;
        mind.FightInReachMs = now;
        mind.TilePath = null;
        tac.NextLookMs = now + 500;
        tac.SwitchedMs = now;
        tac.SampleFoe = 0;
        tac.FoeLossRate = 0;
        tac.NextSpellMs = now + _rng.Next(100, 500);
        NoteFoe(mind, foe, now);
        NoteHuntFoe(mind, foe);
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
        var tac = mind.Tac;
        if (client == null || foe == null || foe.IsDeleted || foe.IsDead || foe.MapIndex != ch.MapIndex ||
            ch.Position.GetDistanceTo(foe.Position) > 18 || now - mind.FightStartedMs > 180_000)
        {
            EndFight(mind, now, won: foe != null && foe.IsDead);
            return;
        }

        // A player killer does not follow its victim into the guards' reach.
        if (foe.IsPlayer && IsPk(mind) && IsGuardedAt(foe.Position))
        {
            GiveUpFoe(mind, foe, now);
            return;
        }

        // The fight looked over: who else is on it, and whether another foe comes first.
        foe = LookAbout(mind, foe, now);
        NoteFoe(mind, foe, now);

        int hp = Pct(ch);
        if (WhyRetreat(mind, foe, now, hp) is { } why)
        {
            StartRetreat(mind, foe, now, why);
            return;
        }

        bool caster = mind.IsCaster;
        // What it carries for itself: a potion when it matters, a bandage with the
        // skill for it. A caster heals and cures by spell first (below).
        if (TryDrink(mind, now, hp, foe))
        {
            mind.NextActMs = now + 400;
            return;
        }
        if (hp < (caster ? 45 : 65) && TryBandageSelf(mind, now))
        {
            mind.NextActMs = now + 400;
            return;
        }

        // The bow: are there arrows left?
        var weapon = ch.FightWeapon();
        if (weapon != null && CombatHelper.IsRangedWeapon(weapon))
        {
            CheckAmmo(mind, now);
            weapon = ch.FightWeapon();
            if (tac.OutOfAmmo && weapon != null && CombatHelper.IsRangedWeapon(weapon))
            {
                // Neither arrows nor a blade: nothing to fight with. Off to buy some.
                LeaveForAmmo(mind, foe, now);
                return;
            }
        }
        bool shoots = ShootsNow(mind);
        bool distant = shoots || (caster && !tac.Brawling);

        int dist = ch.Position.GetDistanceTo(foe.Position);
        // Keep the range the weapon wants: blades close in, bows and casters hang back.
        int want = WantedRange(mind, shoots);
        // In reach is where a blow lands: the combat rule wants sight even next to the
        // foe, so one on a ledge above or below is not in reach however near it stands.
        bool sight = dist <= 12 && Math.Abs(ch.Z - foe.Z) <= ReachZ && _world.CanSeeLOS(ch.Position, foe.Position);
        if (dist <= (distant ? 10 : want) && sight)
            mind.FightInReachMs = now;
        else if (now - mind.FightInReachMs > FoeGiveUpMs)
        {
            GiveUpFoe(mind, foe, now);
            return;
        }

        if (caster && ActMage(mind, foe, now, dist, sight, hp, IsGuardedAt(ch.Position)))
            return;
        // Out of mana it may have closed in to brawl; the range it wants is then a fist's.
        if (caster && tac.Brawling)
        {
            distant = false;
            want = WantedRange(mind, shoots);
        }

        // The engine drops the fight target on some refusals (a cast, a war-mode
        // change): a fighter that means to strike takes it up again.
        if ((shoots || !distant) && ch.FightTarget != foe.Uid && !ch.IsCasting)
            client.HandleAttack(foe.Uid.Value);

        if (shoots && Kite(mind, foe, now, dist, sight, want))
            return;

        bool stepped = false;
        bool run = dist > want + 2;
        if (dist > want || !sight)
            stepped = StepToward(mind, foe.Position, run, searchWithin: 14);
        else if (caster && distant && dist < want - 1 && IsOn(foe, ch) && !IsHeld(foe) && !ch.IsCasting)
        {
            // A caster does not let a blade come to it: back to its distance.
            run = true;
            stepped = StepToward(mind, Away(ch.Position, foe.Position, 4), run: true);
        }
        // Standing its ground it faces its foe, as a player turns to: where the
        // combat rules ask for it (COMBAT_FACECOMBAT) no blow starts otherwise.
        if (!stepped && sight && !ch.IsCasting) client.FaceTarget(foe);
        mind.NextActMs = now + (stepped ? MovementEngine.GetMoveDelay(ch.IsMounted, run) : Math.Min(StepDelay(mind, false, run, now), 350));
    }

    /// <summary>An archer with neither arrows nor a blade leaves the fight - running
    /// if the foe is after it - and its next trip is to where arrows are sold.</summary>
    private void LeaveForAmmo(BotMind mind, Character foe, long now)
    {
        mind.NextSupplyCheckMs = 0;
        mind.LingerUntilMs = Math.Min(mind.LingerUntilMs, now + 5000);
        if (IsOn(foe, mind.Char))
        {
            StartRetreat(mind, foe, now, "out of ammunition");
            return;
        }
        mind.Note(now, $"leaves {foe.GetName()} alone: out of ammunition");
        mind.AvoidFoes[foe.Uid.Value] = now + 60_000;
        EndFight(mind, now, won: false);
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
            mind.Tac.Escape = BotEscape.Run;
            mind.Tac.EscapeSinceMs = now;
            mind.Tac.GuardPoint = null;
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
        mind.Tac.EndFight();
        mind.Mode = mind.ResumeMode;
        mind.ResetLeg(now);
        mind.NextActMs = now + _rng.Next(800, 2500);
        // Whatever else was on it is seen at once, not after the walk to the body.
        mind.NextScanMs = now;
        // A player killer moves on from where it killed, after the body.
        if (won && IsPk(mind)) mind.LingerUntilMs = 0;
        // Won beside the player it goes about with: that is a bond.
        if (won && foe is { IsPlayer: false }) OnFightWonBeside(mind, now);
        // A kind it has beaten is one it goes for again.
        if (won && foe is { IsPlayer: false }) RememberBeaten(mind, foe);
        if (won && foe is { IsPlayer: false }) NoteHuntKill(mind);
        // The body of what it killed: supplies off a monster, anything off a victim.
        if (won && foe != null && !mind.Char.IsDead && StartLoot(mind, foe, now))
            mind.Tac.ArmsDirty = true;
    }

    /// <summary>Mend itself: a caster's heal (Greater Heal or Heal, as the wound and
    /// its mana call for), else a bandage when it has the Healing for one. Potions are
    /// drunk by <see cref="TryDrink"/>, behind their own delay.</summary>
    private bool TryHeal(BotMind mind, long now, int hp)
    {
        var ch = mind.Char;
        if (mind.Client == null) return false;
        if (mind.IsCaster && !ch.IsCasting && HealChoice(mind, hp, null) is { } heal && CastOn(mind, heal, ch, now))
        {
            mind.NextHealMs = now + 3000;
            return true;
        }
        if (TryBandageSelf(mind, now)) return true;
        mind.NextHealMs = now + 3000;
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

    /// <summary>Poisoned: Cure for a caster who can cast it, else a cure potion.</summary>
    private bool TryCure(BotMind mind, long now)
    {
        var ch = mind.Char;
        var client = mind.Client;
        mind.NextCureMs = now + 5000;
        if (client == null) return false;
        if (mind.IsCaster && !ch.IsCasting && CanCast(mind, SpellType.Cure) && CastOn(mind, SpellType.Cure, ch, now))
            return true;
        if (Clients.ClientWorldFeaturesHandler.HasPotionUsedMarker(ch)) return false;
        var potion = FindPotion(ch, SpellType.Cure, SpellType.ArchCure);
        if (potion == null) return false;
        client.HandleDoubleClick(potion.Uid.Value);
        WindowPotions++;
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

    /// <summary>A spell the bot can cast: in its book (when books are required), as a
    /// player can only cast what its book holds. Saved bots from an older kit can lack
    /// the higher circles.</summary>
    internal static bool HasSpell(Character ch, SpellType spell) =>
        !Character.SpellbookRequiredEnabled ||
        (ch.FindSpellbook((int)spell) is { } book && book.ContainsSpell((int)spell));

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
