using SphereNet.Core.Enums;
using System.Globalization;
using SphereNet.Core.Types;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// Item definition template. Maps to CItemBase in Source-X.
/// Loaded lazily from [ITEMDEF] sections.
/// </summary>
public sealed class ItemDef : BaseDef
{
    public ItemType Type { get; set; } = ItemType.Normal;
    public string TypeRaw { get; set; } = "";
    public ushort FlipId { get; set; }
    public int Weight { get; set; }
    public bool HasWeight { get; set; }
    public Layer Layer { get; set; }
    public int ValueMin { get; set; }
    public int ValueMax { get; set; }
    public ulong QwFlags { get; set; }
    public CanEquipFlags CanUse { get; set; }
    public static Func<string, long?>? DefNameResolver { get; set; }

    public int Speed { get; set; }
    public SkillType Skill { get; set; } = SkillType.None;
    public bool HasSkill { get; set; }
    public int ReqStr { get; set; }
    public bool Dye { get; set; }
    public bool Flip { get; set; }
    public bool Repair { get; set; }
    public int HitsMin { get; set; }
    public int HitsMax { get; set; }
    public bool Replicate { get; set; }
    public bool TwoHands { get; set; }
    public uint TData1 { get; set; }
    public uint TData2 { get; set; }
    public uint TData3 { get; set; }
    public uint TData4 { get; set; }
    // Raw TDATA values when they were a defname (e.g. crops store the fruit
    // defname in TDATA3) — resolved to a baseid lazily at use time, after all
    // defnames are loaded.
    public string? TData1Name { get; set; }
    public string? TData2Name { get; set; }
    public string? TData3Name { get; set; }
    public string? TData4Name { get; set; }
    public string? DisplayIdRef { get; set; }
    /// <summary>Which TDATA1..4 (bits 0..3) the section wrote itself, even as 0.
    /// Source-X's ID=&lt;base&gt; copies the base's TDATA (CItemBase::CopyBasic,
    /// CItemBase.cpp:191-194) and a later TDATAn line overrides it, so an explicit
    /// "TDATA3=0" (no ammo) must be told apart from an unset one that inherits.</summary>
    public byte TDataSetMask { get; set; }
    public ulong TFlags { get; set; }
    public ushort AmmoAnim { get; set; }
    public ushort AmmoAnimHue { get; set; }
    public byte AmmoAnimRender { get; set; }
    public ushort AmmoCont { get; set; }
    public string AmmoType { get; set; } = "";
    public string ResMake { get; set; } = "";
    public string DupeList { get; set; } = "";
    public int WeightReduction { get; set; }

    /// <summary>DUPELIST parsed to graphic ids, cached. Source-X "pile" items
    /// (ore, etc.) show a larger graphic as the stack grows: amount 1 = base id,
    /// 2 = DupeIds[0], 3 = DupeIds[1], 4+ = last. Used by Item.DispIdFull.</summary>
    private ushort[]? _dupeIds;
    public ushort[] DupeIds
    {
        get
        {
            if (_dupeIds == null)
            {
                if (string.IsNullOrWhiteSpace(DupeList))
                    _dupeIds = System.Array.Empty<ushort>();
                else
                {
                    var parts = DupeList.Split(',',
                        System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
                    var list = new System.Collections.Generic.List<ushort>(parts.Length);
                    foreach (var p in parts)
                    {
                        ParseHexOrDec(p, out ushort id);
                        if (id != 0)
                            list.Add(id);
                    }
                    _dupeIds = list.ToArray();
                }
            }
            return _dupeIds;
        }
    }

    public ushort DupItemId { get; set; }
    public List<ResourceId> SkillMake { get; } = [];
    public string SkillMakeRaw { get; set; } = "";
    public string ResourcesRaw { get; set; } = "";

    public ItemDef(ResourceId id) : base(id) { }

    /// <summary>
    /// Load properties from script key-value pairs.
    /// </summary>
    public void LoadFromKey(string key, string value)
    {
        switch (key.ToUpperInvariant())
        {
            case "NAME": Name = value; break;
            case "TYPE": TypeRaw = value.Trim(); Type = ParseItemType(value); break;
            case "WEIGHT": Weight = ParseWeight(value); HasWeight = true; break;
            case "LAYER": Layer = ParseLayer(value); break;
            case "FLIPID": ParseHexOrDec(value, out ushort f); FlipId = f; break;
            case "VALUE": (ValueMin, ValueMax) = ParseRange(value); break;
            case "DAM": (AttackMin, AttackMax) = ParseRange(value); break;
            case "ARMOR": (DefenseMin, DefenseMax) = ParseRange(value); break;
            case "CAN": Can = (CanFlags)ParseFlags(value); break;
            case "CANUSE": CanUse = (CanEquipFlags)ParseFlags(value); break;
            case "HEIGHT": byte.TryParse(value, out byte h); Height = h; break;
            case "DUPEITEM": ParseHexOrDec(value, out ushort dup); DupItemId = dup; break;
            case "ID": ParseHexOrDec(value, out ushort id); DispIndex = id; break;
            case "DISPID": ParseHexOrDec(value, out ushort did); DispIndex = did; break;
            case "DEFNAME": DefName = value; break;
            case "EVENTS":
            case "TEVENTS":
                ParseEventsList(value);
                break;
            case "SKILLMAKE": SkillMakeRaw = value.Trim(); ParseResourceList(value, SkillMake); break;
            case "RESOURCES": ResourcesRaw = value.Trim(); ParseResourceList(value, BaseResources); break;
            case "RANGE": (RangeMin, RangeMax) = ParseRange(value); break;
            case "RANGEH": int.TryParse(value, out int rh); RangeMax = rh; break;
            case "RANGEL": int.TryParse(value, out int rl); RangeMin = rl; break;
            case "SPEED": int.TryParse(value, out int spd); Speed = spd; break;
            case "SKILL":
                HasSkill = Enum.TryParse(value, true, out SkillType sk);
                Skill = HasSkill ? sk : SkillType.None;
                break;
            case "REQSTR": int.TryParse(value, out int rs); ReqStr = rs; break;
            // The CAN_I_* flag keys (CItemBase.cpp:1560-1655): no argument sets the
            // bit, otherwise a non-zero number sets it and zero clears it - on the
            // definition's CAN mask, which is where an instance reads them back.
            case "DYE": Dye = ApplyCanFlagKey(value, CanFlags.I_Dye); break;
            case "FLIP": Flip = ApplyCanFlagKey(value, CanFlags.I_Flip); break;
            case "REPAIR": Repair = ApplyCanFlagKey(value, CanFlags.I_Repair); break;
            case "ENCHANT": ApplyCanFlagKey(value, CanFlags.I_Enchant); break;
            case "EXCEPTIONAL": ApplyCanFlagKey(value, CanFlags.I_Exceptional); break;
            case "IMBUE": ApplyCanFlagKey(value, CanFlags.I_Imbue); break;
            case "REFORGE": ApplyCanFlagKey(value, CanFlags.I_Reforge); break;
            case "RETAINCOLOR": ApplyCanFlagKey(value, CanFlags.I_RetainColor); break;
            case "MAKERSMARK": ApplyCanFlagKey(value, CanFlags.I_MakersMark); break;
            case "RECYCLE": ApplyCanFlagKey(value, CanFlags.I_Recycle); break;
            case "HITS":
            case "MAXHITS":
            case "HITSMAX":
                var (hmin, hmax) = ParseRange(value);
                HitsMin = hmin;
                HitsMax = hmax > 0 ? hmax : hmin;
                break;
            case "REPLICATE": Replicate = ApplyCanFlagKey(value, CanFlags.I_Replicate); break;
            case "TWOHANDS": TwoHands = value != "0"; break;
            case "TDATA1":
                if (!ParseHexOrDecUInt(value, out uint td1) && value.Length > 0 &&
                    (char.IsLetter(value[0]) || value[0] == '_')) TData1Name = value.Trim();
                TData1 = td1; TDataSetMask |= 1; break;
            case "TDATA2":
                if (!ParseHexOrDecUInt(value, out uint td2) && value.Length > 0 &&
                    (char.IsLetter(value[0]) || value[0] == '_')) TData2Name = value.Trim();
                TData2 = td2; TDataSetMask |= 2; break;
            case "TDATA3":
                if (!ParseHexOrDecUInt(value, out uint td3) && value.Length > 0 &&
                    (char.IsLetter(value[0]) || value[0] == '_')) TData3Name = value.Trim();
                TData3 = td3; TDataSetMask |= 4; break;
            case "TDATA4":
                if (!ParseHexOrDecUInt(value, out uint td4) && value.Length > 0 &&
                    (char.IsLetter(value[0]) || value[0] == '_')) TData4Name = value.Trim();
                TData4 = td4; TDataSetMask |= 8; break;
            case "TFLAGS": ParseHexOrDecULong(value, out ulong tf); TFlags = tf; break;
            case "AMMOANIM": ParseHexOrDec(value, out ushort aa); AmmoAnim = aa; break;
            case "AMMOANIMHUE": ParseHexOrDec(value, out ushort aah); AmmoAnimHue = aah; break;
            case "AMMOANIMRENDER": byte.TryParse(value, out byte aar); AmmoAnimRender = aar; break;
            case "AMMOCONT": ParseHexOrDec(value, out ushort ac); AmmoCont = ac; break;
            case "AMMOTYPE": AmmoType = value.Trim(); break;
            case "RESMAKE": ResMake = value.Trim(); break;
            case "DUPELIST": DupeList = value.Trim(); break;
            case "WEIGHTREDUCTION": int.TryParse(value, out int wr); WeightReduction = wr; break;
            case "RESLEVEL": byte.TryParse(value, out byte rsl); ResLevel = rsl; break;
            case "RESDISPDNHUE": ParseHexOrDec(value, out ushort rdh); ResDispDnHue = rdh; break;
            case "RESDISPDNID": ParseHexOrDec(value, out ushort rdi); ResDispDnId = rdi; ResDispDnIdRaw = value.Trim(); break;
            // Source-X CCPropsItemEquippable SLAYER_GROUP/SLAYER_SPECIES (the
            // Slayer system's item side) — stored as def-tags; the combat
            // engine reads them with an instance-tag-first fallback.
            case "SLAYER_GROUP":
            case "SLAYER_SPECIES":
                TagDefs.Set(key, value.Trim());
                break;
            // AOS on-hit combat properties (HITLEECHLIFE, HITFIREBALL, ...):
            // same def-tag flow as the SLAYER pair.
            case var _ when AosOnHitProperties.Contains(key):
                TagDefs.Set(key.ToUpperInvariant(), value.Trim());
                break;
            // The AOS suit family an equippable carries, same def-tag flow. The
            // combat engine reads these off a worn item's def tags already.
            case var _ when AosEquipProperties.Contains(key):
                TagDefs.Set(key.ToUpperInvariant(), value.Trim());
                break;
            // The stat and pool bonuses an equippable grants, same def-tag flow.
            case var _ when EquipmentStatBonuses.Contains(key):
                TagDefs.Set(key.ToUpperInvariant(), value.Trim());
                break;
            case var _ when SpellCastingProperties.Contains(key):
                TagDefs.Set(key.ToUpperInvariant(), value.Trim());
                break;
            case CombatSpeedProperties.IncreaseSwingSpeed:
                TagDefs.Set(CombatSpeedProperties.IncreaseSwingSpeed, value.Trim());
                break;
            default:
                if (key.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase))
                {
                    TagDefs.Set(key[4..], value);
                    break;
                }
                // Previously dropped with zero visibility — count it so a
                // real pack load can report what it lost.
                TagDefs.Set(key.ToUpperInvariant(), value);
                UnknownKeyDiagnostics.Record("ITEMDEF", key);
                break;
        }
    }

    /// <summary>One CAN_I_* flag key: set or clear <paramref name="flag"/> on
    /// <see cref="Can"/> and report whether it ended up set.</summary>
    private bool ApplyCanFlagKey(string value, CanFlags flag)
    {
        string s = value.Trim();
        bool on = s.Length == 0 ||
            !SphereNet.Core.Types.ScriptNumber.TryParseArgument(s, out long n) || n != 0;
        Can = on ? Can | flag : Can & ~flag;
        return on;
    }

    private void ParseEventsList(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in parts)
        {
            if (!EventNamesRaw.Contains(name, StringComparer.OrdinalIgnoreCase))
                EventNamesRaw.Add(name);
            var rid = ResourceId.FromEventName(name);
            if (rid.IsValid && !Events.Contains(rid))
                Events.Add(rid);
        }
    }

    private static int ParseWeight(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return 0;

        // Source-X stores item weight in tenths of a stone. Integer script
        // values are whole stones, while decimal values are already expressed
        // as stones with one decimal place (1.0 => 10, 0.1 => 1).
        if (trimmed.Contains('.'))
        {
            if (decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec))
                return Math.Max(0, (int)Math.Round(dec * 10m, MidpointRounding.AwayFromZero));
            return 0;
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int whole)
            ? Math.Max(0, whole * 10)
            : 0;
    }

    private static void ParseResourceList(string value, List<ResourceId> list)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in parts)
        {
            var rid = ResourceId.FromString(name);
            if (rid.IsValid)
                list.Add(rid);
        }
    }

    private static (int Min, int Max) ParseRange(string value)
    {
        value = value.Trim();
        if (value.Length > 0 && value[0] == '{')
        {
            string inner = value.Trim('{', '}').Trim();
            var parts = inner.Split(new[] { ' ', '\t', ',' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out int bmin) && int.TryParse(parts[1], out int bmax))
                return (bmin, bmax);
            if (parts.Length == 1 && int.TryParse(parts[0], out int bsingle))
                return (bsingle, bsingle);
            return (0, 0);
        }

        int comma = value.IndexOf(',');
        if (comma >= 0)
        {
            int.TryParse(value.AsSpan(0, comma).Trim(), out int min);
            int.TryParse(value.AsSpan(comma + 1).Trim(), out int max);
            return (min, max);
        }
        int.TryParse(value, out int single);
        return (single, single);
    }

    private static void ParseHexOrDec(string value, out ushort result)
    {
        result = 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("0", StringComparison.OrdinalIgnoreCase) && value.Length > 1)
        {
            var span = value.AsSpan();
            if (span.Length > 2 && (span[1] == 'x' || span[1] == 'X'))
                ushort.TryParse(span[2..], System.Globalization.NumberStyles.HexNumber, null, out result);
            else
                ushort.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out result);
        }
        else
        {
            ushort.TryParse(value, out result);
        }
    }

    private static bool ParseHexOrDecUInt(string value, out uint result)
    {
        result = 0;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int signed) && signed < 0)
        {
            result = unchecked((uint)signed);
            return true;
        }
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        if (value.StartsWith("0", StringComparison.OrdinalIgnoreCase) && value.Length > 1)
            return uint.TryParse(value.AsSpan(), System.Globalization.NumberStyles.HexNumber, null, out result);
        return uint.TryParse(value, out result);
    }

    private static uint ParseFlags(string value)
    {
        uint result = 0;
        foreach (string raw in value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (ParseHexOrDecUInt(raw, out uint numeric))
            {
                result |= numeric;
                continue;
            }
            if (DefNameResolver?.Invoke(raw.Trim()) is long resolved)
                result |= unchecked((uint)resolved);
        }
        return result;
    }

    private static Layer ParseLayer(string value)
    {
        string token = value.Trim();
        if (ParseHexOrDecUInt(token, out uint numeric) && numeric <= byte.MaxValue)
            return (Layer)(byte)numeric;
        if (DefNameResolver?.Invoke(token) is long resolved && resolved is >= 0 and <= byte.MaxValue)
            return (Layer)(byte)resolved;

        string enumName = token.StartsWith("layer_", StringComparison.OrdinalIgnoreCase)
            ? token[6..]
            : token;
        enumName = enumName.Replace("hand1", "OneHanded", StringComparison.OrdinalIgnoreCase)
            .Replace("hand2", "TwoHanded", StringComparison.OrdinalIgnoreCase)
            .Replace("bankbox", "BankBox", StringComparison.OrdinalIgnoreCase)
            .Replace("beard", "FacialHair", StringComparison.OrdinalIgnoreCase)
            .Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse(enumName, true, out Layer layer) ? layer : Layer.None;
    }

    private static void ParseHexOrDecULong(string value, out ulong result)
    {
        result = 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            ulong.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        else if (value.StartsWith("0", StringComparison.OrdinalIgnoreCase) && value.Length > 1)
            ulong.TryParse(value.AsSpan(), System.Globalization.NumberStyles.HexNumber, null, out result);
        else
            ulong.TryParse(value, out result);
    }

    /// <summary>
    /// Parse Source-X type strings (t_weapon_sword, t_armor, etc.)
    /// to the ItemType enum. Strips the t_ prefix and underscores.
    /// Also handles numeric values.
    /// </summary>
    /// <summary>Read a script TYPE name - "t_ship", "t_multi_custom", a bare number.
    /// A [MULTIDEF] block declares its type with the same spelling an ITEMDEF does, so
    /// the two read it the same way.</summary>
    public static ItemType ParseTypeName(string? value) => ParseItemType(value ?? "");

    private static ItemType ParseItemType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ItemType.Normal;

        // Numeric value
        if (ushort.TryParse(value, out ushort numType))
            return (ItemType)numType;

        // Strip t_ prefix used by Source-X scripts
        string normalized = value.Trim();
        if (normalized.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        // Remove underscores for enum matching (t_weapon_sword → weaponsword → WeaponSword)
        normalized = normalized.Replace("_", "");

        if (Enum.TryParse(normalized, true, out ItemType result))
            return result;

        // The reference's own spelling, where this enum's member is named
        // differently for the same NUMBER. Both of these are tool types the packs
        // declare with the reference name (t_cooking on nine itemdefs, t_cartography
        // on three), and the member here is CookingTool / CartographyTool - so the
        // name matched nothing and the item loaded as an ordinary one, with the
        // craft menu its double-click is supposed to open never opening.
        //
        // An alias rather than a rename: the enum member NAME is the key the C#-side
        // typedef registration uses (TriggerDispatcher keys on ItemType.ToString()),
        // so renaming would silently unhook those. ItemTypeNumberParityTests compares
        // every one of the reference's 200 hardcoded types against this parser, so a
        // future divergence fails there rather than going unnoticed.
        return normalized.ToUpperInvariant() switch
        {
            "COOKING" => ItemType.CookingTool,
            "CARTOGRAPHY" => ItemType.CartographyTool,
            _ => ItemType.Normal,
        };
    }

    /// <summary>
    /// Apply Source-X's <c>%plural/singular%</c> name template rules to
    /// produce the runtime display name for the given amount. Mirrors
    /// <c>CItemBase::GetNamePluralize</c> in <c>CItemBase.cpp</c>:
    ///   • <c>%</c> toggles "inside" mode and resets to plural section.
    ///   • <c>/</c> inside switches to the singular section.
    ///   • Inside characters are kept only when they belong to the
    ///     side selected by <paramref name="pluralize"/>.
    /// Examples:
    ///   • <c>"Black Pearl%s%"</c> → "Black Pearl" / "Black Pearls"
    ///   • <c>"%shoes/shoe%"</c> → "shoes" / "shoe"
    ///   • <c>"loa%ves/f%"</c> → "loaves" / "loaf"
    /// </summary>
    public static string Pluralize(string? nameTemplate, bool pluralize)
    {
        if (string.IsNullOrEmpty(nameTemplate))
            return string.Empty;
        if (nameTemplate.IndexOf('%') < 0)
            return nameTemplate; // no template, fast path

        var sb = new System.Text.StringBuilder(nameTemplate.Length);
        bool inside = false;
        bool plural = false;
        foreach (char c in nameTemplate)
        {
            if (c == '%')
            {
                inside = !inside;
                plural = true;
                continue;
            }
            if (inside)
            {
                if (c == '/')
                {
                    plural = false;
                    continue;
                }
                if (pluralize ? !plural : plural)
                    continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Convenience overload — picks plural form when amount &gt; 1.</summary>
    public static string Pluralize(string? nameTemplate, int amount)
        => Pluralize(nameTemplate, amount != 1);
}
