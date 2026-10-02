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
    /// <summary>A definition's TAG.x / TAG0.x line (CBaseBaseDef::r_LoadVal,
    /// CBase.cpp:293): the key without its prefix, the value read with GetArgStr's
    /// quote flag into <c>CVarDefMap::SetStr</c> - a quoted value a string var, an
    /// unquoted simple number a number var - and fZero always false ("don't change
    /// fZero to true! it would break some scripts!").</summary>
    internal static void LoadDefinitionTag(Variables.VarMap tags, string key, string value)
    {
        string tagKey = key[(key[3] == '0' ? 5 : 4)..];
        string tagValue = Variables.VarMap.UnquoteSaveValue(value.Trim(), out bool quoted);
        tags.SetStr(tagKey, quoted, tagValue);
    }

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
            case "CAN": Can = (CanFlags)ParseFlags(value); HasCanKey = true; break;
            case "CANUSE": CanUse = (CanEquipFlags)ParseFlags(value); break;
            case "HEIGHT": Height = (byte)Math.Clamp(ParseLeadingInt(value), 0, byte.MaxValue); break;
            case "DUPEITEM": ParseHexOrDec(value, out ushort dup); DupItemId = dup; break;
            case "ID": ParseHexOrDec(value, out ushort id); DispIndex = id; break;
            case "DISPID": ParseHexOrDec(value, out ushort did); DispIndex = did; break;
            case "DEFNAME": DefName = value; break;
            // OBC_DEFNAME2 (CBase.cpp:367-369): a second name for the same definition.
            case "DEFNAME2":
                foreach (string alias in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    if (!Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) Aliases.Add(alias);
                break;
            // OBC_CATEGORY / OBC_SUBSECTION / OBC_DESCRIPTION (CBase.cpp:326-335): kept
            // as the definition's base strings (SetDefStr), not as TAGs - and
            // DESCRIPTION=@ means "the same as SUBSECTION".
            case "CATEGORY":
            case "SUBSECTION":
            case "DESCRIPTION":
                SetDefinitionString(BaseDefs, key, value);
                break;
            // A Sphere 56T custom-version key: the item stays in the hand while its
            // wearer casts. It is the same request Source-X makes with the
            // CAN_I_EQUIPONCAST flag, so it is read into that flag.
            case "CASTNOEQUP":
                ApplyCanFlagKey(value, CanFlags.I_EquipOnCast);
                break;
            case "EVENTS":
            case "TEVENTS":
                ParseEventsList(value);
                break;
            case "SKILLMAKE": SkillMakeRaw = value.Trim(); ParseResourceList(value, SkillMake); break;
            case "RESOURCES": ResourcesRaw = value.Trim(); ParseResourceList(value, BaseResources); break;
            case "RANGE": (RangeMin, RangeMax) = ParseRange(value); break;
            case "RANGEH": int.TryParse(value, out int rh); RangeMax = rh; break;
            case "RANGEL": int.TryParse(value, out int rl); RangeMin = rl; break;
            case "SPEED": Speed = ParseLeadingInt(value); break;
            case "SKILL":
                HasSkill = Enum.TryParse(value, true, out SkillType sk);
                Skill = HasSkill ? sk : SkillType.None;
                break;
            case "REQSTR": ReqStr = ParseLeadingInt(value); break;
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
            // IBC_TWOHANDS (CItemBase.cpp:1748-1754): only an argument starting with
            // 1, Y or y means two hands, and what it does is put the item on the
            // two-handed layer. Anything else - TWOHANDS=N, TWOHANDS=0 - leaves the
            // definition as it was; reading "not 0" took N as two-handed and a
            // one-handed weapon then refused a shield.
            case "TWOHANDS":
            {
                string th = value.TrimStart();
                if (th.Length > 0 && th[0] is '1' or 'Y' or 'y')
                {
                    TwoHands = true;
                    Layer = Layer.TwoHanded;
                }
                break;
            }
            case "TDATA1":
                // A number written here replaces a name an ID= base gave (TDATA3=0).
                TData1Name = !ParseHexOrDecUInt(value, out uint td1) && value.Length > 0 &&
                    (char.IsLetter(value[0]) || value[0] == '_') ? value.Trim() : null;
                TData1 = td1; TDataSetMask |= 1; break;
            case "TDATA2":
                // A number written here replaces a name an ID= base gave (TDATA3=0).
                TData2Name = !ParseHexOrDecUInt(value, out uint td2) && value.Length > 0 &&
                    (char.IsLetter(value[0]) || value[0] == '_') ? value.Trim() : null;
                TData2 = td2; TDataSetMask |= 2; break;
            case "TDATA3":
                // A number written here replaces a name an ID= base gave (TDATA3=0).
                TData3Name = !ParseHexOrDecUInt(value, out uint td3) && value.Length > 0 &&
                    (char.IsLetter(value[0]) || value[0] == '_') ? value.Trim() : null;
                TData3 = td3; TDataSetMask |= 4; break;
            case "TDATA4":
                // A number written here replaces a name an ID= base gave (TDATA3=0).
                TData4Name = !ParseHexOrDecUInt(value, out uint td4) && value.Length > 0 &&
                    (char.IsLetter(value[0]) || value[0] == '_') ? value.Trim() : null;
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
            // IBC_ALTERITEM (CItemBase.cpp:1422): a plain SetDefStr on the definition,
            // quotes dropped, an empty value removing it. Nothing in the engine
            // reads it; scripts read it back through the item (GetDefStr).
            case "ALTERITEM":
            {
                string alter = Parsing.ScriptKey.StripQuotePair(value.Trim());
                if (alter.Length == 0) TagDefs.Remove("ALTERITEM");
                else TagDefs.Set("ALTERITEM", alter);
                break;
            }
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
                if (key.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("TAG0.", StringComparison.OrdinalIgnoreCase))
                {
                    // CBaseBaseDef::r_LoadVal TAG/TAG0 (CBase.cpp:293): SetStr with the
                    // quote flag and fZero always false.
                    LoadDefinitionTag(TagDefs, key, value);
                    TagLineKeys.Add(key[(key[3] == '0' ? 5 : 4)..]);
                    break;
                }
                // Previously dropped with zero visibility — count it so a
                // real pack load can report what it lost.
                TagDefs.Set(key.ToUpperInvariant(), value);
                UnknownKeyDiagnostics.Record("ITEMDEF", key);
                break;
        }
    }

    /// <summary>The keys of <see cref="BaseDef.TagDefs"/> that came from TAG.x / TAG0.x
    /// lines - the definition's real TAGs (m_TagDefs), as opposed to the property and
    /// unrecognised keys this engine also keeps in that map. An item reads these
    /// through its definition and never gets a copy of them.</summary>
    public HashSet<string> TagLineKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when an ID= line copied a base definition into this one
    /// (<see cref="CopyBasicFrom"/>).</summary>
    public bool HasIdBase { get; set; }

    /// <summary>CItemBase::CopyBasic (CItemBase.cpp:181-196) and the CBaseBaseDef half
    /// it ends with (CBase.cpp:399-422): what an <c>ID=&lt;base&gt;</c> line copies from
    /// the base at the moment it is read. Keys written before the ID= line are
    /// overwritten - the name excepted, which is only taken when this definition has
    /// none - and keys written after it override. Value, SKILLMAKE, RESOURCES, TAGs
    /// and TEVENTS are not part of it.</summary>
    public void CopyBasicFrom(ItemDef b)
    {
        // CItemBase::CopyBasic
        Speed = b.Speed;
        Weight = b.Weight;
        HasWeight = b.HasWeight;
        DupeList = b.DupeList;
        _dupeIds = null;
        FlipId = b.FlipId;
        Layer = b.Layer;
        Type = b.Type;
        TypeRaw = b.TypeRaw;
        CanUse = b.CanUse;
        TData1 = b.TData1; TData1Name = b.TData1Name;
        TData2 = b.TData2; TData2Name = b.TData2Name;
        TData3 = b.TData3; TData3Name = b.TData3Name;
        TData4 = b.TData4; TData4Name = b.TData4Name;
        TDataSetMask = 0;
        ReqStr = b.ReqStr;          // m_ttEquippable.m_iStrReq shares TDATA2's slot
        TwoHands = b.TwoHands;

        // CBaseBaseDef::CopyBasic
        if (string.IsNullOrEmpty(Name))
            Name = b.Name;
        DispIndex = b.DispIndex;
        Height = b.Height;
        ResLevel = b.ResLevel;
        ResDispDnHue = b.ResDispDnHue;
        ResDispDnId = b.ResDispDnId;
        ResDispDnIdRaw = b.ResDispDnIdRaw;
        AttackMin = b.AttackMin; AttackMax = b.AttackMax;
        DefenseMin = b.DefenseMin; DefenseMax = b.DefenseMax;
        Can = b.Can;
        HasCanKey = b.HasCanKey;
        Dye = b.Dye; Flip = b.Flip; Repair = b.Repair; Replicate = b.Replicate;
        // CEntityProps::Copy - the property components, which this engine keeps as
        // the definition's property tags.
        CopyPropertyTags(b.TagDefs, TagDefs);
        HasIdBase = true;
    }

    /// <summary>Whether the stub section has ON=@ blocks of its own; when it does those
    /// run for it rather than the master's.</summary>
    public bool DupeHasOwnTriggers { get; set; }

    /// <summary>Make this definition read as the DUPEITEM master it is a stub of.
    /// Upstream a DUPEITEM section is not a definition at all: FindItemBase hands back
    /// the master (MakeDupeReplacement, CItemBase.cpp:1801-1838, 2254-2285), so name,
    /// type, value, RESOURCES, SKILLMAKE, TEVENTS and TAGs are all the master's. What
    /// identifies the graphic stays the stub's: the index, DUPEITEM, the display id and
    /// the tiledata-derived height and flags. <paramref name="ownKeys"/> are the keys
    /// the stub section wrote itself (upper case); those keep the stub's value.</summary>
    public void ShareDupeMaster(ItemDef m, ISet<string>? ownKeys = null)
    {
        // Upstream never reads a stub's other lines. A pack that nevertheless gave a
        // stub a property of its own - a lit lantern's TYPE=t_light_lit on a DUPEITEM
        // of the unlit one - keeps that one property here; everything the stub does
        // not say is the master's.
        bool Own(string key) => ownKeys != null && ownKeys.Contains(key);
        if (!Own("NAME")) Name = m.Name;
        if (!Own("TYPE")) { Type = m.Type; TypeRaw = m.TypeRaw; }
        if (!Own("FLIPID")) FlipId = m.FlipId;
        if (!Own("WEIGHT")) { Weight = m.Weight; HasWeight = m.HasWeight; }
        if (!Own("LAYER") && !Own("TWOHANDS")) { Layer = m.Layer; TwoHands = m.TwoHands; }
        if (!Own("VALUE")) { ValueMin = m.ValueMin; ValueMax = m.ValueMax; }
        if (!Own("CANUSE")) CanUse = m.CanUse;
        if (!Own("SPEED")) Speed = m.Speed;
        if (!Own("SKILL")) { Skill = m.Skill; HasSkill = m.HasSkill; }
        if (!Own("REQSTR")) ReqStr = m.ReqStr;
        if (!Own("HITS") && !Own("MAXHITS") && !Own("HITSMAX")) { HitsMin = m.HitsMin; HitsMax = m.HitsMax; }
        if (!Own("TDATA1")) { TData1 = m.TData1; TData1Name = m.TData1Name; }
        if (!Own("TDATA2")) { TData2 = m.TData2; TData2Name = m.TData2Name; }
        if (!Own("TDATA3")) { TData3 = m.TData3; TData3Name = m.TData3Name; }
        if (!Own("TDATA4")) { TData4 = m.TData4; TData4Name = m.TData4Name; }
        TDataSetMask |= m.TDataSetMask;
        if (!Own("AMMOANIM")) AmmoAnim = m.AmmoAnim;
        if (!Own("AMMOANIMHUE")) AmmoAnimHue = m.AmmoAnimHue;
        if (!Own("AMMOANIMRENDER")) AmmoAnimRender = m.AmmoAnimRender;
        if (!Own("AMMOCONT")) AmmoCont = m.AmmoCont;
        if (!Own("AMMOTYPE")) AmmoType = m.AmmoType;
        if (!Own("RESMAKE")) ResMake = m.ResMake;
        if (!Own("DUPELIST")) { DupeList = m.DupeList; _dupeIds = null; }
        if (!Own("WEIGHTREDUCTION")) WeightReduction = m.WeightReduction;
        if (!Own("SKILLMAKE"))
        {
            SkillMakeRaw = m.SkillMakeRaw;
            SkillMake.Clear(); SkillMake.AddRange(m.SkillMake);
        }
        if (!Own("RESOURCES"))
        {
            ResourcesRaw = m.ResourcesRaw;
            BaseResources.Clear(); BaseResources.AddRange(m.BaseResources);
        }
        if (!Own("CAN") && !Own("DYE") && !Own("FLIP") && !Own("REPAIR") && !Own("REPLICATE") &&
            !Own("ENCHANT") && !Own("EXCEPTIONAL") && !Own("IMBUE") && !Own("REFORGE") &&
            !Own("RETAINCOLOR") && !Own("MAKERSMARK") && !Own("RECYCLE") && !Own("CASTNOEQUP"))
        {
            Can = m.Can;
            Dye = m.Dye; Flip = m.Flip; Repair = m.Repair; Replicate = m.Replicate;
        }
        if (!Own("DAM")) { AttackMin = m.AttackMin; AttackMax = m.AttackMax; }
        if (!Own("ARMOR")) { DefenseMin = m.DefenseMin; DefenseMax = m.DefenseMax; }
        if (!Own("RANGE") && !Own("RANGEH") && !Own("RANGEL")) { RangeMin = m.RangeMin; RangeMax = m.RangeMax; }
        if (!Own("RESLEVEL")) ResLevel = m.ResLevel;
        if (!Own("RESDISPDNHUE")) ResDispDnHue = m.ResDispDnHue;
        if (!Own("RESDISPDNID")) { ResDispDnId = m.ResDispDnId; ResDispDnIdRaw = m.ResDispDnIdRaw; }
        if (!Own("TEVENTS") && !Own("EVENTS"))
        {
            Events.Clear(); Events.AddRange(m.Events);
            EventNamesRaw.Clear(); EventNamesRaw.AddRange(m.EventNamesRaw);
        }
        // TAGs and base strings: the master's, with any the stub wrote on top.
        var ownTags = new Variables.VarMap();
        ownTags.CopyFrom(TagDefs);
        TagDefs.Clear();
        TagDefs.CopyFrom(m.TagDefs);
        TagDefs.CopyFrom(ownTags);
        TagLineKeys.UnionWith(m.TagLineKeys);
        var ownBase = new Variables.VarMap();
        ownBase.CopyFrom(BaseDefs);
        BaseDefs.Clear();
        BaseDefs.CopyFrom(m.BaseDefs);
        BaseDefs.CopyFrom(ownBase);
        DupeMasterIndex = m.Id.Index;
    }

    /// <summary>The DUPEITEM master this definition shares (<see cref="ShareDupeMaster"/>),
    /// 0 when it is a definition of its own.</summary>
    public int DupeMasterIndex { get; set; }

    /// <summary>The definition keys this engine keeps as property tags (the CCProps*
    /// components of CEntityProps).</summary>
    internal static void CopyPropertyTags(Variables.VarMap from, Variables.VarMap to)
    {
        var copy = new Variables.VarMap();
        copy.CopyFrom(from);
        foreach (var (k, _) in from.GetAll())
            if (!IsPropertyTagKey(k))
                copy.Remove(k);
        to.CopyFrom(copy);
    }

    private static bool IsPropertyTagKey(string key) =>
        key.Equals("SLAYER_GROUP", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("SLAYER_SPECIES", StringComparison.OrdinalIgnoreCase) ||
        AosOnHitProperties.Contains(key) || AosEquipProperties.Contains(key) ||
        EquipmentStatBonuses.Contains(key) || SpellCastingProperties.Contains(key) ||
        key.Equals(CombatSpeedProperties.IncreaseSwingSpeed, StringComparison.OrdinalIgnoreCase);

    /// <summary>A CAN= line was read. It REPLACES the flags the definition took from
    /// the tiledata (OBC_CAN, CBase.cpp:363), so the walk check reads its movement
    /// bits instead of the tiledata's.</summary>
    public bool HasCanKey { get; set; }

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

        // IBC_WEIGHT (CItemBase.cpp:1767-1774): weight is kept in tenths of a stone.
        // A value written with a '.' is read as it stands - the expression reader
        // skips the dot, so 1.0 is 10, 0.2 is 2 and .1 is 1 - and a value without
        // one is whole stones, multiplied by WEIGHT_UNITS.
        bool fDecimal = trimmed.Contains('.');
        if (!SphereNet.Core.Types.ScriptNumber.TryParseLeadingNumber(trimmed, out long parsed))
            return 0;
        if (!fDecimal)
            parsed *= 10;
        return (int)Math.Clamp(parsed, 0, int.MaxValue);
    }

    /// <summary>A numeric definition value as the reference's GetArgVal reads it - a
    /// leading Sphere number (<see cref="SphereNet.Core.Types.ScriptNumber.TryParseLeadingNumber"/>),
    /// 0 when the value does not start with one.</summary>
    internal static int ParseLeadingInt(string value) =>
        SphereNet.Core.Types.ScriptNumber.TryParseLeadingNumber(value, out long n)
            ? (int)Math.Clamp(n, int.MinValue, int.MaxValue)
            : 0;

    /// <summary>CBaseBaseDef's CATEGORY / SUBSECTION / DESCRIPTION (CBase.cpp:326-335):
    /// stored with SetDefStr, quotes dropped, and a DESCRIPTION of "@" replaced by the
    /// SUBSECTION.</summary>
    internal static void SetDefinitionString(Variables.VarMap baseDefs, string key, string value)
    {
        string upper = key.Trim().ToUpperInvariant();
        string text = Parsing.ScriptKey.StripQuotePair(value.Trim());
        baseDefs.Set(upper, text);
        if (string.Equals(baseDefs.Get("DESCRIPTION"), "@", StringComparison.Ordinal))
            baseDefs.Set("DESCRIPTION", baseDefs.Get("SUBSECTION") ?? "");
    }

    /// <summary>DEFNAME2 names this definition also answers to (OBC_DEFNAME2).</summary>
    public List<string> Aliases { get; } = [];

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

        // Str_ParseCmds over "=, \t" (CBase.cpp:345, CExpression.h:329), each part a
        // number as the expression reader takes one: "13,15", "13 15" and "1.5,3"
        // (15,30) are all ranges, and "190,95" starts with 190.
        var pieces = value.Split([',', ' ', '\t', '='],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length == 0)
            return (0, 0);
        int first = ParseLeadingInt(pieces[0]);
        if (pieces.Length == 1)
            return (first, first);
        return (first, ParseLeadingInt(pieces[1]));
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
        // Sphere's decimal with a dot: Exp_GetVal skips the dot, so an ingot's
        // TDATA1=20.0 is 200 - the tenths a skill value is kept in. Read as nothing
        // before, which left every ingot's smelting minimum and range at zero.
        string trimmed = value.Trim();
        if (trimmed.Length > 0 && trimmed.Contains('.') && char.IsAsciiDigit(trimmed[0]) &&
            trimmed.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            ulong dec = 0;
            foreach (char c in trimmed)
            {
                if (c == '.') continue;
                dec = dec * 10 + (uint)(c - '0');
                if (dec > uint.MaxValue) return false;
            }
            result = (uint)dec;
            return true;
        }
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result))
            return true;
        if (value.StartsWith("0", StringComparison.OrdinalIgnoreCase) && value.Length > 1 &&
            uint.TryParse(value.AsSpan(), System.Globalization.NumberStyles.HexNumber, null, out result))
            return true;
        if (uint.TryParse(value, out result))
            return true;
        // Anything else that still STARTS with a number is read up to where the number
        // ends, as GetArgDWVal does: TDATA3=190,95 is 190.
        if (trimmed.Length > 0 && (char.IsAsciiDigit(trimmed[0]) || trimmed[0] == '.') &&
            SphereNet.Core.Types.ScriptNumber.TryParseLeadingNumber(trimmed, out long leading))
        {
            result = unchecked((uint)leading);
            return true;
        }
        result = 0;
        return false;
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
        switch (normalized.ToUpperInvariant())
        {
            case "COOKING": return ItemType.CookingTool;
            case "CARTOGRAPHY": return ItemType.CartographyTool;
        }

        // A name the engine does not know, numbered by the pack's own [TYPEDEFS]
        // block: upstream reads TYPE through the typedef table (ResourceGetIndexType,
        // CItemBase.cpp:1756), so a 0.56-numbered pack's "t_chair 40" makes the item
        // type 40 rather than a plain item.
        if (TypeNumberResolver?.Invoke(value.Trim()) is int number && number is > 0 and <= ushort.MaxValue)
            return (ItemType)number;
        return ItemType.Normal;
    }

    /// <summary>Resolves a type name to the number a [TYPEDEFS] block gave it. Wired by
    /// the ResourceHolder.</summary>
    public static Func<string, int?>? TypeNumberResolver { get; set; }

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
