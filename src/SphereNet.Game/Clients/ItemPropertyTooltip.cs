using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Clients;

/// <summary>
/// The component-property lines of an item's built-in AOS tooltip: luck, the stat
/// bonuses, resists, hit effects, regeneration and the rest.
///
/// Upstream appends these after the name and the per-type default lines, by walking the
/// object's own property components (CEntityProps::AddPropsTooltipData, called from
/// CClientMsg_AOSTooltip.cpp:118). Each component keeps its numeric properties in a map
/// sorted by property index, and the components themselves sit in a map sorted by
/// component type, so the order a client sees is fixed:
///
///   item-char   (CCPropsItemChar.cpp:213)         WEIGHTREDUCTION
///   item        (CCPropsItem.cpp:177)             LAVAINFUSED, SHIPWRECKITEM, UNLUCKY
///   equippable  (CCPropsItemEquippable.cpp:250)   the long AOS table, alphabetical
///   weapon      (CCPropsItemWeapon.cpp:264)       ASSASSINHONED ... USEBESTWEAPONSKILL
///
/// each in its table's declaration order (src/tables/CCProps*_props.tbl). A property
/// whose value is 0 emits nothing. The argument is the bare decimal value
/// (CClientTooltip(id, int64) formats it with "%lld").
///
/// Only the INSTANCE's own values count: upstream's walk covers the object's
/// components, not the ITEMDEF's, so a value only the definition carries does not
/// appear (scripts put these on the instance, typically in @Create). The equippable
/// component exists only on an equippable type and the weapon component only on a
/// weapon (plus fishing pole and instrument), so their lines are gated the same way.
///
/// Every name here is stored on the item as a tag of the same name - see
/// ComponentProperties, AosEquipProperties, AosOnHitProperties, EquipmentStatBonuses,
/// SpellCastingProperties and CombatSpeedProperties in SphereNet.Core.Enums.
/// </summary>
public static class ItemPropertyTooltip
{
    /// <summary>When the line is shown beyond "value is non-zero".</summary>
    private enum Gate : byte
    {
        Always,
        /// <summary>Only with COMBAT_ELEMENTAL_ENGINE.</summary>
        Elemental,
        /// <summary>COMBAT_ELEMENTAL_ENGINE or DisplayElementalResistance (the
        /// fire/cold/poison/energy resists).</summary>
        ElementalOrDisplay,
    }

    /// <summary>A property line: the tag it reads, the cliloc, whether the value is
    /// passed as the argument (false = the cliloc stands alone) and its gate.</summary>
    private readonly record struct Line(string Key, uint Cliloc, bool WithValue, Gate Gate = Gate.Always);

    // CCPropsItemChar::AddPropsTooltipData - item-only lines.
    private static readonly Line[] ItemCharLines =
    [
        new("WEIGHTREDUCTION", 1072210, true),      // Weight reduction: ~1_PERCENTAGE~%
    ];

    // CCPropsItem::AddPropsTooltipData - item-only lines.
    private static readonly Line[] ItemLines =
    [
        new("LAVAINFUSED", 1151318, true),          // lava infused ~1_token~
        new("SHIPWRECKITEM", 1041645, false),       // recovered from a shipwreck
        new("UNLUCKY", 1151821, false),             // Luck -100
    ];

    // CCPropsItemEquippable::AddPropsTooltipData, numeric half, in PROPIEQUIP order.
    // Properties upstream lists with no cliloc (COMBATBONUS*, EATERDAM,
    // INCREASEDEFCHANCEMAX, RAGEFOCUS, HITSPELLSTR, REGENFOOD, REGENVAL*, RES*MAX,
    // RESONANCE*, SPELLCONSUMPTION, SLAYER_*) emit nothing there and are absent here.
    private static readonly Line[] EquippableLines =
    [
        new("ALTERED", 1111880, false),             // Altered
        new("ANTIQUE", 1152714, false),             // Antique
        new("BONUSBERSERK", 1151541, true),         // Berserk ~1_VAL~
        new("BONUSDEX", 1060409, true),             // dexterity bonus ~1_val~
        new("BONUSDURABILITY", 1060410, true),      // durability ~1_val~%
        new("BONUSHITSMAX", 1060431, true),         // hit point increase ~1_val~%
        new("BONUSINT", 1060432, true),             // intelligence bonus ~1_val~
        new("BONUSMANAMAX", 1060439, true),         // mana increase ~1_val~
        new("BONUSSTAMMAX", 1060484, true),         // stamina increase ~1_val~
        new("BONUSSTR", 1060485, true),             // strength bonus ~1_val~
        new("BRITTLE", 1116209, false),             // Brittle
        new("CASTINGFOCUS", 1113696, true),         // Casting Focus ~1_val~%
        new("DAMCHAOS", 1072846, true, Gate.Elemental),     // chaos damage ~1_val~%
        new("DAMCOLD", 1060404, true, Gate.Elemental),      // cold damage ~1_val~%
        new("DAMDIRECT", 1079978, true, Gate.Elemental),    // direct damage: ~1_PERCENT~%
        new("DAMENERGY", 1060407, true, Gate.Elemental),    // energy damage ~1_val~%
        new("DAMFIRE", 1060405, true, Gate.Elemental),      // fire damage ~1_val~%
        new("DAMPHYSICAL", 1060403, true, Gate.Elemental),  // physical damage ~1_val~%
        new("DAMPOISON", 1060406, true, Gate.Elemental),    // poison damage ~1_val~%
        new("EATERCOLD", 1113594, true, Gate.Elemental),    // Cold Eater ~1_Val~%
        new("EATERENERGY", 1113596, true, Gate.Elemental),  // Energy Eater ~1_Val~%
        new("EATERFIRE", 1113593, true, Gate.Elemental),    // Fire Eater ~1_Val~%
        new("EATERKINETIC", 1113597, true, Gate.Elemental), // Kinetic Eater ~1_Val~%
        new("EATERPOISON", 1113595, true, Gate.Elemental),  // Poison Eater ~1_Val~%
        new("ENHANCEPOTIONS", 1060411, true),       // enhance potions ~1_val~%
        new("EPHEMERAL", 1153085, false),           // Moonbound: Ephemeral
        new("FASTERCASTING", 1060413, true),        // faster casting ~1_val~
        new("FASTERCASTRECOVERY", 1060412, true),   // faster cast recovery ~1_val~
        new("HITAREACOLD", 1060416, true, Gate.Elemental),     // hit cold area ~1_val~%
        new("HITAREAENERGY", 1060418, true, Gate.Elemental),   // hit energy area ~1_val~%
        new("HITAREAFIRE", 1060419, true, Gate.Elemental),     // hit fire area ~1_val~%
        new("HITAREAPHYSICAL", 1060428, true, Gate.Elemental), // hit physical area ~1_val~%
        new("HITAREAPOISON", 1060429, true, Gate.Elemental),   // hit poison area ~1_val~%
        new("HITCURSE", 1113712, true),             // Hit Curse ~1_val~%
        new("HITDISPEL", 1060417, true),            // hit dispel ~1_val~%
        new("HITFATIGUE", 1113700, true),           // Hit Fatigue ~1_val~%
        new("HITFIREBALL", 1060420, true),          // hit fireball ~1_val~%
        new("HITHARM", 1060421, true),              // hit harm ~1_val~%
        new("HITLEECHLIFE", 1060422, true),         // hit life leech ~1_val~%
        new("HITLEECHMANA", 1060427, true),         // hit mana leech ~1_val~%
        new("HITLEECHSTAM", 1060430, true),         // hit stamina leech ~1_val~%
        // Upstream sends 1060422 (hit life leech) here - a copy slip in
        // CCPropsItemEquippable.cpp:410. 1060423 is the "hit lightning ~1_val~%" entry
        // the comment beside it names, so the client gets the line it describes.
        new("HITLIGHTNING", 1060423, true),         // hit lightning ~1_val~%
        new("HITLOWERATK", 1060424, true),          // hit lower attack ~1_val~%
        new("HITLOWERDEF", 1060425, true),          // hit lower defense ~1_val~%
        new("HITMAGICARROW", 1060426, true),        // hit magic arrow ~1_val~%
        new("HITMANADRAIN", 1113699, true),         // Hit Mana Drain ~1_val~%
        new("HITSPARKS", 1157326, true),            // Sparks ~1_val~%
        new("HITSWARM", 1157325, true),             // Swarm ~1_val~%
        new("IMBUED", 1080418, false),              // (Imbued)
        new("INCREASEDAM", 1060401, true),          // damage increase ~1_val~%
        new("INCREASEDEFCHANCE", 1060408, true),    // defense chance increase ~1_val~%
        new("INCREASEGOLD", 1060414, true),         // gold increase ~1_val~%
        new("INCREASEHITCHANCE", 1060415, true),    // hit chance increase ~1_val~%
        new("INCREASEKARMALOSS", 1075210, true),    // increased karma loss ~1val~%
        new("INCREASESPELLDAM", 1060483, true),     // spell damage increase ~1_val~%
        new("INCREASESWINGSPEED", 1060486, true),   // swing speed increase ~1_val~%
        new("LOWERAMMOCOST", 1075208, true),        // Lower Ammo Cost ~1_Percentage~%
        new("LOWERMANACOST", 1060433, true),        // lower mana cost ~1_val~%
        new("LOWERREAGENTCOST", 1060434, true),     // lower reagent cost ~1_val~%
        new("LOWERREQ", 1060435, true),             // lower requirements ~1_val~%
        new("LUCK", 1060436, true),                 // luck ~1_val~
        new("MAGEARMOR", 1060437, false),           // mage armor
        new("MASSIVE", 1038003, true),
        new("NIGHTSIGHT", 1060441, false),          // night sight
        new("PRIZED", 1154910, false),              // Prized
        new("REACTIVEPARALYZE", 1112364, false),    // reactive paralyze
        new("REFLECTPHYSICALDAM", 1060442, true),   // reflect physical damage ~1_val~%
        new("REGENHITS", 1060444, true),            // hit point regeneration ~1_val~
        new("REGENMANA", 1060440, true),            // mana regeneration ~1_val~
        new("REGENSTAM", 1060443, true),            // stamina regeneration ~1_val~
        new("RESCOLD", 1060445, true, Gate.ElementalOrDisplay),    // cold resist ~1_val~%
        new("RESENERGY", 1060446, true, Gate.ElementalOrDisplay),  // energy resist ~1_val~%
        new("RESFIRE", 1060447, true, Gate.ElementalOrDisplay),    // fire resist ~1_val~%
        new("RESPHYSICAL", 1060448, true, Gate.Elemental),         // physical resist ~1_val~%
        new("RESPOISON", 1060449, true, Gate.ElementalOrDisplay),  // poison resist ~1_val~%
        new("SOULCHARGE", 1113630, true),           // Soul Charge ~1_val~%
        new("SOULCHARGECOLD", 1113632, true, Gate.Elemental),      // Soul Charge : Cold : ~1_val~%
        new("SOULCHARGEENERGY", 1113634, true, Gate.Elemental),    // Soul Charge : Energy : ~1_val~%
        new("SOULCHARGEFIRE", 1113631, true, Gate.Elemental),      // Soul Charge : Fire : ~1_val~%
        new("SOULCHARGEKINETIC", 1113635, true, Gate.Elemental),   // Soul Charge : Kinetic : ~1_val~%
        new("SOULCHARGEPOISON", 1113633, true, Gate.Elemental),    // Soul Charge : Poison : ~1_val~%
        new("SPELLCHANNELING", 1060482, false),     // spell channeling
        new("SPELLFOCUSING", 1151391, false),       // Spell Focusing
        new("UNWIELDLY", 1154909, false),           // Unwieldly
    ];

    // CCPropsItemWeapon::AddPropsTooltipData, in PROPIWEAP order (BONEBREAKER is
    // declared before BLOODDRINKER there).
    private static readonly Line[] WeaponLines =
    [
        new("ASSASSINHONED", 1152206, false),       // Assassin Honed
        new("BALANCED", 1072792, false),            // Balanced
        new("BANE", 1154671, false),                // Bane
        new("BATTLELUST", 1113710, false),          // Battle Lust
        new("BONEBREAKER", 1157318, false),         // Bone Breaker
        new("BLOODDRINKER", 1113591, false),        // Blood Drinker
        new("MAGEWEAPON", 1060438, true),           // mage weapon -~1_val~ skill
        new("SEARING", 1151183, false),             // Searing Weapon
        new("SPLINTERING", 1112857, true),          // splintering weapon ~1_val~%
        new("USEBESTWEAPONSKILL", 1060400, false),  // use best weapon skill
    ];

    /// <summary>Append the item's component-property lines to <paramref name="props"/>,
    /// in upstream order, gated by <paramref name="elementalEngine"/> (COMBAT_ELEMENTAL_ENGINE)
    /// and <paramref name="displayElementalResistance"/> (DisplayElementalResistance).</summary>
    public static void Append(Item item, List<(uint ClilocId, string Args)> props,
        bool elementalEngine, bool displayElementalResistance)
    {
        // Upstream gives a memory item no property components at all (CItem.cpp:168).
        if (item.ItemType == Core.Enums.ItemType.EqMemoryObj)
            return;

        AppendLines(item, ItemCharLines, props, elementalEngine, displayElementalResistance);
        AppendLines(item, ItemLines, props, elementalEngine, displayElementalResistance);
        if (item.IsTypeEquippable)
            AppendLines(item, EquippableLines, props, elementalEngine, displayElementalResistance);
        if (item.IsWeaponComponentType)
            AppendLines(item, WeaponLines, props, elementalEngine, displayElementalResistance);
    }

    private static void AppendLines(Item item, Line[] lines, List<(uint ClilocId, string Args)> props,
        bool elementalEngine, bool displayElementalResistance)
    {
        foreach (var line in lines)
        {
            switch (line.Gate)
            {
                case Gate.Elemental when !elementalEngine:
                case Gate.ElementalOrDisplay when !elementalEngine && !displayElementalResistance:
                    continue;
            }
            if (!item.TryGetTag(line.Key, out string? raw) ||
                !ScriptNumber.TryParseToken(raw, out long value) || value == 0)
                continue;
            props.Add((line.Cliloc, line.WithValue
                ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty));
        }
    }
}
