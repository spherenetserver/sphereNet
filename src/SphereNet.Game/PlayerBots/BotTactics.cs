using SphereNet.Core.Enums;
using SphereNet.Core.Types;

namespace SphereNet.Game.PlayerBots;

/// <summary>How a bot that breaks off a fight gets away.</summary>
public enum BotEscape { Run, Guards, Recall, Vanish, Hide }

/// <summary>What a fighting bot knows about the fight it is in and about the kinds of
/// creature it has met: who is on it, how fast each side is losing hit points, what it
/// has already cast, drunk or tried, and (read from its PLAYERBOT tags once) what killed
/// it and what it has beaten. Runtime only apart from those two lists.</summary>
public sealed class BotTactics
{
    // ---- The look about during a fight ----
    /// <summary>When the bot next looks over the fight.</summary>
    public long NextLookMs;
    /// <summary>Foes set on the bot at the last look, the current foe included.</summary>
    public int Attackers;
    /// <summary>When the foe was last changed; a new one is kept a moment.</summary>
    public long SwitchedMs;
    /// <summary>Damage each attacker has done so far (the attacker log's total at the
    /// last look) and what it did lately (halved every look, the new damage added).</summary>
    public Dictionary<uint, (int Total, int Recent)> Hurt { get; } = [];

    // ---- Trend ----
    public long SampleMs;
    public int SampleHits = -1;
    public uint SampleFoe;
    public int SampleFoeHits = -1;
    /// <summary>Hit points per second the bot has been losing lately (smoothed; healing
    /// counts against it, never below zero).</summary>
    public double LossRate;
    /// <summary>Hit points per second the foe has been losing lately.</summary>
    public double FoeLossRate;
    /// <summary>Whether it had anything to mend itself with when it last looked.</summary>
    public bool HealMeans;
    public long HealMeansMs;

    // ---- Spellcraft ----
    /// <summary>The bot has a cast under way that it started; the hesitation before the
    /// next one is counted from when it ends.</summary>
    public bool WasCasting;
    public long NextSpellMs;
    /// <summary>The spell that follows the one just cast (Explosion, then Energy Bolt).</summary>
    public SpellType Combo;
    /// <summary>A ground-target spell waiting for its cursor (a precast spell asks for
    /// the spot once the words are said).</summary>
    public SpellType GroundSpell;
    public long GroundRequestId;
    public Point3D GroundAt;
    public long GroundUntilMs;
    /// <summary>Wards cast for this fight, and whether the time for them is over.</summary>
    public int Wards;
    public bool WardsDone;
    /// <summary>The foe the opener (a curse, a paralysis) was cast at.</summary>
    public uint OpenedOn;
    /// <summary>Poison casts at the current foe that did not take.</summary>
    public uint PoisonedFoe;
    public int PoisonTries;
    public long NextPoisonMs;
    /// <summary>The foe a creature was summoned against, and when it was set on it.</summary>
    public uint SummonedFor;
    public long SummonOrderMs;
    public long NextBreakMs;
    /// <summary>Resting for mana in the middle of a fight, until then.</summary>
    public long RestUntilMs;
    /// <summary>Out of mana and closing in to strike with staff or fists.</summary>
    public bool Brawling;
    /// <summary>The last spell cast and the mana before it, to tell a fizzle.</summary>
    public SpellType LastSpell;

    // ---- Arms and potions ----
    public long NextPotionMs;
    public long NextArmsMs;
    /// <summary>The pack changed (a body was gone through): the arms are looked at soon.</summary>
    public bool ArmsDirty;
    public bool Boosted;
    /// <summary>The bow is out of arrows (or bolts): it fights hand to hand or leaves.</summary>
    public bool OutOfAmmo;
    public long NextAmmoCheckMs;

    // ---- Getting away ----
    public BotEscape Escape;
    public long EscapeSinceMs;
    public Point3D? GuardPoint;
    public long NextGuardLookMs;
    public bool CalledGuards;
    public long NextHideMs;
    public bool TriedVanish;
    public bool TriedRecall;
    /// <summary>Keeping clear of a fight it wants no part of, until then.</summary>
    public long ShyUntilMs;
    public long NextShyMs;

    // ---- What it fights, and what it remembers ----
    /// <summary>The kind of creature it fights (CHARDEF defname), kept for the moment it dies.</summary>
    public string? FoeKind;
    public uint FoeUid;
    public long FoeSeenMs;
    /// <summary>The foe as last sized up (a second and a half old at most).</summary>
    public BotThreat FoeThreat;
    public uint FoeThreatUid;
    public long FoeThreatMs;
    /// <summary>The foe is being fought to help someone, not for itself.</summary>
    public bool Assisting;
    /// <summary>Its own strength, worked out a few times a minute.</summary>
    public BotPower? Power;
    public long PowerMs;
    /// <summary>Kinds that killed it: defname, the unix minute, its strength then.</summary>
    public List<(string Kind, long Minute, int Strength)>? Killers;
    /// <summary>Kinds it has beaten, and how often.</summary>
    public Dictionary<string, int>? Beaten;

    /// <summary>Forget the fight (not what was learned from it).</summary>
    public void EndFight()
    {
        Attackers = 0;
        Hurt.Clear();
        HealMeansMs = 0;
        SampleHits = -1;
        SampleFoeHits = -1;
        SampleFoe = 0;
        LossRate = 0;
        FoeLossRate = 0;
        Combo = SpellType.None;
        GroundSpell = SpellType.None;
        Wards = 0;
        WardsDone = false;
        OpenedOn = 0;
        PoisonedFoe = 0;
        PoisonTries = 0;
        SummonedFor = 0;
        RestUntilMs = 0;
        Brawling = false;
        Boosted = false;
        Assisting = false;
        GuardPoint = null;
        NextGuardLookMs = 0;
        CalledGuards = false;
        TriedVanish = false;
        TriedRecall = false;
    }
}

/// <summary>What a bot can bring to a fight, as it would size itself up: the damage it
/// deals a second, how much of a beating it takes (hit points, armour and what it can
/// heal back), and how much damage its mana pays for (0: it does not fight with spells).</summary>
public sealed record BotPower(double Dps, double Staying, double ManaDamage, int Armor)
{
    /// <summary>One number for "how strong": compared with what it was when something killed it.</summary>
    public int Strength => (int)Math.Round(Dps * Staying / 10.0);
}

/// <summary>What a creature looks like to someone sizing it up: the damage it deals a
/// second, how much it takes to bring down, and whether it casts or poisons.</summary>
public readonly record struct BotThreat(double Dps, double Toughness, bool Casts, bool Poisons, bool Ranged);
