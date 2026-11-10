using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Core.Types;

namespace SphereNet.Game.Skills.Information;

/// <summary>
/// Helpers used by <see cref="InfoSkillEngine"/> that wrap small Source-X CChar /
/// CItem accessors not (yet) present on SphereNet's domain types. Centralised here
/// so the engine itself stays a literal port of CClientTarg.cpp and can be diff'd
/// 1:1 against upstream.
/// </summary>
internal static class InfoSkillExtensions
{
    // ---- Character: pronouns (Source-X CChar::GetPronoun / GetPossessPronoun) ----

    /// <summary>True if the character body is a human female (0x0191) or female ghost (0x0193).</summary>
    public static bool IsFemaleBody(this Character ch) =>
        ch.BodyId is 0x0191 or 0x0193;

    /// <summary>0 = it, 1 = he, 2 = she. The pronoun is picked by the body shown and
    /// nothing else - man / woman, their ghosts, and the elf and gargoyle pairs
    /// (CChar::GetPronoun, CCharStatus.cpp:611-633). It used to go by the NPC brain,
    /// so a human-bodied monster-brained NPC was "it" and any human-brained creature
    /// "he".</summary>
    private static int PronounGender(ushort body) => body switch
    {
        0x0190 or 0x0192 or 0x025D or 0x025F or 0x029A or 0x02B6 => 1,
        0x0191 or 0x0193 or 0x025E or 0x0260 or 0x029B or 0x02B7 => 2,
        _ => 0,
    };

    /// <summary>"he" / "she" / "it" (CChar::GetPronoun, CCharStatus.cpp:611-633).</summary>
    public static string GetPronoun(this Character ch) => ServerMessages.Get(
        PronounGender(ch.BodyId) switch
        {
            1 => Msg.PronounHe,
            2 => Msg.PronounShe,
            _ => Msg.PronounIt,
        });

    /// <summary>"his" / "her" / "its" (CChar::GetPossessPronoun,
    /// CCharStatus.cpp:635-657).</summary>
    public static string GetPossessPronoun(this Character ch) => ServerMessages.Get(
        PronounGender(ch.BodyId) switch
        {
            1 => Msg.PossesspronounHis,
            2 => Msg.PossesspronounHer,
            _ => Msg.PossesspronounIts,
        });

    /// <summary>The chardef's trade name: a NAME written as a name-list reference
    /// ("#NAMES_HUMANMALE the Carpenter") gives up the reference and the "the "
    /// (CCharBase::GetTradeName, CCharBase.cpp:54-69).</summary>
    public static string GetTradeName(this Character ch)
    {
        var def = DefinitionLoader.GetCharDef(ch.CharDefIndex);
        if (def != null && !string.IsNullOrEmpty(def.Name))
            return Clients.PaperdollText.GetTradeName(def.Name);
        return ch.Name;
    }

    /// <summary>
    /// True when the character carries a unique name that differs from its chardef
    /// trade name -- mirrors Source-X CChar::IsIndividualName().
    /// </summary>
    public static bool IsIndividualName(this Character ch)
    {
        var def = DefinitionLoader.GetCharDef(ch.CharDefIndex);
        if (def == null || string.IsNullOrEmpty(def.Name)) return false;
        return !ch.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pet master lookup. Returns null when the character has no master or the
    /// master can't be resolved (e.g. owner offline + not loaded). Mirrors
    /// CChar::NPC_PetGetOwner().
    /// </summary>
    public static Character? GetPetOwner(this Character ch, World.GameWorld world)
    {
        return ch.ResolveOwnerCharacter() ?? (ch.OwnerSerial.IsValid ? world.FindChar(ch.OwnerSerial) : null);
    }

    // ---- Item: ARMSLORE / TASTEID accessors ----

    /// <summary>True for any armor / shield / weapon / clothing / jewelry that ArmsLore inspects.</summary>
    public static bool IsTypeArmorWeapon(this Item it) =>
        IsArmorPiece(it.ItemType) || IsWeapon(it.ItemType);

    public static bool IsArmorPiece(ItemType t) => t is
        ItemType.Armor or ItemType.ArmorLeather or ItemType.ArmorBone or
        ItemType.ArmorChain or ItemType.ArmorRing or ItemType.Shield or
        ItemType.Clothing or ItemType.Jewelry;

    public static bool IsWeapon(ItemType t) => t is
        ItemType.WeaponMaceSharp or ItemType.WeaponMaceSmith or
        ItemType.WeaponMaceStaff or ItemType.WeaponMaceCrook or
        ItemType.WeaponMacePick or ItemType.WeaponSword or ItemType.WeaponAxe or
        ItemType.WeaponFence or ItemType.WeaponBow or ItemType.WeaponXBow or
        ItemType.WeaponThrowing or ItemType.WeaponWhip;

    /// <summary>
    /// Weapon type set that ArmsLore prints damage for. Source-X enumerates
    /// IT_WEAPON_MACE_*, IT_WEAPON_SWORD, IT_WEAPON_AXE, IT_WEAPON_FENCE,
    /// IT_WEAPON_BOW, IT_WEAPON_XBOW, IT_WEAPON_THROWING. SphereNet's ItemType
    /// only exposes <see cref="ItemType.WeaponMaceSharp"/> directly today; richer
    /// weapon variants live in tags / chardef so we treat any non-armor combat
    /// item as a weapon for the ArmsLore branch.
    /// </summary>
    public static bool ArmsLoreShowsAsWeapon(this Item it) => IsWeapon(it.ItemType);

    /// <summary>
    /// Defense rating used by ARMSLORE_DEF. Pulls from the itemdef's ARMOR
    /// property when available; falls back to the runtime ARMOR tag (Source-X
    /// CItem::Armor_GetDefense). Returns 0 when nothing is set so the line still
    /// renders deterministically.
    /// </summary>
    public static int GetArmorDefense(this Item it)
    {
        // Armor_GetDefense (CItem.cpp:4897-4915): m_defenseBase + m_ModAr - the BASE
        // value, never the middle of a range - scaled by wear under
        // OF_ScaleDamageByDurability, plus the enchantment of a magic piece, never
        // below 0.
        return FinishRating(it, (long)GetBaseArmorDefense(it) + it.ModAr,
            enchant: IsArmorPiece(it.ItemType) && it.ItemType != ItemType.Jewelry);
    }

    private static int GetBaseArmorDefense(Item it)
    {
        // The item's own stamped rating first (Source-X m_defenseBase), then the tag
        // an older pack may have set by hand, then the definition.
        if (it.DefenseBaseRaw is > 0)
            return it.DefenseLo;
        if (it.TryGetTag("ARMOR", out string? tag) && ScriptNumber.TryParseInt(tag, out int v))
            return v;
        return DefinitionLoader.GetItemDef(it.BaseId)?.DefenseMin ?? 0;
    }

    /// <summary>Attack rating used by ARMSLORE_DAM (CItem::Weapon_GetAttack).</summary>
    public static int GetWeaponAttack(this Item it)
    {
        // Weapon_GetAttack(true) (CItem.cpp:4917-4937): m_attackBase + m_ModAr +
        // m_attackRange - the TOP of the damage range - scaled by wear under
        // OF_ScaleDamageByDurability, plus the enchantment of a magic weapon that is
        // not a wand, never below 0.
        return FinishRating(it, (long)GetBaseWeaponAttack(it) + it.ModAr,
            enchant: IsWeapon(it.ItemType));
    }

    private static int GetBaseWeaponAttack(Item it)
    {
        if (it.AttackBaseRaw is > 0)
            return it.AttackHi;
        if (it.TryGetTag("DAM", out string? tag) && ScriptNumber.TryParseInt(tag, out int v))
            return v;
        return DefinitionLoader.GetItemDef(it.BaseId)?.AttackMax ?? 0;
    }

    /// <summary>The tail both ratings share (CItem.cpp:4906-4913 / :4928-4935).</summary>
    private static int FinishRating(Item it, long value, bool enchant)
    {
        int max = it.HitsMax, cur = it.HitsCur;
        if ((Clients.GameClient.ServerOptionFlags & OptionFlags.ScaleDamageByDurability) != 0 &&
            max > 0 && cur < max)
            value = value * (50 + ((50 * cur) / max)) / 100;
        // SPELL_Enchant is the item enchantment (88, uofiles_enums.h:904), not the
        // Mysticism spell; m_itArmor.m_spelllevel is the word beside the spell id (MOREY).
        if (enchant && it.IsAttr(ObjAttributes.Magic) &&
            Character.ResolveSpellDef?.Invoke(SpellType.EnchantItem) is { } enchantDef)
            value += enchantDef.GetEffect(it.MoreP.Y);
        return (int)Math.Clamp(value, 0L, int.MaxValue);
    }

    /// <summary>Repair quality desc -- CItem::Armor_GetRepairDesc (CItem.cpp:5774-5790),
    /// the ITEMSTATUS_* messages; the one port of it is shared with the wear
    /// messages.</summary>
    public static string GetArmorRepairDesc(this Item it) => Combat.ItemDamageEngine.RepairDesc(it);

    public static int GetHitsCur(this Item it) => it.HitsCur;

    public static int GetHitsMax(this Item it) => it.HitsMax;

    /// <summary>The poison coat on a weapon or meal, 0-100 (m_poison_skill, MOREZ).</summary>
    public static int GetPoisonSkill(this Item it) => Combat.CombatEngine.GetWeaponPoisonSkill(it);

    /// <summary>
    /// Item full name (Source-X CItem::GetNameFull): the item's name as GetName resolves
    /// it - its own, else its definition's, else the tile's, pluralised for the amount -
    /// with "an" / "a" by the vowel rule. Only a name stamped on the item itself was
    /// read, so an item carrying its definition's name came out as "an item".
    ///
    /// The flag is "identified", not "add an article" (CItem.cpp:1774-1986): the
    /// article or the amount is always there, and an identified item also names what
    /// it is - Blessed / Cursed / Magic in front, "of &lt;spell&gt; (N charges)" behind.
    /// The loom, archery butte, town stone, hide and light suffixes are not ported;
    /// their counters have no home on this item model.
    /// </summary>
    public static string GetNameFull(this Item it, bool identified)
    {
        string name = it.GetName();
        if (string.IsNullOrEmpty(name)) name = "item";
        bool noPrefix = (Clients.GameClient.ServerOptionFlags & OptionFlags.NoPrefix) != 0;
        bool individual = IsIndividualName(it);

        // :1789-1799 - the article of an unnamed single item, or the amount.
        bool singular = it.Amount == 1 || it.ItemType == ItemType.Corpse;
        var text = new System.Text.StringBuilder();
        bool titled = !singular; // pszTitle is already "%u " for a pile
        if (singular)
        {
            if (!individual)
                text.Append(GetArticleAndSpace(it, noPrefix));
        }
        else
            text.Append(it.Amount).Append(' ');

        // :1801-1846
        const ObjAttributes Holiness = ObjAttributes.Cursed | ObjAttributes.Cursed2 |
            ObjAttributes.Blessed | ObjAttributes.Blessed2;
        if (identified && (it.Attributes & (Holiness | ObjAttributes.Magic)) != 0)
        {
            string? title = (it.Attributes & Holiness) switch
            {
                ObjAttributes.Cursed | ObjAttributes.Cursed2 => Msg.ItemtitleUnholy,
                ObjAttributes.Cursed2 => Msg.ItemtitleDamned,
                ObjAttributes.Cursed => Msg.ItemtitleCursed,
                ObjAttributes.Blessed | ObjAttributes.Blessed2 => Msg.ItemtitleHoly,
                ObjAttributes.Blessed2 => Msg.ItemtitleSacred,
                ObjAttributes.Blessed => Msg.ItemtitleBlessed,
                _ => null,
            };
            if (title != null)
            {
                string titleText = ServerMessages.Get(title);
                // The title's own article replaces the item's.
                if (singular && !noPrefix)
                    text.Clear().Append(ArticleFor(titleText));
                text.Append(titleText);
                titled = true;
            }

            if (it.IsAttr(ObjAttributes.Magic))
            {
                if (!titled)
                    text.Clear().Append(noPrefix ? "" : "a ");
                // No "magic" on arms and armour, nor on a name that already says so.
                if (!it.IsTypeArmorWeaponOrWand() &&
                    !name.StartsWith("MAGIC", StringComparison.OrdinalIgnoreCase))
                    text.Append(ServerMessages.Get(Msg.ItemtitleMagic));
            }
        }

        // :1848-1882 - the type's own prefix.
        switch (it.ItemType)
        {
            case ItemType.StoneGuild:
                text.Append(ServerMessages.Get(Msg.ItemtitleGuildstoneFor));
                break;
            case ItemType.StoneTown:
                text.Append(ServerMessages.Get(Msg.ItemtitleTownOf));
                break;
            case ItemType.EqMemoryObj:
                text.Append(ServerMessages.Get(Msg.ItemtitleMemoryOf));
                break;
            case ItemType.SpawnChar:
                if (!individual)
                    text.Append(ServerMessages.Get(Msg.ItemtitleSpawn));
                break;
            case ItemType.Key:
                if (!Item.HasLockCode(it))
                    text.Append(ServerMessages.Get(Msg.ItemtitleBlank));
                break;
            case ItemType.Rune:
                if (!it.HasRuneMark)
                    text.Append(ServerMessages.Get(Msg.ItemtitleBlank));
                else if (it.More1 == 0) // m_itRune.m_Strength
                    text.Append(ServerMessages.Get(Msg.ItemtitleFaded));
                break;
            case ItemType.Telepad:
                if (!it.HasRuneMark)
                    text.Append(ServerMessages.Get(Msg.ItemtitleBlank));
                break;
        }

        text.Append(name);

        // :1888-1902 - what a magic weapon or piece of armour casts (m_itWeapon.m_spell
        // is MOREX, m_spellcharges MORE2).
        if (identified && it.IsAttr(ObjAttributes.Magic) && it.IsTypeArmorWeaponOrWand())
        {
            var spell = (SpellType)it.MoreP.X;
            if (spell != SpellType.None && Character.ResolveSpellDef?.Invoke(spell) is { } spellDef)
            {
                text.Append(" of ").Append(spellDef.Name);
                if (it.More2 != 0)
                    text.Append(" (").Append(it.More2).Append(' ')
                        .Append(ServerMessages.Get(Msg.ItemtitleCharges)).Append(')');
            }
        }

        // :1968-1983
        if (it.IsAttr(ObjAttributes.Stolen))
        {
            var victim = it.Link.IsValid ? Item.ResolveWorld?.Invoke()?.FindChar(it.Link) : null;
            if (victim != null)
                text.Append(" (").Append(ServerMessages.Get(Msg.ItemtitleStolenFrom)).Append(' ')
                    .Append(victim.GetName()).Append(')');
            else
                text.Append(" (").Append(ServerMessages.Get(Msg.ItemtitleStolen)).Append(')');
        }

        return text.ToString();
    }

    /// <summary>CItem::IsTypeArmorWeapon as GetNameFull reads it - "wand is also a
    /// weapon" (CItem.cpp:1888).</summary>
    private static bool IsTypeArmorWeaponOrWand(this Item it) =>
        it.IsTypeArmorWeapon() || it.ItemType == ItemType.Wand;

    /// <summary>The item carries a name of its own, not its definition's
    /// (CObjBase::IsIndividualName).</summary>
    private static bool IsIndividualName(Item it)
    {
        string own = it.Name ?? "";
        if (own.Length == 0) return false;
        string defName = DefinitionLoader.GetItemDef(it.BaseId)?.Name ?? "";
        return defName.Length == 0 ||
            !own.Equals(DefinitionLoader.ResolveNames(defName), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Str_GetArticleAndSpace (sstring.cpp:1215-1233).</summary>
    private static string ArticleFor(string word) =>
        word.Length > 0 && char.ToUpperInvariant(word[0]) is 'A' or 'E' or 'I' or 'O' or 'U' ? "an " : "a ";

    /// <summary>CItemBase::GetArticleAndSpace (CItemBase.cpp:160-172): nothing under
    /// OF_NoPrefix; by the vowel rule for a name the script gave; else what the tile
    /// data says, which may be no article at all.</summary>
    private static string GetArticleAndSpace(Item it, bool noPrefix)
    {
        if (noPrefix) return "";
        string defName = DefinitionLoader.GetItemDef(it.BaseId)?.Name ?? "";
        if (defName.Length > 0)
            return ArticleFor(DefinitionLoader.ResolveNames(defName));
        var mapData = Item.ResolveWorld?.Invoke()?.MapData;
        if (mapData == null)
            return ArticleFor(it.GetName());
        var flags = mapData.GetItemTileData(it.BaseId).Flags;
        if ((flags & MapData.Tiles.TileFlag.ArticleAn) != 0) return "an ";
        if ((flags & MapData.Tiles.TileFlag.ArticleA) != 0) return "a ";
        return "";
    }

    public static bool HasObjAttr(this Item it, ObjAttributes attr) =>
        (it.Attributes & attr) != 0;
}
