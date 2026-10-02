namespace SphereNet.Core.Enums;

/// <summary>
/// NPC brain AI type. Maps exactly to NPCBRAIN_TYPE in Source-X CChar.h.
/// </summary>
public enum NpcBrainType : byte
{
    None = 0,
    Animal = 1,
    Human = 2,
    Healer = 3,
    Guard = 4,
    Banker = 5,
    Vendor = 6,
    Stable = 7,
    Monster = 8,
    Berserk = 9,
    Dragon = 10,

    Qty = 11,
}

/// <summary>
/// Brain names and the older brain numbering. Sphere 0.55 / 0.56a-b (and the Sphere
/// 56T custom version built on them) numbered the brains NONE, ANIMAL, HUMAN, HEALER,
/// GUARD, BANKER, VENDOR, BEGGAR, STABLE, THIEF, MONSTER, BERSERK, UNDEAD, DRAGON,
/// VENDOR_OFFDUTY; Source-X dropped BEGGAR, THIEF, UNDEAD and VENDOR_OFFDUTY and
/// renumbered the rest. Its own backwards-compatibility table
/// (Scripts-X core/backwards_compatibility_56c.scp) maps the dropped names by meaning:
/// beggar/thief/towncrier to brain_human, undead to brain_monster, vendor_offduty to
/// brain_vendor, beserk to brain_berserk.
/// </summary>
public static class NpcBrainNames
{
    /// <summary>The 0.56 brain numbering, index = number written in the save.</summary>
    public static readonly string[] Legacy056Numbering =
    [
        "none", "animal", "human", "healer", "guard", "banker", "vendor", "beggar",
        "stable", "thief", "monster", "berserk", "undead", "dragon", "vendor_offduty",
    ];

    /// <summary>Every brain name, current or retired, a script pack may define as
    /// <c>brain_&lt;name&gt;</c>.</summary>
    public static readonly string[] KnownNames =
    [
        "none", "animal", "human", "healer", "guard", "banker", "vendor", "stable",
        "animal_trainer", "monster", "berserk", "dragon", "beggar", "thief", "undead",
        "vendor_offduty", "towncrier", "teacher", "beserk",
    ];

    /// <summary>Resolve a brain NAME (with or without the <c>brain_</c>/<c>npc_</c>
    /// prefix) to its brain, retired names included. Numbers are not handled here.</summary>
    public static bool TryParseName(string? value, out NpcBrainType brain)
    {
        brain = NpcBrainType.None;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string name = value.Trim();
        if (name.StartsWith("brain_", StringComparison.OrdinalIgnoreCase))
            name = name[6..];
        else if (name.StartsWith("npc_", StringComparison.OrdinalIgnoreCase))
            name = name[4..];
        if (name.Length == 0 || char.IsDigit(name[0]) || name[0] == '-')
            return false;

        switch (name.ToLowerInvariant())
        {
            case "animal_trainer": brain = NpcBrainType.Stable; return true;
            case "beggar":
            case "thief":
            case "towncrier":
            case "teacher": brain = NpcBrainType.Human; return true;
            case "undead": brain = NpcBrainType.Monster; return true;
            case "vendor_offduty": brain = NpcBrainType.Vendor; return true;
            case "beserk": brain = NpcBrainType.Berserk; return true;
            case "qty": return false;
        }
        return Enum.TryParse(name, true, out brain) && Enum.IsDefined(brain);
    }

    /// <summary>The brain a 0.56-numbered save value means, or null when the number
    /// is outside that numbering.</summary>
    public static NpcBrainType? FromLegacy056(int number) =>
        number >= 0 && number < Legacy056Numbering.Length &&
        TryParseName(Legacy056Numbering[number], out var brain)
            ? brain
            : null;

    /// <summary>True when a pack's brain_* numbers are not the Source-X ones: its
    /// brain_monster is defined and is not <see cref="NpcBrainType.Monster"/>.</summary>
    public static bool IsLegacyNumberedPack(Func<string, long?> resolveDef) =>
        resolveDef("brain_monster") is { } monster && monster != (long)NpcBrainType.Monster;

    /// <summary>The number -> brain table of 0.56-numbered data: the built-in 0.56
    /// table, overlaid with the pack's own brain_* numbers when the pack itself is
    /// 0.56-numbered (so a custom pack that renumbered a brain is honoured).</summary>
    public static Dictionary<int, NpcBrainType> BuildLegacyNumbering(Func<string, long?>? resolveDef)
    {
        var map = new Dictionary<int, NpcBrainType>();
        for (int n = 0; n < Legacy056Numbering.Length; n++)
        {
            if (FromLegacy056(n) is { } brain)
                map[n] = brain;
        }
        if (resolveDef != null && IsLegacyNumberedPack(resolveDef))
        {
            foreach (string name in KnownNames)
            {
                if (resolveDef("brain_" + name) is { } number && number is >= 0 and <= 255 &&
                    TryParseName(name, out var meaning))
                    map[(int)number] = meaning;
            }
        }
        return map;
    }

    // Script-facing numbering of the loaded pack; null while the pack numbers the
    // brains the Source-X way (or no pack is loaded). The engine itself always keeps
    // NpcBrainType, and saves write it; only what a script reads and writes as
    // NPC / NPCBRAIN goes through these.
    private static Dictionary<int, NpcBrainType>? _packFromNumber;
    private static Dictionary<NpcBrainType, int>? _packToNumber;

    /// <summary>Adopt the loaded pack's brain numbering for script-facing NPC reads and
    /// numeric writes. A Source-X-numbered pack (or none) clears it.</summary>
    public static void ConfigurePackNumbering(Func<string, long?>? resolveDef)
    {
        if (resolveDef == null || !IsLegacyNumberedPack(resolveDef))
        {
            ClearPackNumbering();
            return;
        }

        var fromNumber = BuildLegacyNumbering(resolveDef);
        var toNumber = new Dictionary<NpcBrainType, int>();
        foreach (NpcBrainType brain in Enum.GetValues<NpcBrainType>())
        {
            if (brain == NpcBrainType.Qty)
                continue;
            // The pack's own number for the brain's canonical name, then the 0.56
            // table's, then (a brain the numbering has no slot for) the engine's.
            string canonical = brain == NpcBrainType.Stable ? "stable" : brain.ToString().ToLowerInvariant();
            long? packNumber = resolveDef("brain_" + canonical);
            if (packNumber == null && brain == NpcBrainType.Stable)
                packNumber = resolveDef("brain_animal_trainer");
            int legacyIndex = Array.IndexOf(Legacy056Numbering, canonical);
            toNumber[brain] = packNumber is >= 0 and <= 255
                ? (int)packNumber.Value
                : legacyIndex >= 0 ? legacyIndex : (int)brain;
        }
        _packFromNumber = fromNumber;
        _packToNumber = toNumber;
    }

    /// <summary>Back to the Source-X numbering for scripts.</summary>
    public static void ClearPackNumbering()
    {
        _packFromNumber = null;
        _packToNumber = null;
    }

    /// <summary>The number a script reads for <paramref name="brain"/> (CCharNPC
    /// r_WriteVal NPC): the pack's number when the pack renumbers the brains.</summary>
    public static int ToScriptNumber(NpcBrainType brain) =>
        _packToNumber != null && _packToNumber.TryGetValue(brain, out int n) ? n : (int)brain;

    /// <summary>The brain a script's numeric NPC=&lt;n&gt; means, read in the pack's
    /// numbering when the pack renumbers the brains. A number that numbering has no
    /// brain for is passed through unchanged, as before.</summary>
    public static NpcBrainType FromScriptNumber(int number) =>
        _packFromNumber != null && _packFromNumber.TryGetValue(number, out var brain)
            ? brain
            : (NpcBrainType)number;
}
