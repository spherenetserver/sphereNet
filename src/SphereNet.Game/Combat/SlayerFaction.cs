using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Combat;

/// <summary>Slayer/faction groups (Source-X CFactionDef::Group). Each group has
/// an opposing group (Humanoid vs Undead, Arachnid vs Reptilian, Abyss vs
/// Fey+Elemental); an NPC belongs to one via FACTION_GROUP, an item targets one
/// via SLAYER_GROUP.</summary>
[Flags]
public enum FactionGroup : uint
{
    None      = 0,
    Fey       = 0x01,
    Elemental = 0x02,
    Abyss     = 0x04,
    Humanoid  = 0x08,
    Undead    = 0x10,
    Arachnid  = 0x20,
    Reptilian = 0x40,
}

/// <summary>
/// A group+species faction pairing — Source-X CFactionDef, used both as an
/// NPC's FACTION (FACTION_GROUP / FACTION_SPECIES) and an item's SLAYER
/// (SLAYER_GROUP / SLAYER_SPECIES). Species 1 is always the Super Slayer of
/// its group; higher species are the Lesser ("single") Slayers. The bonus
/// tables are a faithful port of CFactionDef.cpp — including its quirks — so
/// script data behaves identically to Source-X.
/// </summary>
public readonly struct SlayerFaction
{
    public const int SuperSlayerSpecies = 1;

    public FactionGroup Group { get; }
    public int Species { get; }

    public SlayerFaction(FactionGroup group, int species)
    {
        Group = group;
        Species = species;
    }

    public bool IsNone => Group == FactionGroup.None || Species == 0;

    /// <summary>An NPC's FACTION from its FACTION_GROUP / FACTION_SPECIES tags
    /// (chardef lines flow in through the def-tag apply).</summary>
    public static SlayerFaction FromChar(Character ch) => new(
        (FactionGroup)ReadNum(ch.TryGetTag("FACTION_GROUP", out var g) ? g : null),
        (int)ReadNum(ch.TryGetTag("FACTION_SPECIES", out var s) ? s : null));

    /// <summary>An item's SLAYER from its SLAYER_GROUP / SLAYER_SPECIES tags,
    /// falling back to the ITEMDEF's def-tags when the instance has none.</summary>
    public static SlayerFaction FromItem(Item item)
    {
        long group = ReadNum(item.TryGetTag("SLAYER_GROUP", out var g) ? g : null);
        long species = ReadNum(item.TryGetTag("SLAYER_SPECIES", out var s) ? s : null);
        if (group == 0 && species == 0)
        {
            var def = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
            if (def != null)
            {
                group = ReadNum(def.TagDefs.Get("SLAYER_GROUP"));
                species = ReadNum(def.TagDefs.Get("SLAYER_SPECIES"));
            }
        }
        return new SlayerFaction((FactionGroup)group, (int)species);
    }

    private static long ReadNum(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out long hex))
            return hex;
        return long.TryParse(raw, out long dec) ? dec : 0;
    }

    /// <summary>Opposing-group table (CFactionDef::IsOppositeGroup): Abyss
    /// opposes both Fey and Elemental; the other pairs are symmetric.</summary>
    public bool IsOppositeGroup(SlayerFaction target) => (Group, target.Group) switch
    {
        (FactionGroup.Elemental, FactionGroup.Abyss) => true,
        (FactionGroup.Abyss, FactionGroup.Fey) => true,
        (FactionGroup.Abyss, FactionGroup.Elemental) => true,
        (FactionGroup.Fey, FactionGroup.Abyss) => true,
        (FactionGroup.Reptilian, FactionGroup.Arachnid) => true,
        (FactionGroup.Arachnid, FactionGroup.Reptilian) => true,
        (FactionGroup.Humanoid, FactionGroup.Undead) => true,
        (FactionGroup.Undead, FactionGroup.Humanoid) => true,
        _ => false,
    };

    /// <summary>Super Slayer match (CFactionDef::IsSuperSlayerVersus, ported
    /// verbatim): the target must carry the group-generic species (1). The
    /// (Abyss vs Elemental) row is Source-X's own table — kept as-is for
    /// data parity.</summary>
    public bool IsSuperSlayerVersus(SlayerFaction target)
    {
        if (target.Species != SuperSlayerSpecies)
            return false;
        return (Group, target.Group) switch
        {
            (FactionGroup.Fey, FactionGroup.Fey) => true,
            (FactionGroup.Elemental, FactionGroup.Elemental) => true,
            (FactionGroup.Abyss, FactionGroup.Elemental) => true,
            (FactionGroup.Humanoid, FactionGroup.Humanoid) => true,
            (FactionGroup.Undead, FactionGroup.Undead) => true,
            (FactionGroup.Arachnid, FactionGroup.Arachnid) => true,
            (FactionGroup.Reptilian, FactionGroup.Reptilian) => true,
            _ => false,
        };
    }

    /// <summary>Lesser ("single") Slayer match: shared group bit + exact
    /// species (CFactionDef::IsLesserSlayerVersus).</summary>
    public bool IsLesserSlayerVersus(SlayerFaction target) =>
        ((uint)Group & (uint)target.Group) != 0 && Species == target.Species;

    public bool HasSuperSlayer => Group != FactionGroup.None && Species == SuperSlayerSpecies;

    // Source-X CFactionDef.cpp damage constants: Lesser x3, Super x2,
    // opposite-group penalty x2 (negated — see GetSlayerDamagePenaltyPercent).
    private const int LesserBonusPercent = 200;
    private const int SuperBonusPercent = 100;
    private const int OppositePenaltyPercent = 100;

    /// <summary>Bonus damage % a slayer deals to a matching victim faction —
    /// the Lesser match is checked first, exactly like Source-X.</summary>
    public int GetSlayerDamageBonusPercent(SlayerFaction target)
    {
        if (IsLesserSlayerVersus(target))
            return LesserBonusPercent;
        if (IsSuperSlayerVersus(target))
            return SuperBonusPercent;
        return 0;
    }

    /// <summary>Opposite-group penalty (CFactionDef::GetSlayerDamagePenaltyPercent):
    /// a Super Slayer used against its opposing group returns -100, which the
    /// damage formula folds in as a reduction — Source-X's exact arithmetic.</summary>
    public int GetSlayerDamagePenaltyPercent(SlayerFaction target) =>
        HasSuperSlayer && IsOppositeGroup(target) ? -OppositePenaltyPercent : 0;
}
