using SphereNet.Core.Enums;

namespace SphereNet.Game.Magic;

/// <summary>
/// The layers Source-X equips spell memories on (LAYER_TYPE, uofiles_enums.h).
/// They sit above <see cref="Layer.Dragging"/> and are never sent to the
/// client. The numbers match the reference byte for byte, so a script's
/// FINDLAYER(n) means the same thing on both servers.
/// </summary>
public static class SpellLayers
{
    public const Layer Stats = (Layer)32;           // LAYER_SPELL_STATS
    public const Layer Reactive = (Layer)33;
    public const Layer NightSight = (Layer)34;
    public const Layer Protection = (Layer)35;
    public const Layer Incognito = (Layer)36;
    public const Layer MagicReflect = (Layer)37;
    public const Layer Paralyze = (Layer)38;
    public const Layer Invis = (Layer)39;
    public const Layer Polymorph = (Layer)40;
    public const Layer Summon = (Layer)41;
    public const Layer FlagPotion = (Layer)44;      // LAYER_FLAG_Potion
    public const Layer FlagDrunk = (Layer)47;       // LAYER_FLAG_Drunk
    public const Layer FlagHallucination = (Layer)49;
    public const Layer BloodOath = (Layer)55;
    public const Layer CurseWeapon = (Layer)56;
    public const Layer CorpseSkin = (Layer)57;
    public const Layer EvilOmen = (Layer)58;
    public const Layer PainSpike = (Layer)59;
    public const Layer MindRot = (Layer)60;
    public const Layer Strangle = (Layer)61;
    public const Layer ManaDrain = (Layer)79;
    public const Layer Explosion = (Layer)80;

    /// <summary>The layer Source-X OnSpellEffect equips this spell's memory on
    /// (CCharSpell.cpp:3875-4151): a fixed layer for the spells with their own
    /// case, the SPELL definition's LAYER for the rest when it is a spell layer
    /// (LAYER_SPELL_STATS or above), otherwise <see cref="Layer.Special"/> - the
    /// reference makes no memory for those, the engine keeps its own.</summary>
    public static Layer ForSpell(SpellType spell, SpellDef? def) => spell switch
    {
        SpellType.Clumsy or SpellType.Feeblemind or SpellType.Weaken or
        SpellType.Curse or SpellType.Agility or SpellType.Cunning or
        SpellType.Strength or SpellType.Bless or SpellType.MassCurse or
        SpellType.Trance or SpellType.Regenerate => Stats,
        SpellType.NightSight => NightSight,
        SpellType.ReactiveArmor => Reactive,
        SpellType.ManaDrain => ManaDrain,
        SpellType.MagicReflect => MagicReflect,
        SpellType.Protection or SpellType.ArchProtection or
        SpellType.Shield or SpellType.Steelskin or SpellType.Stoneskin => Protection,
        SpellType.SummonCreature => Summon,
        SpellType.Explosion => Explosion,
        SpellType.Invisibility => Invis,
        SpellType.Incognito => Incognito,
        SpellType.Paralyze or SpellType.ParalyzeField or
        SpellType.Stone or SpellType.ParticleForm => Paralyze,
        SpellType.Light => FlagPotion,
        SpellType.Hallucination => FlagHallucination,
        SpellType.Ale or SpellType.Wine or SpellType.Liquor => FlagDrunk,
        SpellType.WraithForm or SpellType.HorrificBeast or SpellType.LichForm or
        SpellType.VampiricEmbrace or SpellType.StoneForm or SpellType.ReaperForm or
        SpellType.Polymorph or SpellType.Chameleon or SpellType.BeastForm or
        SpellType.MonsterForm => Polymorph,
        SpellType.BloodOath => BloodOath,
        SpellType.CorpseSkin => CorpseSkin,
        SpellType.EvilOmen => EvilOmen,
        SpellType.MindRot => MindRot,
        SpellType.PainSpike => PainSpike,
        SpellType.Strangle => Strangle,
        SpellType.CurseWeapon => CurseWeapon,
        _ => def != null && def.Layer >= Stats ? def.Layer : Layer.Special,
    };
}
