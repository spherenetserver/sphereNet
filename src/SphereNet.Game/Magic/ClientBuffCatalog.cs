using SphereNet.Core.Enums;

namespace SphereNet.Game.Magic;

/// <summary>Source-X buff icon and cliloc mapping used by packet 0xDF.</summary>
public readonly record struct ClientBuffDefinition(
    BuffIcon Icon, uint TitleCliloc, uint DescriptionCliloc);

public static class ClientBuffCatalog
{
    private static readonly IReadOnlyDictionary<BuffIcon, ClientBuffDefinition> s_byIcon =
        new Dictionary<BuffIcon, ClientBuffDefinition>
        {
            [BuffIcon.Hidden] = new(BuffIcon.Hidden, 1075655, 1075656),
            [BuffIcon.ActiveMeditation] = new(BuffIcon.ActiveMeditation, 1075657, 1075658),
            [BuffIcon.NightSight] = new(BuffIcon.NightSight, 1075643, 1075644),
            [BuffIcon.Clumsy] = new(BuffIcon.Clumsy, 1075831, 1075832),
            [BuffIcon.Feeblemind] = new(BuffIcon.Feeblemind, 1075833, 1075834),
            [BuffIcon.Weaken] = new(BuffIcon.Weaken, 1075837, 1075838),
            [BuffIcon.Curse] = new(BuffIcon.Curse, 1075835, 1075836),
            [BuffIcon.MassCurse] = new(BuffIcon.MassCurse, 1075839, 1075840),
            [BuffIcon.Strength] = new(BuffIcon.Strength, 1075845, 1075846),
            [BuffIcon.Agility] = new(BuffIcon.Agility, 1075841, 1075842),
            [BuffIcon.Cunning] = new(BuffIcon.Cunning, 1075843, 1075844),
            [BuffIcon.Bless] = new(BuffIcon.Bless, 1075847, 1075848),
            [BuffIcon.ReactiveArmor] = new(BuffIcon.ReactiveArmor, 1075812, 1070722),
            [BuffIcon.Protection] = new(BuffIcon.Protection, 1075814, 1070722),
            [BuffIcon.ArchProtection] = new(BuffIcon.ArchProtection, 1075816, 1070722),
            [BuffIcon.Poison] = new(BuffIcon.Poison, 1017383, 1070722),
            [BuffIcon.Incognito] = new(BuffIcon.Incognito, 1075819, 1075820),
            [BuffIcon.Paralyze] = new(BuffIcon.Paralyze, 1075827, 1075828),
            [BuffIcon.MagicReflection] = new(BuffIcon.MagicReflection, 1075817, 1070722),
            [BuffIcon.Invisibility] = new(BuffIcon.Invisibility, 1075825, 1075826),
            [BuffIcon.Polymorph] = new(BuffIcon.Polymorph, 1075824, 1070722),
        };

    private static readonly IReadOnlyDictionary<SpellType, BuffIcon> s_spellIcons =
        new Dictionary<SpellType, BuffIcon>
        {
            [SpellType.NightSight] = BuffIcon.NightSight,
            [SpellType.Clumsy] = BuffIcon.Clumsy,
            [SpellType.Feeblemind] = BuffIcon.Feeblemind,
            [SpellType.Weaken] = BuffIcon.Weaken,
            [SpellType.Curse] = BuffIcon.Curse,
            [SpellType.MassCurse] = BuffIcon.MassCurse,
            [SpellType.Strength] = BuffIcon.Strength,
            [SpellType.Agility] = BuffIcon.Agility,
            [SpellType.Cunning] = BuffIcon.Cunning,
            [SpellType.Bless] = BuffIcon.Bless,
            [SpellType.ReactiveArmor] = BuffIcon.ReactiveArmor,
            [SpellType.Protection] = BuffIcon.Protection,
            [SpellType.ArchProtection] = BuffIcon.ArchProtection,
            [SpellType.Poison] = BuffIcon.Poison,
            [SpellType.Incognito] = BuffIcon.Incognito,
            [SpellType.Paralyze] = BuffIcon.Paralyze,
            [SpellType.MagicReflect] = BuffIcon.MagicReflection,
            [SpellType.Invisibility] = BuffIcon.Invisibility,
            [SpellType.Polymorph] = BuffIcon.Polymorph,
        };

    public static bool TryGet(BuffIcon icon, out ClientBuffDefinition definition) =>
        s_byIcon.TryGetValue(icon, out definition);

    public static bool TryGet(SpellType spell, out ClientBuffDefinition definition)
    {
        if (s_spellIcons.TryGetValue(spell, out var icon))
            return TryGet(icon, out definition);
        definition = default;
        return false;
    }
}
