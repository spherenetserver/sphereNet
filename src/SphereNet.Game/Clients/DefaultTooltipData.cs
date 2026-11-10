using System.Globalization;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;

namespace SphereNet.Game.Clients;

/// <summary>
/// The built-in lines of an object's property list, in the order upstream pushes them:
/// the character name line and its default data (AOSTooltip_addName /
/// AOSTooltip_addDefaultCharData, CClientMsg_AOSTooltip.cpp:253-352) and an item's
/// default data after the attribute lines (AOSTooltip_addDefaultItemData, :384-707).
///
/// Not ported: the "guarding" line (no NPCACT_GUARD_TARG action to read), the spawn
/// item read-outs and the guild stone's own list.
/// </summary>
internal static class DefaultTooltipData
{
    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>GetDefNum(key, true): the object's own value, else its definition's.</summary>
    private static long DefNum(Item item, string key)
    {
        if (item.TryGetTag(key, out string? own) && ScriptNumber.TryParseToken(own, out long v))
            return v;
        string? baseValue = DefinitionLoader.GetItemDef(item.BaseId)?.TagDefs.Get(key);
        return ScriptNumber.TryParseToken(baseValue, out long d) ? d : 0;
    }

    private static string Tag(Character ch, string key) =>
        ch.TryGetTag(key, out string? v) && v != null ? v : "";

    // ---------------------------------------------------------------- characters

    /// <summary>The name line, 1050045 "~1_PREFIX~~2_NAME~~3_SUFFIX~", and the guild
    /// line (CClientMsg_AOSTooltip.cpp:253-325).
    ///
    /// Upstream inserts the guild line at the FRONT of the list, ahead of the name; the
    /// client takes the first entry for the object's name, so it is appended after the
    /// name here.</summary>
    public static void AppendCharName(Character ch, PrivLevel viewerPriv,
        List<(uint ClilocId, string Args)> props)
    {
        long nameLoc = 0;
        if (ch.TryGetTag("NAMELOC", out string? locRaw))
            ScriptNumber.TryParseToken(locRaw, out nameLoc);
        string hue = Tag(ch, "NAMELOC.HUE");

        // NAME.PREFIX, else the fame title, else a blank (:256-262).
        string prefix = Tag(ch, "NAME.PREFIX");
        if (prefix.Length == 0)
            prefix = PaperdollText.GetFameTitle(ch);
        if (prefix.Length == 0)
            prefix = " ";

        // NAME.SUFFIX, then the guild abbreviation of a member who shows it (:264-283).
        string suffix = Tag(ch, "NAME.SUFFIX");
        bool showGuild = !ch.IsStatFlag(StatFlag.Incognito) || viewerPriv > ch.PrivLevel;
        var member = PaperdollText.FindGuildMember(ch, out string abbrev, out string guildName);
        if (member != null && showGuild && member.ShowAbbrev && abbrev.Length > 0)
            suffix += $" [{abbrev}]";
        if (suffix.Length == 0)
            suffix = " ";

        string name = nameLoc != 0 ? Num(nameLoc) : ch.GetName();
        props.Add((1050045, hue.Length > 0
            ? $"<basefont color=\"#{hue}\"> {prefix}\t{name}\t{suffix} "
            : $"{prefix}\t{name}\t{suffix}"));

        // :311-324
        if (showGuild && member != null && member.ShowAbbrev)
        {
            if (member.Title.Length > 0)
                props.Add((1060776, $"{member.Title}\t{guildName}")); // ~1_val~, ~2_val~
            else
                props.Add((1070722, guildName)); // ~1_NOTHING~
        }
    }

    /// <summary>AOSTooltip_addDefaultCharData (CClientMsg_AOSTooltip.cpp:328-352).</summary>
    public static void AppendCharData(Character ch, bool featureMl,
        List<(uint ClilocId, string Args)> props)
    {
        if (ch.IsPlayer)
        {
            if (ch.IsGmMode && ch.PrivShow)
                props.Add((1018085, "")); // Game Master
            return;
        }

        if (featureMl)
        {
            // Pack llama, pack horse, giant beetle (:340-346).
            if (ch.BodyId is 0x0124 or 0x0123 or 0x0317)
            {
                int weight = ch.GetTotalWeight();
                props.Add((weight == 1 ? 1072788u : 1072789u, Num(weight))); // Weight: ~1_WEIGHT~ stone(s)
            }
        }

        if (ch.IsStatFlag(StatFlag.Conjured))
            props.Add((1049646, "")); // (summoned)
        else if (ch.IsStatFlag(StatFlag.Pet))
            props.Add((ch.IsBonded ? 1049608u : 502006u, "")); // (bonded) / (tame)
    }

    // --------------------------------------------------------------------- items

    /// <summary>CItem::IsMovable (CItem.cpp:726-735): anything carried counts, unless
    /// it is pinned in place.</summary>
    private static bool IsMovable(Item item)
    {
        if (item.ContainedIn.IsValid && !item.IsAttr(ObjAttributes.Move_Never) &&
            !item.IsAttr(ObjAttributes.LockedDown))
            return true;
        return item.IsMovableType;
    }

    private static bool IsArmorType(ItemType t) => t is
        ItemType.Armor or ItemType.ArmorBone or ItemType.ArmorChain or ItemType.ArmorLeather or
        ItemType.ArmorRing or ItemType.Clothing or ItemType.Shield;

    /// <summary>Everything AOSTooltip_addDefaultItemData adds after the attribute and
    /// race lines (CClientMsg_AOSTooltip.cpp:396-707).</summary>
    /// <param name="viewerIsGm">The requesting client is a GM (telepad / moongate
    /// destination, :604).</param>
    /// <param name="isMultiAt">Whether a point lies inside a multi region (the rune's
    /// "(House)" variants, :623); null when the caller cannot tell.</param>
    public static void AppendItemData(Item item, GameWorld? world, bool featureMl,
        bool elementalEngine, bool viewerIsGm, Func<Point3D, bool>? isMultiAt,
        List<(uint ClilocId, string Args)> props)
    {
        // :396-405
        if (featureMl && IsMovable(item))
        {
            int weight = item.TotalWeight;
            props.Add((weight == 1 ? 1072788u : 1072789u, Num(weight))); // Weight: ~1_WEIGHT~ stone(s)
        }

        // :407-412
        var craftsman = item.Crafter.IsValid ? world?.FindChar(item.Crafter) : null;
        if (craftsman != null)
            props.Add((1050043, craftsman.GetName())); // crafted by ~1_NAME~

        // :414-415
        if (item.IsAttr(ObjAttributes.Exceptional))
            props.Add((1060636, "")); // exceptional

        // :417-422
        long rarity = DefNum(item, "RARITY");
        if (rarity > 0)
            props.Add((1061078, Num(rarity))); // artifact rarity ~1_val~

        // :424-429
        long uses = item.UsesRemaining != 0 ? item.UsesRemaining : DefNum(item, "USESCUR");
        if (uses > 0)
            props.Add((1060584, Num(uses))); // uses remaining: ~1_val~

        // :431-439 (IsTypeArmorWeapon counts the wand as a weapon)
        bool armor = IsArmorType(item.ItemType);
        bool weapon = InfoSkillExtensions.IsWeapon(item.ItemType);
        if (armor || weapon || item.ItemType == ItemType.Wand)
        {
            long selfRepair = DefNum(item, "SELFREPAIR");
            if (selfRepair != 0)
                props.Add((1060450, Num(selfRepair))); // self repair ~1_val~
        }

        var def = DefinitionLoader.GetItemDef(item.BaseId);

        if (item.ItemType == ItemType.ContainerLocked)
        {
            props.Add((3005142, "")); // Locked
        }
        else if (item.ItemType is ItemType.Container or ItemType.TrashCan)
        {
            // :451-473. MODMAXWEIGHT and the pack allowance are kept in stones here.
            if (featureMl)
            {
                bool isPack = item.ContainedIn.IsChar && item.ResolveEquipLayer() == Layer.Pack;
                int maxWeight = (isPack ? Item.BackpackOverload : 0) + item.ModMaxWeight;
                if (maxWeight > 0)
                    props.Add((1072241, // Contents: ~1_COUNT~/~2_MAXCOUNT~ items, ~3_WEIGHT~/~4_MAXWEIGHT~ stones
                        $"{item.ContentCount}\t{Item.MaxContainerItems}\t{item.TotalWeight}\t{maxWeight}"));
                else
                    props.Add((1073841, // Contents: ~1_COUNT~/~2_MAXCOUNT~ items, ~3_WEIGHT~ stones
                        $"{item.ContentCount}\t{Item.MaxContainerItems}\t{item.TotalWeight}"));
            }
            else
                props.Add((1050044, $"{item.ContentCount}\t{item.TotalWeight}")); // ~1_COUNT~ items, ~2_WEIGHT~ stones
        }
        else if (armor)
        {
            // :484-518
            if (!elementalEngine)
            {
                int rating = item.GetArmorDefense();
                if (rating != 0)
                    props.Add((1060658, // ~1_val~: ~2_val~
                        $"{ServerMessages.Get(Msg.TooltipTagArmor)}\t{rating}"));
            }

            long strReq = item.ReqStr - DefNum(item, "LOWERREQ");
            if (strReq > 0)
                props.Add((1061170, Num(strReq))); // strength requirement ~1_val~

            if (item.HitsMax > 0)
                props.Add((1060639, $"{item.HitsCur}\t{item.HitsMax}")); // durability ~1_val~ / ~2_val~
        }
        else if (weapon)
        {
            // :533-577
            if (item.GetPoisonSkill() != 0)
                props.Add((1017383, "")); // poisoned

            props.Add((1061168, // weapon damage ~1_val~ - ~2_val~
                $"{item.AttackLo + item.ModAr}\t{item.GetWeaponAttack()}"));
            props.Add((1061167, Num((byte)item.Speed))); // weapon speed ~1_val~

            int range = def?.RangeMax ?? 0;
            if (range > 1)
                props.Add((1061169, Num(range))); // range ~1_val~

            long strReq = item.ReqStr - DefNum(item, "LOWERREQ");
            if (strReq > 0)
                props.Add((1061170, Num(strReq))); // strength requirement ~1_val~

            props.Add(((def?.Layer ?? Layer.None) == Layer.TwoHanded ? 1061171u : 1061824u, "")); // two-handed / one-handed weapon

            if (DefNum(item, "USEBESTWEAPONSKILL") == 0 &&
                item.TryGetProperty("SKILL", out string skillRaw) &&
                ScriptNumber.TryParseInt(skillRaw, out int skill))
            {
                uint skillLine = (SkillType)skill switch
                {
                    SkillType.Swordsmanship => 1061172, // skill required: swordsmanship
                    SkillType.MaceFighting => 1061173,  // skill required: mace fighting
                    SkillType.Fencing => 1061174,       // skill required: fencing
                    SkillType.Archery => 1061175,       // skill required: archery
                    SkillType.Throwing => 1112075,      // skill required: throwing
                    _ => 0,
                };
                if (skillLine != 0)
                    props.Add((skillLine, ""));
            }

            if (item.HitsMax > 0)
                props.Add((1060639, $"{item.HitsCur}\t{item.HitsMax}")); // durability ~1_val~ / ~2_val~
        }
        else if (item.ItemType == ItemType.Wand)
        {
            // :580-583 (m_itWeapon.m_spellcharges is MORE2)
            props.Add((1054132, Num(item.More2))); // [charges: ~1_charges~]
        }
        else if (item.ItemType is ItemType.Telepad or ItemType.Moongate)
        {
            // :585-592
            if (viewerIsGm)
                props.Add((1060658, // ~1_val~: ~2_val~
                    $"{ServerMessages.Get(Msg.TooltipTagDestination)}\t{item.MoreP}"));
        }
        else if (item.ItemType == ItemType.Rune)
        {
            // :594-620
            if (item.TryGetRuneMark(out Point3D mark))
            {
                string regionName = world?.FindRegion(mark)?.Name is { Length: > 0 } found
                    ? found
                    : ServerMessages.Get(Msg.RuneLocationUnk);
                bool multi = isMultiAt?.Invoke(mark) == true;
                uint cliloc = mark.Map switch
                {
                    0 => multi ? 1062452u : 1060805u, // ~1_val~ (Felucca)[(House)]
                    1 => multi ? 1062453u : 1060806u, // ~1_val~ (Trammel)[(House)]
                    3 => multi ? 1062454u : 1060804u, // ~1_val~ (Malas)[(House)]
                    4 => multi ? 1063260u : 1063259u, // ~1_val~ (Tokuno Islands)[(House)]
                    5 => multi ? 1113206u : 1113205u, // ~1_val~ (Ter Mur)[(House)]
                    _ => 1042971u,                    // ~1_NOTHING~
                };
                props.Add((cliloc, $"{ServerMessages.Get(Msg.RuneTo)} {regionName}"));
            }
        }
        else if (item.IsSpellbook)
        {
            // :622-639 - GetSpellcountInBook: the set bits within the book's capacity.
            ulong mask = ((ulong)item.More2 << 32) | item.More1;
            uint capacity = Math.Min(64u, item.SpellbookSpellCount);
            if (capacity < 64)
                mask &= (1UL << (int)capacity) - 1;
            int count = System.Numerics.BitOperations.PopCount(mask);
            if (count > 0)
                props.Add((1042886, Num(count))); // ~1_NUMBERS_OF_SPELLS~ Spells
        }
        else if (item.ItemType == ItemType.CommCrystal)
        {
            // :685-690
            var link = item.Link.IsValid ? world?.FindObject(item.Link) as Item : null;
            bool active = link != null && link.ItemType == ItemType.CommCrystal;
            props.Add((active ? 1060742u : 1060743u, "")); // active / inactive
            props.Add((1060745u, "")); // broadcast
        }
    }
}
