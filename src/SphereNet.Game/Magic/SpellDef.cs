using SphereNet.Core.Enums;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Game.Magic;

/// <summary>
/// Spell definition. Maps to CSpellDef in Source-X CSpellDef.h.
/// Loaded from [SPELL ...] sections in scripts.
/// </summary>
public sealed class SpellDef
{
    public SpellType Id { get; init; }
    public string Name { get; set; } = "";
    public SpellFlag Flags { get; set; }
    public ushort ManaCost { get; set; }
    public ushort TithingCost { get; set; }
    public int Sound { get; set; }
    public string Runes { get; set; } = "";
    public string TargetPrompt { get; set; } = "";
    public ushort EffectId { get; set; }
    public ushort RuneItemId { get; set; }
    public ushort ScrollItemId { get; set; }
    public Layer Layer { get; set; }

    /// <summary>The [SPELL] script section carries at least one ON=@... stage
    /// (set at load time). Used to tell a trigger-scripted spell apart from a
    /// def that has no behaviour at all (see SpellEngine.IsInertSchoolSpell).</summary>
    public bool HasScriptedStages { get; set; }

    // Curves. CAST_TIME, EFFECT, DURATION and INTERRUPT are CValueCurveDef in the
    // reference (CSpellDef.h:69-72): a list of points spread across skill
    // 0-100.0, read with GetLinear (CValueDefs.cpp:90). The point list is the
    // authority; it keeps an explicit 0 endpoint ("10,0") and every segment of a
    // three-or-more point curve. CAST_TIME and DURATION points are tenths of a
    // second as written ("6" = 6 tenths, "6.0" = 60, "3*60.0" = 1800) - nothing
    // rescales them (CCharSpell.cpp:3499, :4308).
    // No CAST_TIME line is an empty curve, which reads as 0 (CValueDefs.cpp:107)
    // - the cast then takes the one-tenth floor, not an invented 1.5 s.
    public ValueCurve CastTimeCurve { get; set; } = ValueCurve.Empty;
    public ValueCurve EffectCurve { get; set; } = ValueCurve.Empty;
    public ValueCurve DurationCurve { get; set; } = ValueCurve.Empty;
    /// <summary>INTERRUPT curve (per-mille, legacy fixed-point "100.0" = 1000).
    /// Default: always disturbable - the reference initialises it to the single
    /// point 1000 (CSpellDef.cpp:85-87).</summary>
    public ValueCurve InterruptCurve { get; set; } = new([1000]);

    // Endpoint view of the curves, kept for code that builds a definition by hand.
    // Base is the first point; Scale is the TOP endpoint (the last point), NOT a
    // delta - and 0 when the curve has a single point. Setting either rebuilds a
    // one-point (constant) curve, or a two-point line when Scale is non-zero.
    public int CastTimeBase { get => First(CastTimeCurve); set => CastTimeCurve = Line(value, CastTimeScale, positiveTopOnly: true); }
    public int CastTimeScale { get => Top(CastTimeCurve); set => CastTimeCurve = Line(CastTimeBase, value, positiveTopOnly: true); }
    public int EffectBase { get => First(EffectCurve); set => EffectCurve = Line(value, EffectScale); }
    public int EffectScale { get => Top(EffectCurve); set => EffectCurve = Line(EffectBase, value); }
    public int DurationBase { get => First(DurationCurve); set => DurationCurve = Line(value, DurationScale); }
    public int DurationScale { get => Top(DurationCurve); set => DurationCurve = Line(DurationBase, value); }
    public int InterruptBase { get => First(InterruptCurve); set => InterruptCurve = Line(value, InterruptScale, positiveTopOnly: true); }
    public int InterruptScale { get => Top(InterruptCurve); set => InterruptCurve = Line(InterruptBase, value, positiveTopOnly: true); }

    private static int First(ValueCurve curve) => curve.Count > 0 ? curve[0] : 0;
    private static int Top(ValueCurve curve) => curve.Count > 1 ? curve[curve.Count - 1] : 0;

    private static ValueCurve Line(int baseValue, int top, bool positiveTopOnly = false)
    {
        bool hasTop = positiveTopOnly ? top > 0 : top != 0;
        return hasTop ? new ValueCurve([baseValue, top]) : new ValueCurve([baseValue]);
    }

    public ulong Group { get; set; }

    // Reagents (resource ID → amount)
    public Dictionary<ushort, int> Reagents { get; } = [];

    // Skill requirements (SkillType → minimum value)
    public Dictionary<SkillType, int> SkillReq { get; } = [];

    /// <summary>Get the primary casting skill for this spell.</summary>
    public SkillType GetPrimarySkill()
    {
        foreach (var kv in SkillReq)
            return kv.Key;
        return SkillType.Magery;
    }

    /// <summary>Get primary skill difficulty (0-1000).</summary>
    public int GetDifficulty()
    {
        foreach (var kv in SkillReq)
            return kv.Value;
        return 0;
    }

    /// <summary>Effect strength at the given skill level (0-1000): the EFFECT
    /// curve read with CValueCurveDef::GetLinear (CValueDefs.cpp:90).</summary>
    public int GetEffect(int skillLevel) => EffectCurve.GetLinear(skillLevel);

    /// <summary>Duration in tenths of a second at the given skill level
    /// (0-1000): m_Duration.GetLinear, already tenths (CCharSpell.cpp:4308).</summary>
    public int GetDuration(int skillLevel) => DurationCurve.GetLinear(skillLevel);

    /// <summary>Disturb chance (per-mille) at the given caster skill
    /// (reference m_Interrupt.GetLinear, CCharFight.cpp:888).</summary>
    public int GetInterruptChance(int skillLevel) => InterruptCurve.GetLinear(skillLevel);

    /// <summary>Cast time at the given skill level, in tenths of a second
    /// (reference m_CastTime.GetLinear, CCharSpell.cpp:3499). 1-tenth floor.</summary>
    public int GetCastTime(int skillLevel) => Math.Max(1, CastTimeCurve.GetLinear(skillLevel));

    public bool IsFlag(SpellFlag flag) => (Flags & flag) != 0;

    /// <summary>Read a property by key (for script access).</summary>
    public bool TryGetProperty(string key, out string value)
    {
        value = "";
        var upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "NAME": value = Name; return true;
            case "FLAGS": value = ((ulong)Flags).ToString(); return true;
            case "GROUP": value = Group.ToString(); return true;
            case "MANAUSE": value = ManaCost.ToString(); return true;
            case "SOUND": value = Sound.ToString(); return true;
            case "RUNES": value = Runes; return true;
            case "PROMPT_MSG": value = TargetPrompt; return true;
            case "EFFECT_ID": value = $"0{EffectId:X}"; return true;
            case "RUNE_ITEM": value = $"0{RuneItemId:X}"; return true;
            case "SCROLL_ITEM": value = $"0{ScrollItemId:X}"; return true;
            // CValueCurveDef::Write (CValueDefs.cpp:55): every point, comma separated.
            case "CAST_TIME": value = CastTimeCurve.Write(); return true;
            case "EFFECT": value = EffectCurve.Write(); return true;
            case "DURATION": value = DurationCurve.Write(); return true;
            case "INTERRUPT": value = InterruptCurve.Write(); return true;
        }

        // RESOURCES.n.KEY / RESOURCES.n.VAL
        if (upper.StartsWith("RESOURCES.", StringComparison.Ordinal))
        {
            var rest = upper[10..]; // "n.KEY" or "n.VAL"
            int dot = rest.IndexOf('.');
            if (dot > 0 && int.TryParse(rest[..dot], out int idx))
            {
                string sub = rest[(dot + 1)..];
                int i = 0;
                foreach (var kv in Reagents)
                {
                    if (i == idx)
                    {
                        value = sub == "KEY" ? $"0{kv.Key:X}" : kv.Value.ToString();
                        return true;
                    }
                    i++;
                }
            }
            return true;
        }

        return false;
    }

    /// <summary>Get the circle (1-8) for magery spells.</summary>
    public int GetCircle() => Id switch
    {
        >= SpellType.Clumsy and <= SpellType.Weaken => 1,
        >= SpellType.Agility and <= SpellType.Strength => 2,
        >= SpellType.Bless and <= SpellType.WallOfStone => 3,
        >= SpellType.ArchCure and <= SpellType.Recall => 4,
        >= SpellType.BladeSpirit and <= SpellType.SummonCreature => 5,
        >= SpellType.Dispel and <= SpellType.Reveal => 6,
        >= SpellType.ChainLightning and <= SpellType.Polymorph => 7,
        >= SpellType.Earthquake and <= SpellType.WaterElemental => 8,
        _ => 0,
    };

    /// <summary>Per-letter rune word lookup wired to the [RUNES] script table
    /// (Source-X g_Cfg.GetRune: 'A'→runes[0] ... 'Z'→runes[25]). Returns null
    /// when the table has no entry so the hardcoded mantra fallback applies.</summary>
    public static Func<char, string?>? RuneWordResolver { get; set; }

    /// <summary>Get spell power words. Script RUNES come in two forms: the full
    /// spoken words (".In Lor", parsed to "In Lor" — always contains a space)
    /// and the legacy letter abbreviation ("IL"). Source-X composes the
    /// abbreviation via the [RUNES] letter table (one word per letter); when
    /// that table isn't loaded fall back to the canonical UO mantra. UO
    /// mantras are always 2+ words, so a space reliably tells the spoken form
    /// from the abbreviation.</summary>
    public string GetPowerWords()
    {
        if (Runes.Contains(' '))
            return Runes;

        // Letter form — decode each letter through the [RUNES] table like
        // Source-X CChar::Spell_CastStart.
        if (RuneWordResolver != null && Runes.Length > 0)
        {
            var words = new List<string>(Runes.Length);
            bool anyResolved = false;
            foreach (char ch in Runes)
            {
                string? word = RuneWordResolver(ch);
                if (!string.IsNullOrEmpty(word) && word != "?")
                    anyResolved = true;
                words.Add(string.IsNullOrEmpty(word) ? "?" : word);
            }
            if (anyResolved)
                return string.Join(' ', words);
        }

        string fallback = GetDefaultPowerWords(Id);
        return !string.IsNullOrEmpty(fallback) ? fallback : Runes;
    }

    private static string GetDefaultPowerWords(SpellType spell) => spell switch
    {
        SpellType.Clumsy => "Uus Jux",
        SpellType.CreateFood => "In Mani Ylem",
        SpellType.Feeblemind => "Rel Wis",
        SpellType.Heal => "In Mani",
        SpellType.MagicArrow => "In Por Ylem",
        SpellType.NightSight => "In Lor",
        SpellType.ReactiveArmor => "Flam Sanct",
        SpellType.Weaken => "Des Mani",
        SpellType.Agility => "Ex Uus",
        SpellType.Cunning => "Uus Wis",
        SpellType.Cure => "An Nox",
        SpellType.Harm => "An Mani",
        SpellType.MagicTrap => "In Jux",
        SpellType.MagicUntrap => "An Jux",
        SpellType.Protection => "Uus Sanct",
        SpellType.Strength => "Uus Mani",
        SpellType.Bless => "Rel Sanct",
        SpellType.Fireball => "Vas Flam",
        SpellType.MagicLock => "An Por",
        SpellType.Poison => "In Nox",
        SpellType.Telekinesis => "Ort Por Ylem",
        SpellType.Teleport => "Rel Por",
        SpellType.Unlock => "Ex Por",
        SpellType.WallOfStone => "In Sanct Ylem",
        SpellType.ArchCure => "Vas An Nox",
        SpellType.ArchProtection => "Vas Uus Sanct",
        SpellType.Curse => "Des Sanct",
        SpellType.FireField => "In Flam Grav",
        SpellType.GreaterHeal => "In Vas Mani",
        SpellType.Lightning => "Por Ort Grav",
        SpellType.ManaDrain => "Ort Rel",
        SpellType.Recall => "Kal Ort Por",
        SpellType.BladeSpirit => "In Jux Hur Ylem",
        SpellType.DispelField => "An Grav",
        SpellType.Incognito => "Kal In Ex",
        SpellType.MagicReflect => "In Jux Sanct",
        SpellType.MindBlast => "Por Corp Wis",
        SpellType.Paralyze => "An Ex Por",
        SpellType.PoisonField => "In Nox Grav",
        SpellType.SummonCreature => "Kal Xen",
        SpellType.Dispel => "An Ort",
        SpellType.EnergyBolt => "Corp Por",
        SpellType.Explosion => "Vas Ort Flam",
        SpellType.Invisibility => "An Lor Xen",
        SpellType.Mark => "Kal Por Ylem",
        SpellType.MassCurse => "Vas Des Sanct",
        SpellType.ParalyzeField => "In Ex Grav",
        SpellType.Reveal => "Wis Quas",
        SpellType.ChainLightning => "Vas Ort Grav",
        SpellType.EnergyField => "In Sanct Grav",
        SpellType.Flamestrike => "Kal Vas Flam",
        SpellType.GateTravel => "Vas Rel Por",
        SpellType.ManaVampire => "Ort Sanct",
        SpellType.MassDispel => "Vas An Ort",
        SpellType.MeteorSwarm => "Flam Kal Des Ylem",
        SpellType.Polymorph => "Vas Ylem Rel",
        SpellType.Earthquake => "In Vas Por",
        SpellType.EnergyVortex => "Vas Corp Por",
        SpellType.Resurrection => "An Corp",
        SpellType.AirElemental => "Kal Des Flam Ylem",
        SpellType.SummonDaemon => "Kal Vas Xen Corp",
        SpellType.EarthElemental => "Kal Vas Xen Ylem",
        SpellType.FireElemental => "Kal Vas Xen Flam",
        SpellType.WaterElemental => "Kal Vas Xen An Flam",
        _ => "",
    };
}
