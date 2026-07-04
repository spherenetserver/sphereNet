// NPC casting: spell selection, wands, breath/throw and special abilities.
// Decomposed from the former single-file NpcAI.cs (see NpcAI.cs core).
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Messages;
using SphereNet.Game.World;

namespace SphereNet.Game.AI;

public sealed partial class NpcAI
{
    /// <summary>
    /// Source-X: NPC_FightMagery — attempt to cast a spell at the target.
    /// Requires INT >= 5, spell list on NPC, mana available.
    /// When mana drops below 25% of INT, NPC abandons magic and goes melee.
    /// At melee range with good mana, steps back to gain casting distance.
    /// </summary>
    private bool TryNpcCastSpell(Character npc, Character target, int dist)
    {
        if (npc.Int < 5)
            return false;

        // Source-X NPC_FightMayCast: scripts throttle a caster by setting
        // NPCNoCastTill (a tick-time gate) — it blocks ALL casting until due.
        if (npc.TryGetTag("NPCNoCastTill", out string? noCastTill) &&
            long.TryParse(noCastTill, out long noCastUntil) &&
            noCastUntil > Environment.TickCount64)
            return false;

        int mana = npc.Mana;
        int intStat = npc.Int;

        // NOTE: no low-mana early abort here — Source-X has none. Low mana
        // flows into the chance formula below (which FAVORS casting cheap
        // spells when starved) and each spell's own affordability check.

        // Source-X caps ALL magery (wand included) at ¾ of sight range.
        if (dist > GetNpcSight(npc) * 3 / 4)
            return false;

        // Source-X NPC_FightMagery: within striking distance a tactician
        // (Tactics > 20.0) stands and fights ~50% of the time — magery fails
        // this tick and NPC_Act_Fight falls through to melee. The old
        // unconditional step-back inverted this for every caster (always
        // kited, never melee'd) and gated on mana instead of Tactics.
        if (dist <= 1 &&
            npc.GetSkill(SkillType.Tactics) > 200 && _rand.Next(2) == 0)
            return false;

        // Source-X NPC_FightMayCast region gate: SAFE blocks casting too,
        // not just antimagic (CCharNPCStatus REGION_ANTIMAGIC|REGION_FLAG_SAFE).
        // Sits above the wand roll so wands honor it as well.
        var region = _world.FindRegion(npc.Position);
        if (region != null && (region.NoMagic || region.IsFlag(RegionFlag.Safe)))
            return false;

        // Mana-based cast chance — exact Source-X dice: GetVal(chance) yields
        // 0..chance-1 (not 0..chance), and the kite-backoff carries a ~1/INT
        // sub-gate that instead abandons the magery attempt entirely.
        int chance = Math.Max(1, mana >= intStat / 2 ? mana : intStat - mana);
        if (_rand.Next(chance) < intStat / 4)
        {
            // Failed chance — kite to maintain distance while mana regens
            if (mana > intStat / 3 && _rand.Next(intStat) != 0)
            {
                if (dist < 4)
                    MoveAway(npc, target.Position);
                else if (dist > 8)
                    MoveToward(npc, target.Position, run: true);
                return true;
            }
            return false;
        }

        // Source-X NPC_FightMagery wand path — AFTER the chance gates (the
        // old top-of-method placement let wand NPCs bypass the backoff and
        // stand-and-fight logic). A charged IT_WAND (spell in More1 +
        // ATTR_MAGIC) fires ~50% of the time, mana-free.
        Item? wand = FindNpcWand(npc);
        if (wand != null && _rand.Next(2) == 0)
        {
            if (CastViaTrigger(npc, target, (SpellType)wand.More1, wandUse: true))
            {
                ConsumeWandCharge(wand);
                return true;
            }
        }

        if (npc.NpcSpells.Count == 0)
        {
            // No scripted spell list — derive one from a carried/equipped
            // spellbook (Source-X NPC_AddSpellsFromBook). Tried once per NPC.
            EnsureNpcSpellsFromBook(npc);
            if (npc.NpcSpells.Count == 0)
                return false;
        }

        // Source-X NPC_FightCast loop: iterate until a spell the NPC can
        // actually AFFORD (per-spell mana + skill requirement, via the same
        // check CANCAST uses) — a single uncastable pick used to waste the
        // whole magery tick with no fallback.
        static bool CanAfford(Character ch, SpellType s) =>
            Character.OnCanCastCheck?.Invoke(ch, (int)s) ?? true;

        var (spell, castTarget) = ChooseBestSpell(npc, target, dist);
        if (spell != SpellType.None && CanAfford(npc, spell))
        {
            // Fire @NPCActCast and cast unless the script aborts. On abort
            // (RETURN 1) CastViaTrigger returns false, so the magery attempt
            // fails and NPC_Act_Fight falls through to archery/melee.
            return CastViaTrigger(npc, castTarget, spell);
        }

        foreach (var candidate in npc.NpcSpells)
        {
            if (candidate == spell)
                continue;
            // Fallback candidates fire at the enemy — leave beneficial spells
            // to ChooseBestSpell's own recipient logic above.
            if (candidate is SpellType.Heal or SpellType.GreaterHeal
                or SpellType.Cure or SpellType.ArchCure or SpellType.Resurrection)
                continue;
            if (!CanAfford(npc, candidate))
                continue;
            return CastViaTrigger(npc, target, candidate);
        }
        return false;
    }

    /// <summary>Fire @NPCActCast, then launch the native cast unless the script
    /// aborted it. Source-X parity: RETURN 1 cancels the cast and the fight
    /// falls back to melee (returns false); otherwise the possibly-overridden
    /// spell/target are cast (returns true). With no script hook this always
    /// casts the given spell.</summary>
    private bool CastViaTrigger(Character npc, Character target, SpellType spell, bool wandUse = false)
    {
        if (OnNpcActCast != null)
        {
            var d = OnNpcActCast(npc, target, spell, wandUse);
            if (d.Abort)
                return false; // RETURN 1 — revert to melee, no cast this attempt
            if (d.Spell != SpellType.None) spell = d.Spell;
            if (d.Target != null && !d.Target.IsDeleted && !d.Target.IsDead) target = d.Target;
        }
        OnNpcCastSpell?.Invoke(npc, target, spell);
        return true;
    }

    /// <summary>
    /// Intelligent spell selection. Priority:
    /// 1. Self-cure if poisoned
    /// 2. Self-heal if HP &lt; 50%
    /// 3. Dispel if target is summoned
    /// 4. Area spells if 3+ enemies nearby
    /// 5. High damage at close range
    /// 6. Ranged damage at distance
    /// 7. Fallback: random non-reflected spell
    /// </summary>
    /// <summary>Pick the best spell AND the character it should be cast on.
    /// Beneficial spells (heal/cure) return the wounded recipient (self or a
    /// hurt ally); harmful spells return the enemy. Previously every spell was
    /// cast on the enemy, so "self-heal" actually healed the enemy.</summary>
    internal (SpellType Spell, Character CastTarget) ChooseBestSpell(Character npc, Character target, int dist)
    {
        var spells = npc.NpcSpells;
        bool targetReflects = target.IsStatFlag(StatFlag.Reflection);

        // 1. Self-cure if poisoned (50% chance — don't loop on cure forever)
        if (npc.IsPoisoned && _rand.Next(2) == 0)
        {
            if (spells.Contains(SpellType.Cure)) return (SpellType.Cure, npc);
            if (spells.Contains(SpellType.ArchCure)) return (SpellType.ArchCure, npc);
        }

        // 2. Self-heal below the configured wound threshold (Source-X NPC_FightCast
        //    iHealThreshold, default 30% — sphere.ini NPCHEALTHRESHOLD) instead of a
        //    hardcoded 50%. Flat 33% chance at any wound level (aggressive bias: even
        //    a hurt caster spends ~2/3 of its casts on offense). Below half the
        //    threshold it prefers GreaterHeal.
        int healThreshold = _config.NpcHealThreshold > 0 ? _config.NpcHealThreshold : 30;
        if (npc.MaxHits > 0 && npc.Hits * 100 < npc.MaxHits * healThreshold && _rand.Next(3) == 0)
        {
            if (npc.Hits * 200 < npc.MaxHits * healThreshold && spells.Contains(SpellType.GreaterHeal))
                return (SpellType.GreaterHeal, npc);
            if (spells.Contains(SpellType.Heal)) return (SpellType.Heal, npc);
            if (spells.Contains(SpellType.GreaterHeal)) return (SpellType.GreaterHeal, npc);
        }

        // 2b. Group support — heal/cure a wounded ally (Source-X NPC_FightCast
        //     GOOD-spell path). Only caster NPCs with a heal/cure spell scan,
        //     and only under NPC_AI_COMBAT: without the flag Source-X's friend
        //     list is just the caster itself (CCharNPCAct_Magic.cpp:322).
        bool hasHeal = spells.Contains(SpellType.Heal) || spells.Contains(SpellType.GreaterHeal);
        if (GetNpcFlags(npc).HasFlag(NpcAIFlags.Combat) &&
            (hasHeal || spells.Contains(SpellType.Cure)) && _rand.Next(2) == 0)
        {
            var ally = FindWoundedAlly(npc);
            if (ally != null)
            {
                if (ally.IsPoisoned && spells.Contains(SpellType.Cure))
                    return (SpellType.Cure, ally);
                if (ally.MaxHits > 0 && ally.Hits < ally.MaxHits / 4 && spells.Contains(SpellType.GreaterHeal))
                    return (SpellType.GreaterHeal, ally);
                if (spells.Contains(SpellType.Heal)) return (SpellType.Heal, ally);
                if (spells.Contains(SpellType.GreaterHeal)) return (SpellType.GreaterHeal, ally);
            }
        }

        // 3. Dispel if target is summoned
        if (target.IsSummoned)
        {
            if (spells.Contains(SpellType.Dispel)) return (SpellType.Dispel, target);
            if (spells.Contains(SpellType.MassDispel)) return (SpellType.MassDispel, target);
        }

        // 3b. Spell combo chain (ServUO/Source-X mage burst): lock the target
        //     down with Paralyze, then unload Explosion/EnergyBolt/Poison while
        //     it can't move. Returns None unless a combo is active/startable.
        var combo = NextComboSpell(npc, target, targetReflects);
        if (combo != SpellType.None) return (combo, target);

        // 4. Area spells if 3+ enemies nearby
        int nearbyEnemies = 0;
        foreach (var ch in _world.GetCharsInRange(target.Position, 3))
        {
            if (ch == npc || ch.IsDead) continue;
            if (ch.IsPlayer || (ch.NpcBrain != npc.NpcBrain && ch.BodyId != npc.BodyId))
                nearbyEnemies++;
        }
        if (nearbyEnemies >= 3)
        {
            if (spells.Contains(SpellType.MeteorSwarm) && !targetReflects)
                return (SpellType.MeteorSwarm, target);
            if (spells.Contains(SpellType.ChainLightning) && !targetReflects)
                return (SpellType.ChainLightning, target);
        }

        // 5. Paralyze a fleeing target to set up damage — but never spam it.
        //    Skip a target that is already frozen, AND enforce a per-caster
        //    cooldown so a target that breaks free instantly (trapped pouch,
        //    expiry) isn't re-locked ahead of the damage spells below. Without
        //    the cooldown the caster loops on Paralyze forever — the locked or
        //    re-locked target stays >4 tiles away, so this rule keeps firing
        //    and no damage is ever dealt. Between locks it falls through to
        //    Poison/direct damage.
        if (dist > 4 && spells.Contains(SpellType.Paralyze) && !targetReflects
            && !target.IsStatFlag(StatFlag.Freeze))
        {
            long now = Environment.TickCount64;
            long paraNext = 0;
            if (npc.TryGetTag("PARA_CD", out string? pcd)) long.TryParse(pcd, out paraNext);
            if (now >= paraNext)
            {
                npc.SetTag("PARA_CD", (now + ParalyzeRecastCooldownMs).ToString());
                return (SpellType.Paralyze, target);
            }
        }

        // 6. Poison if target isn't poisoned yet
        if (!target.IsPoisoned && spells.Contains(SpellType.Poison) && !targetReflects)
        {
            if (_rand.Next(3) == 0) return (SpellType.Poison, target);
        }

        // 7. Damage spells — prefer high damage, avoid if target reflects
        if (!targetReflects)
        {
            if (dist <= 4)
            {
                if (spells.Contains(SpellType.Explosion)) return (SpellType.Explosion, target);
                if (spells.Contains(SpellType.Flamestrike)) return (SpellType.Flamestrike, target);
            }
            if (spells.Contains(SpellType.EnergyBolt)) return (SpellType.EnergyBolt, target);
            if (spells.Contains(SpellType.Lightning)) return (SpellType.Lightning, target);
            if (spells.Contains(SpellType.Fireball)) return (SpellType.Fireball, target);
            if (spells.Contains(SpellType.Harm)) return (SpellType.Harm, target);
            if (spells.Contains(SpellType.MagicArrow)) return (SpellType.MagicArrow, target);
        }

        // 8. Curse/Weaken debuffs (safe against reflect)
        if (spells.Contains(SpellType.Curse) && _rand.Next(4) == 0) return (SpellType.Curse, target);
        if (spells.Contains(SpellType.Weaken) && _rand.Next(4) == 0) return (SpellType.Weaken, target);

        // 9. Fallback: random non-reflected spell (skip heals/cures on enemies)
        int startIdx = _rand.Next(spells.Count);
        for (int i = 0; i < spells.Count; i++)
        {
            var spell = spells[(startIdx + i) % spells.Count];
            if (spell == SpellType.None) continue;
            if (spell is SpellType.Heal or SpellType.GreaterHeal or SpellType.Cure or SpellType.ArchCure)
                continue;
            if (targetReflects && spell is SpellType.Lightning or SpellType.EnergyBolt
                or SpellType.Explosion or SpellType.Flamestrike or SpellType.MagicArrow
                or SpellType.Fireball or SpellType.Harm or SpellType.MeteorSwarm
                or SpellType.ChainLightning)
                continue;
            return (spell, target);
        }

        return (SpellType.None, target);
    }

    /// <summary>Populate an NPC's spell list from any carried/equipped spellbook
    /// (Source-X NPC_AddSpellsFromBook). The book's itemdef carries the spell
    /// range: TDATA3 = first-spell offset, TDATA4 = max spells. Spell (offset+1+i)
    /// is present when bit i is set in the book's More1:More2 mask (bits 0-31 in
    /// More1, 32-63 in More2 — Source-X CItem::IsSpellInBook). This covers
    /// necro/chivalry/mysticism/spellweaving books, not just the classic 64-bit
    /// magery book. Tried once per NPC.</summary>
    internal static void EnsureNpcSpellsFromBook(Character npc)
    {
        if (npc.TryGetTag("SPELLS_LOADED", out _)) return;
        npc.SetTag("SPELLS_LOADED", "1");

        Item? book = FindSpellbook(npc);
        if (book != null)
        {
            var def = DefinitionLoader.GetItemDef(book.BaseId);
            // TDATA3/TDATA4 define the book's spell window. Undefined (0 max) →
            // fall back to the classic magery book (offset 0, 64 spells).
            int offset = (int)(def?.TData3 ?? 0);
            int maxSpells = (int)(def?.TData4 ?? 0);
            if (maxSpells <= 0) { offset = 0; maxSpells = 64; }

            ulong bits = ((ulong)book.More2 << 32) | book.More1;
            for (int i = 0; i < maxSpells && i < 64; i++)
                if ((bits & (1UL << i)) != 0)
                    npc.NpcSpellAdd((SpellType)(offset + 1 + i));
        }

        if (npc.NpcSpells.Count == 0)
            AddDefaultSpellsForCasterBody(npc);
    }

    private static void AddDefaultSpellsForCasterBody(Character npc)
    {
        if (!IsLichBody(npc.BodyId))
            return;

        npc.NpcSpellAdd(SpellType.MagicArrow);
        npc.NpcSpellAdd(SpellType.Harm);
        npc.NpcSpellAdd(SpellType.Fireball);
        npc.NpcSpellAdd(SpellType.Poison);
        npc.NpcSpellAdd(SpellType.Curse);
        npc.NpcSpellAdd(SpellType.Lightning);
        npc.NpcSpellAdd(SpellType.MindBlast);
        npc.NpcSpellAdd(SpellType.Paralyze);
        npc.NpcSpellAdd(SpellType.EnergyBolt);
        npc.NpcSpellAdd(SpellType.Explosion);
        npc.NpcSpellAdd(SpellType.Flamestrike);
    }

    private static bool IsLichBody(ushort bodyId) =>
        bodyId is 0x0018 or 0x004E or 0x004F or 0x033E;

    /// <summary>All spellbook item types (Source-X CItemBase::IsTypeSpellbook):
    /// magery plus necro/paladin/bushido/ninjitsu/arcanist/mystic/mastery/extra.</summary>
    private static bool IsSpellbookType(ItemType type) => type is
        ItemType.Spellbook or ItemType.SpellbookNecro or ItemType.SpellbookPala or
        ItemType.SpellbookExtra or ItemType.SpellbookBushido or ItemType.SpellbookNinjitsu or
        ItemType.SpellbookArcanist or ItemType.SpellbookMystic or ItemType.SpellbookMastery;

    private static Item? FindSpellbook(Character npc)
    {
        var held = npc.GetEquippedItem(Layer.OneHanded);
        if (held != null && IsSpellbookType(held.ItemType)) return held;
        held = npc.GetEquippedItem(Layer.TwoHanded);
        if (held != null && IsSpellbookType(held.ItemType)) return held;
        var pack = npc.Backpack;
        if (pack != null)
            foreach (var it in pack.Contents)
                if (!it.IsDeleted && IsSpellbookType(it.ItemType)) return it;
        return null;
    }

    /// <summary>An equipped wand (IT_WAND) that can still cast — it holds a spell in
    /// More1, carries ATTR_MAGIC (Source-X NPC_FightMagery requires it,
    /// CCharNPCAct_Magic.cpp:166) and is not out of charges. A wand with no
    /// CHARGES tag stays infinite (matching the player double-click wand path
    /// and the mortechUO imports, which carry no charge tag).</summary>
    internal static Item? FindNpcWand(Character npc)
    {
        // Source-X only inspects LAYER_HAND1 (a HAND2-held wand is ignored).
        var held = npc.GetEquippedItem(Layer.OneHanded);
        if (held == null || held.ItemType != ItemType.Wand || held.More1 == 0)
            return null;
        if (!held.Attributes.HasFlag(ObjAttributes.Magic))
            return null;
        if (held.TryGetTag("CHARGES", out string? ch) && int.TryParse(ch, out int charges) && charges <= 0)
            return null;
        return held;
    }

    /// <summary>Decrement a wand's CHARGES after a cast; at zero, clear its spell and
    /// the tag (mirrors ClientItemUseHandler's player wand path). A wand with no
    /// CHARGES tag is infinite and is left untouched.</summary>
    private static void ConsumeWandCharge(Item wand)
    {
        if (!wand.TryGetTag("CHARGES", out string? ch) || !int.TryParse(ch, out int charges))
            return;
        charges--;
        if (charges <= 0)
        {
            wand.More1 = 0;
            wand.RemoveTag("CHARGES");
        }
        else
        {
            wand.SetTag("CHARGES", charges.ToString());
        }
    }

    /// <summary>Spell-combo state machine. Tags COMBO_STEP / COMBO_TARGET track
    /// progress. Step 0 may start a combo (Paralyze) when mana is high and the
    /// target is free; later steps unload damage while the target is locked.
    /// Returns None when no combo applies.</summary>
    private SpellType NextComboSpell(Character npc, Character target, bool targetReflects)
    {
        if (targetReflects) { npc.RemoveTag("COMBO_STEP"); return SpellType.None; }
        var spells = npc.NpcSpells;

        int step = 0;
        if (npc.TryGetTag("COMBO_STEP", out string? s)) int.TryParse(s, out step);

        // Abandon a combo aimed at a different target.
        if (step > 0 && npc.TryGetTag("COMBO_TARGET", out string? ct) &&
            (!uint.TryParse(ct, out uint ctUid) || ctUid != target.Uid.Value))
        {
            npc.RemoveTag("COMBO_STEP");
            step = 0;
        }

        if (step > 0)
        {
            // Advance the chain. Each step falls through to the next available
            // spell so a missing spell doesn't stall the combo.
            npc.RemoveTag("COMBO_STEP");
            if (step <= 1)
            {
                if (spells.Contains(SpellType.Explosion)) { npc.SetTag("COMBO_STEP", "2"); return SpellType.Explosion; }
                step = 2;
            }
            if (step <= 2)
            {
                if (spells.Contains(SpellType.EnergyBolt)) { npc.SetTag("COMBO_STEP", "3"); return SpellType.EnergyBolt; }
                step = 3;
            }
            if (step <= 3 && !target.IsPoisoned && spells.Contains(SpellType.Poison))
                return SpellType.Poison; // final hit, combo ends
            return SpellType.None;
        }

        // Try to START a combo: high mana, target not already locked, and we
        // have Paralyze plus at least one follow-up.
        if (npc.Mana >= npc.Int * 2 / 3 && npc.Mana > 40
            && !target.IsStatFlag(StatFlag.Freeze)
            && spells.Contains(SpellType.Paralyze)
            && (spells.Contains(SpellType.Explosion) || spells.Contains(SpellType.EnergyBolt))
            && _rand.Next(3) == 0)
        {
            npc.SetTag("COMBO_STEP", "1");
            npc.SetTag("COMBO_TARGET", target.Uid.Value.ToString());
            return SpellType.Paralyze;
        }
        return SpellType.None;
    }

    // Special-trail creatures (Source-X NPC Action_StartSpecial): the giant
    // spider lays web on the ground, the fire elemental drops fire patches.
    private const ushort GiantSpiderBody = 0x001C;

    private const ushort FireElementalBody = 0x000F;

    private const ushort DefaultWebId = 0x10D5;   // spider web tile

    private const ushort DefaultFireId = 0x398C;  // fire column tile

    // Below this value a trail tag is read as a bare on/off flag (e.g. "1"),
    // not a graphic override; at or above it the value overrides the tile id.
    private const ushort TrailIdOverrideFloor = 0x0100;

    /// <summary>Giant spiders web the ground and fire elementals leave fire
    /// patches as they act (Source-X NPC Action_StartSpecial). Enabled by the
    /// known giant-spider / fire-elemental body, or by a WEBTRAIL / FIRETRAIL
    /// tag — whose value, when it looks like a tile id, overrides the graphic.
    /// Fire patches carry FIELD_DAMAGE so a creature stepping on one is hurt
    /// (the same field path the fire-field spell uses); webs are an atmospheric
    /// obstacle. At most one drop per few ticks, never stacked on a tile.</summary>
    private void TryDropSpecialTrail(Character npc)
    {
        if (npc.IsDead) return;

        bool hasFireTag = npc.TryGetTag("FIRETRAIL", out string? fireTag);
        bool hasWebTag = npc.TryGetTag("WEBTRAIL", out string? webTag);
        bool fire = hasFireTag || npc.BodyId == FireElementalBody;
        bool web = !fire && (hasWebTag || npc.BodyId == GiantSpiderBody);
        if (!fire && !web) return;

        // ~1 in 4 acting ticks, and never two trails on the same tile.
        if (_rand.Next(4) != 0) return;
        foreach (var existing in _world.GetItemsInRange(npc.Position, 0))
        {
            if (!existing.IsDeleted && existing.TryGetTag("SPECIAL_TRAIL", out _))
                return;
        }

        ushort itemId = fire ? DefaultFireId : DefaultWebId;
        string? tagVal = fire ? fireTag : webTag;
        if (!string.IsNullOrWhiteSpace(tagVal) && TryParseTileId(tagVal, out ushort overrideId)
            && overrideId >= TrailIdOverrideFloor)
            itemId = overrideId;

        var item = _world.CreateItem();
        item.BaseId = itemId;
        item.SetTag("SPECIAL_TRAIL", "1");
        long now = Environment.TickCount64;
        if (fire)
        {
            item.Name = "fire";
            int dmg = Math.Clamp(npc.Str / 25, 2, 15);
            item.SetTag("FIELD_DAMAGE", dmg.ToString());
            item.DecayTime = now + 10_000;
        }
        else
        {
            item.Name = "web";
            item.DecayTime = now + 20_000;
        }
        _world.PlaceItem(item, npc.Position);
    }

    /// <summary>Parse a tile id written as hex (0x10D5) or decimal.</summary>
    private static bool TryParseTileId(string s, out ushort id)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(s.AsSpan(2),
                System.Globalization.NumberStyles.HexNumber, null, out id);
        return ushort.TryParse(s, out id);
    }

    /// <summary>Dragon-family bodies (reference CREID list): dragon grey/red,
    /// drakes, wyvern, serpentine/skeletal dragons, shadow/white wyrm, swamp
    /// dragon, ancient wyrm. These breathe regardless of the scripted brain.</summary>
    private static bool IsDragonBody(ushort bodyId) => bodyId switch
    {
        0x000C or 0x003B or 0x003C or 0x003D or 0x003E or
        0x0067 or 0x0068 or 0x006A or 0x00B4 or 0x031A or 0x031E => true,
        _ => false,
    };

    /// <summary>Bodies that throw rocks by default (Source-X NPC_Act_Fight:
    /// CREID_OGRE 0x01, CREID_ETTIN 0x02, CREID_CYCLOPS 0x4C). These throw only
    /// while carrying a rock; a THROWOBJ tag enables throwing on any body.</summary>
    internal static bool IsRockThrowerBody(ushort bodyId) =>
        bodyId is 0x0001 or 0x0002 or 0x004C;

    /// <summary>True when the NPC carries a throwable rock in its pack
    /// (Source-X ContentFind(IT_AROCK)). ItemType.Rock is accepted too so a
    /// plain rock pile also arms a default thrower.</summary>
    internal static bool HasThrowableRock(Character npc)
    {
        var pack = npc.Backpack;
        if (pack == null) return false;
        foreach (var it in pack.Contents)
            if (!it.IsDeleted && it.ItemType is ItemType.ARock or ItemType.Rock)
                return true;
        return false;
    }

    /// <summary>Source-X Skill_Act_Breath: an explicit BREATH.DAM tag is used
    /// UNCLAMPED (script authority); the STR*5/100 default clamps 1-65535.</summary>
    private static int GetBreathDamage(Character npc)
    {
        if (npc.TryGetTag("BREATH.DAM", out string? dmgStr) && int.TryParse(dmgStr, out int custom))
            return Math.Max(1, custom);
        int dmg = npc.Str * 5 / 100;
        return Math.Clamp(dmg, 1, ushort.MaxValue);
    }

    /// <summary>Minimum gap between a caster's Paralyze re-casts on its target
    /// (ChooseBestSpell rule 5). Stops the lock-down rule from firing ahead of
    /// the damage spells every tick — including against a target that breaks
    /// free instantly (trapped pouch) — so the caster actually deals damage
    /// between locks instead of looping on Paralyze.</summary>
    private const int ParalyzeRecastCooldownMs = 12000;

    /// <summary>Callback: NPC casts a spell. Parameters: caster, target, spell.
    /// Program.cs handles SpellEngine.CastStart + broadcast.</summary>
    public Action<Character, Character, SpellType>? OnNpcCastSpell { get; set; }

    /// <summary>Advance an NPC's in-progress spell cast timer. Returns true while still casting.</summary>
    public Func<Character, bool>? OnNpcTickSpellCast { get; set; }
}
