using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Definitions;

/// <summary>CHARDEF defname ↔ body graphic resolution and apply helpers.</summary>
public static class CharDefHelper
{
    public static int ResolveDefIndex(string? defname, ResourceHolder? resources)
    {
        if (string.IsNullOrWhiteSpace(defname) || resources == null)
            return 0;

        var rid = resources.ResolveDefName(defname.Trim());
        return rid.IsValid && rid.Type == ResType.CharDef ? rid.Index : 0;
    }

    public static string? ResolveDefName(int charDefIndex)
    {
        if (charDefIndex == 0)
            return null;
        return DefinitionLoader.GetCharDef(charDefIndex)?.DefName;
    }

    public static ushort ResolveBodyId(int charDefIndex, ResourceHolder? resources = null)
    {
        if (charDefIndex == 0)
            return 0;

        var def = DefinitionLoader.GetCharDef(charDefIndex);
        if (def == null)
            return 0;

        return ResolveBodyId(def, charDefIndex, resources ?? DefinitionLoader.StaticResources);
    }

    public static ushort ResolveBodyId(CharDef charDef, int charDefIndex, ResourceHolder? resources) =>
        ResolveBodyId(charDef, charDefIndex, resources, charDefIndex);

    private static ushort ResolveBodyId(CharDef charDef, int charDefIndex, ResourceHolder? resources, int originIndex)
    {
        if (charDef.DispIndex > 0)
            return charDef.DispIndex;

        string alias = charDef.DisplayIdRef?.Trim() ?? "";
        if (alias.Length > 0 && resources != null)
        {
            var refRid = resources.ResolveDefName(alias);
            if (refRid.IsValid && refRid.Type == ResType.CharDef &&
                refRid.Index != charDefIndex && refRid.Index != originIndex)
            {
                var refDef = DefinitionLoader.GetCharDef(refRid.Index);
                if (refDef != null)
                    return ResolveBodyId(refDef, refRid.Index, resources, originIndex);
            }
        }

        return IsNumericCharDefBody(charDef, charDefIndex)
            ? (ushort)charDefIndex
            : (ushort)0;
    }

    /// <summary>Save files sometimes store the CHARDEF hash in BODY.</summary>
    public static bool BodyMatchesDefHash(Character ch) =>
        ch.CharDefIndex != 0 && ch.CharDefIndex > ushort.MaxValue && ch.BodyId == (ushort)ch.CharDefIndex;

    private static bool IsNumericCharDefBody(CharDef charDef, int charDefIndex)
    {
        if (charDefIndex <= 0 || charDefIndex > ushort.MaxValue)
            return false;

        string header = charDef.HeaderArgument?.Trim() ?? "";
        if (header.Length == 0)
            return charDefIndex <= 0x03E7;

        string first = header.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return TryParseHexOrDec(first, out int parsed) && parsed == charDefIndex;
    }

    private static bool TryParseHexOrDec(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        else if (text.Length > 1 && text[0] == '0')
            text = text[1..];

        return int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    /// <summary>Resolve CHARDEF tag / index to a display body and fix saves
    /// that stored the def hash (e.g. 0x03DB) in BODY.</summary>
    public static bool EnsureDisplayBody(Character ch, ResourceHolder? resources = null)
    {
        resources ??= DefinitionLoader.StaticResources;
        bool corrupt = BodyMatchesDefHash(ch);

        if (ch.TryGetTag("CHARDEF", out string? tag) && !string.IsNullOrWhiteSpace(tag) &&
            TryApplyDefName(ch, tag, resources, refresh: false))
            return true;

        string? defname = ResolveDefName(ch.CharDefIndex);
        if (!string.IsNullOrEmpty(defname) &&
            TryApplyDefName(ch, defname, resources, refresh: false))
            return true;

        ushort resolved = ResolveBodyId(ch.CharDefIndex, resources);
        if (resolved != 0 && (corrupt || resolved != ch.BodyId))
        {
            ch.BodyId = resolved;
            ch.BaseId = resolved;
            if (ch.OBody == 0 || BodyMatchesDefHash(ch))
                ch.OBody = resolved;
            return true;
        }

        return false;
    }

    public static CanFlags GetCanFlags(Character ch)
    {
        var def = DefinitionLoader.GetCharDef(ch.CharDefIndex);
        // Unresolved definitions retain the legacy engine defaults. A loaded
        // definition with explicit CAN=0 must remain incapable of movement.
        var baseCan = def?.Can ?? (CanFlags.C_Walk | CanFlags.C_Run | CanFlags.C_UseHands | CanFlags.C_Equip);
        return (CanFlags)((ulong)baseCan ^ ch.CanMask);
    }

    public static bool CanPassWalls(Character ch)
    {
        if (ch.IsStatFlag(StatFlag.Insubstantial))
            return true;
        if (ch.PrivLevel >= PrivLevel.GM && ch.AllMove)
            return true;

        string? defname = ResolveDefName(ch.CharDefIndex);
        if (ch.PrivLevel >= PrivLevel.GM &&
            !string.IsNullOrEmpty(defname) &&
            defname.EndsWith("_gm", StringComparison.OrdinalIgnoreCase))
            return true;

        if ((GetCanFlags(ch) & CanFlags.C_PassWalls) != 0)
            return true;

        return false;
    }

    public static bool CanPassDoors(Character ch) => ch.IsDead ||
        (GetCanFlags(ch) & CanFlags.C_Ghost) != 0;

    public static bool IsMountCapable(Character ch) => !ch.IsDead &&
        (ch.IsHuman || ch.BodyId is 0x025D or 0x025E || (GetCanFlags(ch) & CanFlags.C_Mount) != 0);

    /// <summary>Fresh-character initialization only; never BODY changes or save/login repair.</summary>
    public static Action<Character>? AfterApplyDefName;

    /// <summary>Apply a CHARDEF defname (e.g. c_man, c_man_gm) to a character.</summary>
    public static bool TryApplyDefName(Character ch, string? defname, ResourceHolder? resources,
        bool stats = false, bool refresh = true, bool fireCreate = false)
    {
        if (string.IsNullOrWhiteSpace(defname) || resources == null)
            return false;

        int defIndex = ResolveDefIndex(defname, resources);
        if (defIndex == 0)
            return false;

        var def = DefinitionLoader.GetCharDef(defIndex);
        ushort bodyId = def != null
            ? ResolveBodyId(def, defIndex, resources)
            : ResolveBodyId(defIndex, resources);
        if (bodyId == 0)
            return false;

        ch.CharDefIndex = defIndex;
        ch.SetTag("CHARDEF", defname.Trim());
        ch.BodyId = bodyId;
        ch.BaseId = bodyId;
        ch.OBody = bodyId;

        if (ch.IsPlayer)
            ch.NpcBrain = NpcBrainType.None;
        else if (def != null && def.NpcBrain != NpcBrainType.None)
            ch.NpcBrain = def.NpcBrain;

        if (stats && def != null)
        {
            // Roll each stat within its [min,max] range (Source-X randomizes on
            // spawn) instead of always taking the max.
            int strVal = RollStat(def.StrMin, def.StrMax);
            int dexVal = RollStat(def.DexMin, def.DexMax);
            int intVal = RollStat(def.IntMin, def.IntMax);
            ch.Str = (short)Math.Clamp(strVal, 1, short.MaxValue);
            ch.Dex = (short)Math.Clamp(dexVal, 1, short.MaxValue);
            ch.Int = (short)Math.Clamp(intVal, 1, short.MaxValue);
            int hits = def.HitsMax > 0 || def.HitsMin > 0
                ? RollStat(def.HitsMin, def.HitsMax)
                : Math.Max(1, strVal);
            ch.MaxHits = (short)Math.Clamp(hits, 1, short.MaxValue);
            if (ch.Hits > ch.MaxHits)
                ch.Hits = ch.MaxHits;
            // Fill the stamina pool with the rolled DEX. The Dex setter raises
            // MaxStam but never the current pool, and swing speed reads that pool in
            // eras 1-4 (Source-X Stat_GetVal(STAT_DEX)) - a creature spawned at 0
            // stamina would swing at the slowest rate the formula allows.
            if (ch.Stam <= 0)
                ch.Stam = ch.MaxStam;
        }

        if (def != null && !ch.IsPlayer)
        {
            ApplyNpcDefinitionSkills(ch, def);
            ApplyNpcDefinitionTags(ch, def);
            ApplyNpcDefinitionProperties(ch, def);
        }

        // Source-X SetID does not run @Create. Only fresh NPC creation does;
        // replaying it during BODY assignment can recursively change BODY again.
        if (fireCreate)
            AfterApplyDefName?.Invoke(ch);
        if (refresh)
            ch.RefreshAppearance();
        return true;
    }

    /// <summary>Roll a stat within its [min,max] range. Falls back to whichever
    /// bound is set when the other is missing.</summary>
    private static int RollStat(int min, int max)
    {
        if (max <= 0 && min <= 0) return 1;
        if (max <= 0) return Math.Max(1, min);
        if (min <= 0) min = max;
        if (min > max) (min, max) = (max, min);
        return min == max ? min : Random.Shared.Next(min, max + 1);
    }

    public static void ApplyNpcDefinitionSkills(Character ch, CharDef def)
    {
        if (ch.IsPlayer || def.SkillRanges.Count == 0)
            return;

        foreach (var (skill, range) in def.SkillRanges)
        {
            int value = RollStat(range.Min, range.Max);
            if (value <= 0)
                continue;
            ch.SetSkill(skill, (ushort)Math.Clamp(value, 0, ushort.MaxValue));
        }
    }

    public static void ApplyNpcDefinitionTags(Character ch, CharDef def)
    {
        if (ch.IsPlayer)
            return;

        foreach (var (key, value) in def.TagDefs.GetAll())
            ch.SetTag(key, value);
    }

    private static void ApplyNpcDefinitionProperties(Character ch, CharDef def)
    {
        if (def.BaseColor != 0) ch.TrySetProperty("COLOR", $"0{def.BaseColor:X}");
        if (def.ResPhysical != 0) ch.TrySetProperty("RESPHYSICAL", def.ResPhysical.ToString());
        if (def.ResFire != 0) ch.TrySetProperty("RESFIRE", def.ResFire.ToString());
        if (def.ResCold != 0) ch.TrySetProperty("RESCOLD", def.ResCold.ToString());
        if (def.ResPoison != 0) ch.TrySetProperty("RESPOISON", def.ResPoison.ToString());
        if (def.ResEnergy != 0) ch.TrySetProperty("RESENERGY", def.ResEnergy.ToString());
        if (def.ResPhysicalMax != 0) ch.TrySetProperty("RESPHYSICALMAX", def.ResPhysicalMax.ToString());
        if (def.ResFireMax != 0) ch.TrySetProperty("RESFIREMAX", def.ResFireMax.ToString());
        if (def.ResColdMax != 0) ch.TrySetProperty("RESCOLDMAX", def.ResColdMax.ToString());
        if (def.ResPoisonMax != 0) ch.TrySetProperty("RESPOISONMAX", def.ResPoisonMax.ToString());
        if (def.ResEnergyMax != 0) ch.TrySetProperty("RESENERGYMAX", def.ResEnergyMax.ToString());
        if (def.ReflectPhysicalDam != 0) ch.SetTag("REFLECTPHYSICALDAM", def.ReflectPhysicalDam.ToString());
        if (def.EraLimitGear != 0) ch.SetTag("ERALIMITGEAR", def.EraLimitGear.ToString());
        if (def.EraLimitLoot != 0) ch.SetTag("ERALIMITLOOT", def.EraLimitLoot.ToString());
        if (def.EraLimitProps != 0) ch.SetTag("ERALIMITPROPS", def.EraLimitProps.ToString());
        if (!string.IsNullOrWhiteSpace(def.FoodTypeRaw)) ch.SetTag("FOODTYPE", def.FoodTypeRaw);

        ApplyCombatProperties(ch, def);
    }

    /// <summary>The elemental share of the damage this creature deals.
    ///
    /// An unset DAMPHYSICAL is the remainder the elemental percents leave of 100
    /// (Source-X OnTakeDamage), NOT the 100 a pure-physical creature keeps - a drake
    /// declaring DAMFIRE=20 deals 80 physical and 20 fire, not 100 and 20.
    ///
    /// This used to be written out twice, in the spawner and in the client's NPC
    /// creation, and NOT in the shared helper both of them call for the rest of the
    /// definition. So a creature born any other way - a GM .add, a script NEWNPC,
    /// anything reaching TryApplyDefName - dealt pure physical damage however much
    /// elemental its chardef declared. The shipped packs declare it on about forty
    /// creatures per element.</summary>
    public static void ApplyCombatProperties(Character ch, CharDef def)
    {
        // The creature's own damage, copied off the chardef the way upstream copies it
        // (CChar.cpp:313), so a script can arm ONE creature differently from the rest
        // of its kind and that stays with it.
        //
        // It lives HERE, next to the split, because this is the one call every path
        // that makes a creature already shares: the spawner and the client's NPC
        // creation reach it directly rather than through the full definition helper,
        // which is exactly how the split came to be missing from two of three paths.
        if (def.AttackMin != 0 || def.AttackMax != 0)
            ch.SetAttackRating(def.AttackMin, def.AttackMax);

        bool anyElemental = def.DamFire != 0 || def.DamCold != 0 ||
                            def.DamPoison != 0 || def.DamEnergy != 0;
        if (def.DamPhysical != 0)
            ch.DamPhysical = def.DamPhysical;
        else if (anyElemental)
            ch.DamPhysical = (short)Math.Max(0,
                100 - def.DamFire - def.DamCold - def.DamPoison - def.DamEnergy);

        if (def.DamFire != 0) ch.DamFire = def.DamFire;
        if (def.DamCold != 0) ch.DamCold = def.DamCold;
        if (def.DamPoison != 0) ch.DamPoison = def.DamPoison;
        if (def.DamEnergy != 0) ch.DamEnergy = def.DamEnergy;
    }
}
