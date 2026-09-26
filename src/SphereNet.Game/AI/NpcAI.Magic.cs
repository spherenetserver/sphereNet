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
    /// <summary>Source-X NPC_FightMayCast (CCharNPCStatus.cpp:428-446) without the
    /// skill probe: TAG.NPCNoCastTill still in the future, an anti-magic or safe
    /// region, or under 5 mana (Stat_GetVal(STAT_INT) is the current mana).
    /// NPCNoCastTill is compared against the server clock in TENTHS of a second
    /// (CWorldGameTime::GetCurrentTime().GetTimeRaw() / MSECS_PER_TENTH) - the
    /// value SERV.TIME reads - so a script writes <c>TAG.NPCNoCastTill =
    /// &lt;SERV.TIME&gt; + 50</c> for a five second pause.</summary>
    private bool NpcFightMayCast(Character npc)
    {
        if (npc.TryGetTag("NPCNoCastTill", out string? noCastTill) &&
            SphereNet.Core.Types.ScriptNumber.TryParseToken(noCastTill ?? "", out long noCastUntil) &&
            noCastUntil > _world.GameClockMs / 100)
            return false;
        var region = _world.FindRegion(npc.Position);
        // REGION_ANTIMAGIC_DAMAGE | REGION_FLAG_SAFE (CCharNPCStatus.cpp:441) - an
        // anti-magic-ALL area is not part of this gate.
        if (region != null && (region.IsFlag(RegionFlag.NoMagicDamage) || region.IsFlag(RegionFlag.Safe)))
            return false;
        if (npc.Mana < 5)
            return false;
        return true;
    }

    /// <summary>
    /// Source-X NPC_FightMagery (CCharNPCAct_Magic.cpp:150-271): try to cast at the
    /// fight target. Gates: NPC_FightMayCast, a spell list or a charged wand,
    /// three quarters of sight range, the tactician's stand-and-fight roll and the
    /// mana-based chance roll (a failed roll backs off and reports "busy"). The
    /// spell itself comes from a random start in the NPC's spell list, taking the
    /// first one NPC_FightCast accepts (<see cref="NpcFightMageryWalk"/>); with
    /// NPCAIEXTRAS SmartCaster the role-ordered <see cref="ChooseBestSpell"/> is
    /// asked first.
    /// </summary>
    private bool TryNpcCastSpell(Character npc, Character target, int dist)
    {
        if (!NpcFightMayCast(npc))
            return false;

        if (npc.NpcSpells.Count == 0)
        {
            // No scripted spell list — derive one from a carried/equipped
            // spellbook (Source-X NPC_GetAllSpellbookSpells). Tried once per NPC.
            EnsureNpcSpellsFromBook(npc);
        }
        // A wand in the first hand that is really a charged magic wand
        // (CCharNPCAct_Magic.cpp:161-168).
        Item? wand = FindNpcWand(npc);
        if (npc.NpcSpells.Count < 1 && wand == null)
            return false;

        int mana = npc.Mana;
        int intStat = npc.Int;

        // Source-X caps ALL magery (wand included) at 3/4 of UO_MAP_VIEW_SIGHT, a
        // fixed 10 tiles (CCharNPCAct_Magic.cpp:173) - not the creature's own sight.
        if (dist > UoMapViewSight * 3 / 4)
            return false;

        // Source-X NPC_FightMagery: within striking distance a tactician
        // (Tactics > 20.0) stands and fights ~50% of the time — magery fails
        // this tick and NPC_Act_Fight falls through to melee. The old
        // unconditional step-back inverted this for every caster (always
        // kited, never melee'd) and gated on mana instead of Tactics.
        if (dist <= 1 &&
            npc.GetSkill(SkillType.Tactics) > 200 && _rand.Next(2) == 0)
            return false;

        // Mana-based cast chance — exact Source-X dice (CCharNPCAct_Magic.cpp:185-200):
        // GetVal(chance) yields 0..chance-1, and a failed roll backs off from the
        // target (NPC_Act_Follow(false, rand(3)+2, true)) while mana regenerates -
        // unless a ~1/INT sub-roll abandons the magery attempt instead.
        int chance = Math.Max(1, mana >= intStat / 2 ? mana : intStat - mana);
        if (_rand.Next(chance) < intStat / 4)
        {
            if (mana > intStat / 3 && _rand.Next(Math.Max(1, intStat)) != 0)
            {
                if (dist < 4 || dist > 8)
                    NpcFightFollow(npc, target, _rand.Next(3) + 2, moveAway: true);
                return true;
            }
            return false;
        }

        if (HasExtra(npc, NpcAiExtraFlags.SmartCaster))
        {
            var smart = TrySmartCast(npc, target, dist, wand, mana, intStat);
            if (smart.HasValue)
                return smart.Value;
        }
        return NpcFightMageryWalk(npc, target, dist, wand, mana, intStat);
    }

    /// <summary>Per-spell affordability: mana, reagents and the skill requirement,
    /// through the same check CANCAST uses (Source-X NPC_FightCast Spell_CanCast +
    /// Skill_GetBase &lt; iSkillReq, CCharNPCAct_Magic.cpp:298-302).</summary>
    private static bool CanAffordNpcSpell(Character ch, SpellType s) =>
        Character.OnCanCastCheck?.Invoke(ch, (int)s) ?? true;

    /// <summary>The Source-X spell walk (CCharNPCAct_Magic.cpp:203-271): with a
    /// charged wand, the wand's spell is tried first half of the time; then the
    /// spells from a random index to the end of the list. @NPCActCast fires for
    /// every candidate (RETURN 1 = back to melee; ARGN1 = the spell; REF1 = a new
    /// target, which also overrides the AI's own friend choice for good spells;
    /// LOCAL.HealThreshold read back), and the first candidate NPC_FightCast
    /// accepts is cast. Before the cast starts the caster repositions and
    /// reveals itself (:255-263).</summary>
    private bool NpcFightMageryWalk(Character npc, Character enemy, int dist, Item? wand, int mana, int intStat)
    {
        var spells = npc.NpcSpells;
        int count = spells.Count;
        int idx = count > 0 ? _rand.Next(count) : 0;
        bool wandUse = wand != null && _rand.Next(100) < 50;
        Character castTarget = enemy;
        bool ignoreAiTargetChoice = false;
        int healThreshold = _config.NpcHealThreshold;

        while (idx < count || wandUse)
        {
            bool fromWand = wandUse;
            SpellType spell;
            if (wandUse)
            {
                spell = SphereNet.Game.Magic.SpellEngine.MagicItemSpell(wand!);
                wandUse = false;
            }
            else
            {
                spell = spells[idx];
            }

            if (OnNpcActCast != null)
            {
                var d = OnNpcActCast(npc, castTarget, spell, fromWand);
                if (d.Abort)
                {
                    ResetNpcCombo(npc);
                    return false; // RETURN 1 - revert to melee
                }
                spell = d.Spell;
                if (d.HealThreshold >= 0)
                    healThreshold = d.HealThreshold;
                if (d.Target != null && !d.Target.IsDeleted && (d.Retargeted || d.Target != castTarget))
                {
                    castTarget = d.Target;
                    ignoreAiTargetChoice = true;
                }
            }

            Character chosen = castTarget;
            if (NpcFightCastAccepts(npc, ref chosen, spell, healThreshold, ignoreAiTargetChoice, fromWand))
                return BeginNpcCast(npc, enemy, chosen, spell, fromWand ? wand : null, dist, mana, intStat);
            idx++;
        }
        ResetNpcCombo(npc);
        return false;
    }

    /// <summary>Source-X NPC_FightCast (CCharNPCAct_Magic.cpp:276-451): does this spell
    /// suit the fight right now, and on whom? A harmful spell always does, cast on
    /// the enemy. A GOOD spell aimed at a character is cast only on the caster or,
    /// under NPC_AI_COMBAT, on up to three characters fighting the same enemy -
    /// and only when it is needed (heal at or under the heal threshold, cure while
    /// poisoned, a BLESS whose effect layer is missing). A HEAL spell that takes no
    /// character target is cast on the caster; a summon (and any other spell) is
    /// aimed at the enemy's position. PLAYERONLY spells never qualify.</summary>
    private bool NpcFightCastAccepts(Character npc, ref Character castTarget, SpellType spell,
        int healThreshold, bool ignoreAiTargetChoice, bool fromWand)
    {
        if (spell == SpellType.None)
            return false;
        SpellFlag? resolved = ResolveNpcSpellFlags != null ? ResolveNpcSpellFlags(spell) : FallbackSpellFlags(spell);
        if (resolved is not SpellFlag flags)
            return false; // no [SPELL] definition (:281-283)
        // A wand pays with its charge; the caster's own mana/skill gate applies to
        // spells it casts itself.
        if (!fromWand && !CanAffordNpcSpell(npc, spell))
            return false;
        if (flags.HasFlag(SpellFlag.PlayerOnly))
            return false;

        if (flags.HasFlag(SpellFlag.Harm))
            return true;

        if (flags.HasFlag(SpellFlag.Good))
        {
            if (flags.HasFlag(SpellFlag.TargChar))
            {
                // Help self or friends if needed: self + 3 friends (:312-387).
                var friends = new List<Character>(4);
                if (ignoreAiTargetChoice)
                {
                    friends.Add(castTarget);
                }
                else
                {
                    friends.Add(npc);
                    if (GetNpcFlags(npc).HasFlag(NpcAIFlags.Combat))
                        CollectFightFriends(npc, castTarget, friends);
                    if (flags.HasFlag(SpellFlag.TargNoSelf))
                        friends.RemoveAt(0);
                }
                foreach (var friend in friends)
                {
                    if (NpcGoodSpellSuits(friend, spell, flags, healThreshold))
                    {
                        castTarget = friend;
                        return true;
                    }
                }
                return false;
            }
            if (flags.HasFlag(SpellFlag.Heal))
            {
                // A good HEAL that takes no character target works on the caster
                // (:402-426).
                castTarget = npc;
                return true;
            }
            return true;
        }
        // Summons and everything else: the target stays the enemy, whose position
        // is where the spell lands (m_Act_p = pTarg->GetTopPoint(), :267).
        return true;
    }

    /// <summary>The friend list NPC_FightCast builds under NPC_AI_COMBAT
    /// (CCharNPCAct_Magic.cpp:322-339): characters within sight that remember the
    /// enemy as a fight / harmed-by / irritated-by memory, up to four entries with
    /// the caster in the first slot.</summary>
    private void CollectFightFriends(Character npc, Character enemy, List<Character> friends)
    {
        foreach (var ch in _world.GetCharsInRange(npc.Position, UoMapViewSight))
        {
            if (friends.Count >= 4)
                break;
            if (ch.IsDeleted)
                continue;
            var mem = ch.Memory_FindObj(enemy.Uid);
            if (mem != null && mem.IsMemoryTypes(MemoryType.Fight | MemoryType.HarmedBy | MemoryType.IrritatedBy))
                friends.Add(ch);
        }
    }

    /// <summary>Source-X UO_MAP_VIEW_SIGHT (uofiles_macros.h:17).</summary>
    private const int UoMapViewSight = 14;

    /// <summary>Source-X bSpellSuits (CCharNPCAct_Magic.cpp:356-379).</summary>
    private bool NpcGoodSpellSuits(Character who, SpellType spell, SpellFlag flags, int healThreshold)
    {
        if (who.IsDead)
            return false;
        if (flags.HasFlag(SpellFlag.Heal) && who.MaxHits > 0 && who.Hits * 100 / who.MaxHits <= healThreshold)
            return true;
        if (spell is SpellType.Cure or SpellType.ArchCure or SpellType.CleanseByFire or SpellType.CleansingWinds &&
            !flags.HasFlag(SpellFlag.Scripted) && who.IsPoisoned)
            return true;
        if (flags.HasFlag(SpellFlag.Bless))
        {
            var layer = ResolveNpcSpellLayer?.Invoke(spell) ?? Layer.None;
            if (layer != Layer.None && who.GetEquippedItem(layer) == null)
                return true;
        }
        return false;
    }

    /// <summary>Spell flags for the classic Magery circle when no spell table is
    /// wired (isolated engine use): the SPELLFLAG_* the reference pack's
    /// spells_magery.scp gives each spell, without PLAYERONLY. Any other spell has
    /// no definition and is skipped, as upstream skips a spell it cannot find.</summary>
    private static SpellFlag? FallbackSpellFlags(SpellType spell)
    {
        const SpellFlag harm = SpellFlag.Harm | SpellFlag.TargChar;
        const SpellFlag dmg = harm | SpellFlag.Damage;
        const SpellFlag good = SpellFlag.Good | SpellFlag.TargChar;
        return spell switch
        {
            SpellType.Clumsy or SpellType.Weaken or SpellType.Feeblemind or SpellType.Curse or
                SpellType.ManaDrain or SpellType.ManaVampire or SpellType.Paralyze => harm | SpellFlag.Curse,
            SpellType.Poison => harm,
            SpellType.MagicArrow or SpellType.Harm or SpellType.Fireball or SpellType.Lightning or
                SpellType.MindBlast or SpellType.EnergyBolt or SpellType.Explosion or SpellType.Flamestrike => dmg,
            SpellType.ChainLightning or SpellType.MeteorSwarm or SpellType.Earthquake => dmg | SpellFlag.Area,
            SpellType.MassCurse => SpellFlag.Harm | SpellFlag.Area | SpellFlag.Curse,
            SpellType.WallOfStone or SpellType.FireField or SpellType.PoisonField or
                SpellType.ParalyzeField or SpellType.EnergyField => SpellFlag.Harm | SpellFlag.TargXYZ | SpellFlag.Field,
            SpellType.Heal or SpellType.GreaterHeal => good | SpellFlag.Heal,
            SpellType.Cure or SpellType.NightSight or SpellType.ReactiveArmor or SpellType.Invisibility => good,
            SpellType.ArchCure => SpellFlag.Good | SpellFlag.Area,
            SpellType.Agility or SpellType.Cunning or SpellType.Strength or SpellType.Bless or
                SpellType.Protection or SpellType.MagicReflect => good | SpellFlag.Bless,
            SpellType.ArchProtection => SpellFlag.Good | SpellFlag.Area | SpellFlag.Bless,
            SpellType.Resurrection => good | SpellFlag.TargDead,
            SpellType.BladeSpirit or SpellType.EnergyVortex => SpellFlag.Summon | SpellFlag.TargXYZ,
            SpellType.SummonCreature or SpellType.AirElemental or SpellType.SummonDaemon or
                SpellType.EarthElemental or SpellType.FireElemental or SpellType.WaterElemental => SpellFlag.Summon,
            SpellType.Dispel => SpellFlag.TargChar,
            SpellType.MassDispel or SpellType.Reveal => SpellFlag.Area,
            _ => null,
        };
    }

    /// <summary>The steps between choosing a spell and casting it
    /// (CCharNPCAct_Magic.cpp:255-270): with mana to spare (and a 1 in 2*INT
    /// exception) a caster closer than 4 or farther than 8 tiles repositions to
    /// five tiles (NPC_Act_Follow(false, 5, true)); otherwise it follows the
    /// target in. It reveals itself, then the cast starts (Skill_Start). A wand
    /// spends its charge only once the cast is accepted.</summary>
    private bool BeginNpcCast(Character npc, Character enemy, Character castTarget, SpellType spell,
        Item? wand, int dist, int mana, int intStat)
    {
        if (mana > intStat / 3 && _rand.Next(Math.Max(1, intStat << 1)) != 0)
        {
            if (dist < 4 || dist > 8)
                NpcFightFollow(npc, enemy, 5, moveAway: true);
        }
        else
        {
            NpcFightFollow(npc, enemy, 1, moveAway: false);
        }
        npc.ClearHiddenState();

        bool started = StartNpcCast(npc, castTarget, spell);
        if (started && wand != null)
            ConsumeWandCharge(wand);
        return started;
    }

    /// <summary>Fight-time NPC_Act_Follow(false, maxDistance, fMoveAway)
    /// (CCharNPCAct.cpp:1312-1440): an immobile creature stays put; a target past
    /// the radar is not chased; moving away steps off while closer than
    /// <paramref name="maxDistance"/> and otherwise walks in; following stops
    /// within <paramref name="maxDistance"/>. War mode runs (:1439).</summary>
    private void NpcFightFollow(Character npc, Character target, int maxDistance, bool moveAway)
    {
        if ((CharDefHelper.GetCanFlags(npc) & CanFlags.C_NonMover) != 0)
            return;
        int dist = npc.Position.GetDistanceTo(target.Position);
        int radar = _config.MapViewRadar > 0 ? _config.MapViewRadar : Character.MapViewRadarTiles;
        if (dist > radar)
            return;
        if (moveAway)
        {
            if (dist < maxDistance)
            {
                MoveAway(npc, target.Position);
                return;
            }
        }
        else if (dist <= maxDistance)
        {
            return;
        }
        MoveToward(npc, target.Position, run: true);
    }

    /// <summary>Fire @NPCActCast, then launch the native cast unless the script
    /// aborted it. Source-X parity: RETURN 1 cancels the cast and the fight
    /// falls back to melee (returns false); otherwise the possibly-overridden
    /// spell/target are cast (returns true). With no script hook this always
    /// casts the given spell.</summary>
    private bool CastViaTrigger(Character npc, Character target, SpellType spell, bool wandUse = false)
    {
        if (!FireNpcActCast(npc, ref target, ref spell, wandUse))
            return false;
        return StartNpcCast(npc, target, spell);
    }

    /// <summary>The @NPCActCast half of a cast: false = RETURN 1 (back to melee);
    /// otherwise the spell (ARGN1) and target (REF1) the script left behind.</summary>
    private bool FireNpcActCast(Character npc, ref Character target, ref SpellType spell, bool wandUse)
    {
        if (OnNpcActCast == null)
            return true;
        SpellType requestedSpell = spell;
        Character requestedTarget = target;
        var d = OnNpcActCast(npc, target, spell, wandUse);
        if (d.Abort)
        {
            ResetNpcCombo(npc);
            return false; // RETURN 1 — revert to melee, no cast this attempt
        }
        if (d.Spell != SpellType.None) spell = d.Spell;
        if (d.Target != null && !d.Target.IsDeleted && !d.Target.IsDead) target = d.Target;
        if (spell != requestedSpell || target != requestedTarget)
            ResetNpcCombo(npc);
        return true;
    }

    /// <summary>Start the native cast (Skill_Start(SKILL_MAGERY)); false when the
    /// spell engine refused it.</summary>
    private bool StartNpcCast(Character npc, Character target, SpellType spell)
    {
        bool started;
        if (OnNpcTryStartSpellCast != null)
            started = OnNpcTryStartSpellCast(npc, target, spell);
        else if (OnNpcCastSpell != null)
        {
            OnNpcCastSpell(npc, target, spell);
            started = true;
        }
        else
        {
            started = false;
        }

        if (!started)
            ResetNpcCombo(npc);
        return started;
    }

    private void ResetNpcCombo(Character npc)
    {
        if (_fightMemory.TryGetValue(npc.Uid.Value, out var mem))
        {
            mem.ComboStep = 0;
            mem.ComboTarget = 0;
        }
    }

    private static bool IsKnownBeneficialSpell(SpellType spell) => spell is
        SpellType.Heal or SpellType.NightSight or SpellType.ReactiveArmor or
        SpellType.Agility or SpellType.Cunning or SpellType.Cure or
        SpellType.Protection or SpellType.Strength or SpellType.Bless or
        SpellType.ArchCure or SpellType.ArchProtection or SpellType.GreaterHeal or
        SpellType.Incognito or SpellType.MagicReflect or SpellType.Invisibility or
        SpellType.HorrificBeast or SpellType.LichForm or SpellType.VampiricEmbrace or
        SpellType.WraithForm or SpellType.CloseWounds or SpellType.ConsecrateWeapon or
        SpellType.DivineFury or SpellType.EnemyOfOne or SpellType.RemoveCurse or
        SpellType.Confidence or SpellType.Evasion or SpellType.CounterAttack or
        SpellType.ArcaneCircle or SpellType.GiftOfRenewal or SpellType.ImmolatingWeapon or
        SpellType.Attunement or SpellType.ReaperForm or SpellType.EtherealVoyage or
        SpellType.GiftOfLife or SpellType.ArcaneEmpowerment or SpellType.HealingStone or
        SpellType.Enchant or SpellType.StoneForm or SpellType.SpellTrigger or
        SpellType.CleansingWinds;

    /// <summary>Resolve a safe recipient for a random/fallback spell. Runtime
    /// spell flags are authoritative; the classic/newer beneficial list is the
    /// fallback used by isolated tests or incomplete script packs.</summary>
    private bool TryResolveNpcSpellTarget(
        Character npc, Character enemy, SpellType spell, bool enemyReflects,
        out Character castTarget)
    {
        castTarget = enemy;
        SpellFlag? resolved = ResolveNpcSpellFlags?.Invoke(spell);
        if (resolved is SpellFlag flags && flags != SpellFlag.None)
        {
            bool harmful = flags.HasFlag(SpellFlag.Harm) ||
                           flags.HasFlag(SpellFlag.Damage) ||
                           flags.HasFlag(SpellFlag.Curse) ||
                           flags.HasFlag(SpellFlag.Field);
            if (harmful)
                return !enemyReflects;

            bool beneficial = flags.HasFlag(SpellFlag.Good) ||
                              flags.HasFlag(SpellFlag.Bless) ||
                              flags.HasFlag(SpellFlag.Heal);
            if (beneficial)
            {
                if (flags.HasFlag(SpellFlag.TargDead) || flags.HasFlag(SpellFlag.TargNoSelf))
                    return false;
                // A good spell aimed at a character is cast only when it is NEEDED
                // (NPC_FightCast, CCharNPCAct_Magic.cpp:349): a heal below the heal
                // threshold, a cure while poisoned, a BLESS spell whose effect layer
                // the recipient does not already carry. Anything else - Invisibility,
                // Night Sight, a buff already running - does not suit, and the NPC
                // moves on. Every good spell used to qualify, so a dragon facing a
                // reflecting target recast Invisibility on itself forever.
                if (flags.HasFlag(SpellFlag.TargChar))
                {
                    if (!GoodSpellSuits(npc, spell, flags))
                        return false;
                    castTarget = npc;
                    return true;
                }
                castTarget = flags.HasFlag(SpellFlag.Heal) ? npc : enemy;
                return true;
            }

            // A summon lands at the enemy's position (CCharNPCAct_Magic.cpp:265-267).
            if (flags.HasFlag(SpellFlag.Summon))
                return true;
            return false;
        }

        if (spell == SpellType.Resurrection)
            return false;
        if (IsKnownBeneficialSpell(spell))
        {
            castTarget = npc;
            return true;
        }
        return !enemyReflects || spell is not (
            SpellType.Lightning or SpellType.EnergyBolt or SpellType.Explosion or
            SpellType.Flamestrike or SpellType.MagicArrow or SpellType.Fireball or
            SpellType.Harm or SpellType.MeteorSwarm or SpellType.ChainLightning or
            SpellType.Curse or SpellType.Weaken);
    }

    private bool GoodSpellSuits(Character who, SpellType spell, SpellFlag flags)
    {
        int healThreshold = _config.NpcHealThreshold > 0 ? _config.NpcHealThreshold : 30;
        if (flags.HasFlag(SpellFlag.Heal) && who.MaxHits > 0 && who.Hits * 100 <= who.MaxHits * healThreshold)
            return true;
        if (spell is SpellType.Cure or SpellType.ArchCure or SpellType.CleansingWinds &&
            !flags.HasFlag(SpellFlag.Scripted) && who.IsPoisoned)
            return true;
        if (flags.HasFlag(SpellFlag.Bless))
        {
            var layer = ResolveNpcSpellLayer?.Invoke(spell) ?? Layer.None;
            if (layer != Layer.None && who.GetEquippedItem(layer) == null)
                return true;
        }
        return false;
    }

    /// <summary>Per-NPC fight state that lives only in memory (never saved): the
    /// SmartCaster paralyze cooldown and spell combo, and the CombatExtras breath
    /// cooldown. It used to sit in TAGs (PARA_CD, COMBO_STEP / COMBO_TARGET,
    /// BREATH_CD), which a world save wrote out and a reload brought back stale.</summary>
    private sealed class NpcFightMemory
    {
        public long ParalyzeReadyAt;
        public int ComboStep;
        public uint ComboTarget;
        public long BreathReadyAt;
    }

    private readonly Dictionary<uint, NpcFightMemory> _fightMemory = [];

    private NpcFightMemory FightMemory(Character npc)
    {
        if (!_fightMemory.TryGetValue(npc.Uid.Value, out var mem))
        {
            mem = new NpcFightMemory();
            _fightMemory[npc.Uid.Value] = mem;
        }
        return mem;
    }

    /// <summary>Runtime AI TAGs an older build wrote into saves. They are state, not
    /// script data, so a character loaded with them has them dropped the first time
    /// it fights. The hidden-pursuit pair stays while HiddenPursuit is on, because
    /// that pursuit still keeps its state there.</summary>
    private static readonly string[] LegacyFightStateTags = ["BREATH_CD", "PARA_CD", "COMBO_STEP", "COMBO_TARGET", "SPELLS_LOADED"];

    private void ScrubLegacyFightTags(Character npc)
    {
        if (npc.Tags.Count == 0)
            return;
        foreach (var key in LegacyFightStateTags)
            if (npc.Tags.Has(key))
                npc.RemoveTag(key);
        if (!HasExtra(npc, NpcAiExtraFlags.HiddenPursuit))
        {
            if (npc.Tags.Has("LAST_TGT_LOC")) npc.RemoveTag("LAST_TGT_LOC");
            if (npc.Tags.Has("HIDE_PURSUIT")) npc.RemoveTag("HIDE_PURSUIT");
        }
    }

    /// <summary>NPCAIEXTRAS SmartCaster: the wand (half the time, as the reference
    /// walk does), then the role-ordered <see cref="ChooseBestSpell"/>, still through
    /// @NPCActCast so a script's RETURN 1 / ARGN1 / REF1 win. Null = nothing
    /// suitable, fall back to the Source-X walk.</summary>
    private bool? TrySmartCast(Character npc, Character target, int dist, Item? wand, int mana, int intStat)
    {
        if (wand != null && _rand.Next(2) == 0)
        {
            var wandSpell = SphereNet.Game.Magic.SpellEngine.MagicItemSpell(wand);
            var wandTarget = target;
            if (!FireNpcActCast(npc, ref wandTarget, ref wandSpell, wandUse: true))
                return false;
            if (BeginNpcCast(npc, target, wandTarget, wandSpell, wand, dist, mana, intStat))
                return true;
        }
        if (npc.NpcSpells.Count == 0)
            return null;

        var (spell, castTarget) = ChooseBestSpell(npc, target, dist);
        if (spell == SpellType.None || !CanAffordNpcSpell(npc, spell))
        {
            ResetNpcCombo(npc);
            return null;
        }
        if (!FireNpcActCast(npc, ref castTarget, ref spell, wandUse: false))
            return false;
        return BeginNpcCast(npc, target, castTarget, spell, null, dist, mana, intStat);
    }

    /// <summary>Spell flags for the SmartCaster roles: the loaded [SPELL] FLAGS,
    /// else the classic table.</summary>
    private SpellFlag SmartSpellFlags(SpellType spell) =>
        (ResolveNpcSpellFlags != null ? ResolveNpcSpellFlags(spell) : FallbackSpellFlags(spell)) ?? SpellFlag.None;

    /// <summary>A spell's mana cost for ranking inside a SmartCaster role: the
    /// [SPELL] MANAUSE when wired, else the classic Magery circle cost.</summary>
    private int SmartSpellMana(SpellType spell)
    {
        int wired = ResolveNpcSpellMana?.Invoke(spell) ?? -1;
        if (wired >= 0)
            return wired;
        int id = (int)spell;
        if (id is < 1 or > 64)
            return 0;
        ReadOnlySpan<int> circleMana = [4, 6, 9, 11, 14, 20, 40, 50];
        return circleMana[(id - 1) / 8];
    }

    private static bool IsCureSpell(SpellType s) =>
        s is SpellType.Cure or SpellType.ArchCure or SpellType.CleanseByFire or SpellType.CleansingWinds;

    private static bool IsDispelSpell(SpellType s) =>
        s is SpellType.Dispel or SpellType.MassDispel or SpellType.DispelEvil;

    /// <summary>The cheapest (or, with <paramref name="strongest"/>, the most
    /// expensive) spell of a role; None when it is empty.</summary>
    private SpellType PickByMana(List<SpellType> bucket, bool strongest)
    {
        SpellType best = SpellType.None;
        int bestMana = strongest ? int.MinValue : int.MaxValue;
        foreach (var s in bucket)
        {
            int m = SmartSpellMana(s);
            if (strongest ? m > bestMana : m < bestMana)
            {
                bestMana = m;
                best = s;
            }
        }
        return best;
    }

    /// <summary>
    /// NPCAIEXTRAS SmartCaster spell choice (after ServUO MageAI): the NPC's spells
    /// are sorted into roles by their SPELLFLAGs - any school, not only Magery -
    /// keeping only those it can afford, and the first role that applies wins:
    /// <list type="number">
    /// <item>cure itself (or, under NPC_AI_COMBAT, a poisoned ally);</item>
    /// <item>heal itself under the heal threshold, or a wounded ally under NPC_AI_COMBAT;</item>
    /// <item>dispel a summoned creature attacking it (the target or any other attacker);</item>
    /// <item>answer a heal the enemy is casting with poison or a curse;</item>
    /// <item>the paralyze-then-burst combo and a paralyze on a distant target;</item>
    /// <item>an area spell when three or more foes stand around the target;</item>
    /// <item>poison (one time in three), then direct damage, then a curse (one in four);</item>
    /// <item>a summon or a field;</item>
    /// <item>otherwise a random spell that suits.</item>
    /// </list>
    /// Inside a role the strongest (most mana) spell is preferred, except a heal
    /// that is not yet urgent, which takes the cheapest. Harmful spells are never
    /// aimed at a reflecting target. Returns the spell and the character to cast
    /// it on (a heal goes to the wounded one, everything harmful to the enemy).
    /// </summary>
    internal (SpellType Spell, Character CastTarget) ChooseBestSpell(Character npc, Character target, int dist)
    {
        var spells = npc.NpcSpells;
        if (spells.Count == 0)
            return (SpellType.None, target);
        bool targetReflects = target.IsStatFlag(StatFlag.Reflection);
        int healThreshold = _config.NpcHealThreshold > 0 ? _config.NpcHealThreshold : 30;

        var cures = new List<SpellType>();
        var heals = new List<SpellType>();
        var dispels = new List<SpellType>();
        var area = new List<SpellType>();
        var damage = new List<SpellType>();
        var poisons = new List<SpellType>();
        var curses = new List<SpellType>();
        var summons = new List<SpellType>();
        var fields = new List<SpellType>();
        foreach (var s in spells)
        {
            if (s == SpellType.None || !CanAffordNpcSpell(npc, s))
                continue;
            var f = SmartSpellFlags(s);
            if (IsCureSpell(s)) cures.Add(s);
            else if (f.HasFlag(SpellFlag.Heal) && f.HasFlag(SpellFlag.Good)) heals.Add(s);
            else if (IsDispelSpell(s)) dispels.Add(s);
            else if (f.HasFlag(SpellFlag.Summon)) summons.Add(s);
            else if (f.HasFlag(SpellFlag.Field)) fields.Add(s);
            else if (f.HasFlag(SpellFlag.Harm) && f.HasFlag(SpellFlag.Area)) area.Add(s);
            else if (f.HasFlag(SpellFlag.Harm) && f.HasFlag(SpellFlag.Damage)) damage.Add(s);
            else if (s == SpellType.Poison || (f.HasFlag(SpellFlag.Harm) && f.HasFlag(SpellFlag.Tick))) poisons.Add(s);
            else if (f.HasFlag(SpellFlag.Harm) && s != SpellType.Paralyze) curses.Add(s);
        }
        bool combatFlag = GetNpcFlags(npc).HasFlag(NpcAIFlags.Combat);

        // 1. Cure itself when poisoned (half the time, so a re-poisoning does not
        //    lock it into curing).
        if (cures.Count > 0 && npc.IsPoisoned && _rand.Next(2) == 0)
            return (PickByMana(cures, strongest: false), npc);

        // 2. Heal itself under the heal threshold (a flat one in three, so a hurt
        //    caster still spends most casts on offense; below half the threshold
        //    the strongest heal), then a wounded or poisoned ally. Without
        //    NPC_AI_COMBAT the reference's friend list is the caster alone
        //    (CCharNPCAct_Magic.cpp:322).
        if (heals.Count > 0 && npc.MaxHits > 0 && npc.Hits * 100 < npc.MaxHits * healThreshold && _rand.Next(3) == 0)
            return (PickByMana(heals, strongest: npc.Hits * 200 < npc.MaxHits * healThreshold), npc);
        if (combatFlag && (heals.Count > 0 || cures.Count > 0) && _rand.Next(2) == 0)
        {
            var ally = FindWoundedAlly(npc);
            if (ally != null)
            {
                if (ally.IsPoisoned && cures.Count > 0)
                    return (PickByMana(cures, strongest: false), ally);
                if (heals.Count > 0)
                    return (PickByMana(heals, strongest: ally.MaxHits > 0 && ally.Hits < ally.MaxHits / 4), ally);
            }
        }

        // 3. Dispel a summoned creature that is attacking - the target, or any
        //    other attacker in reach and in sight.
        if (dispels.Count > 0)
        {
            var summoned = FindSummonedAttacker(npc, target);
            if (summoned != null)
            {
                var single = dispels.FindAll(d => !SmartSpellFlags(d).HasFlag(SpellFlag.Area));
                return (PickByMana(single.Count > 0 ? single : dispels, strongest: false), summoned);
            }
        }

        // 4. Counter a heal: the enemy is casting a HEAL spell - a poison stops it
        //    healing, a curse weakens what it gains.
        if (!targetReflects && target.TryGetCastingSpell(out var enemySpell) &&
            SmartSpellFlags(enemySpell).HasFlag(SpellFlag.Heal))
        {
            if (!target.IsPoisoned && poisons.Count > 0)
                return (PickByMana(poisons, strongest: true), target);
            if (curses.Count > 0)
                return (PickByMana(curses, strongest: true), target);
        }

        // 5. The paralyze-then-burst combo, and a paralyze on a target that keeps
        //    its distance (never spammed: a per-caster cooldown lets damage land
        //    between locks, also against a target that breaks free at once).
        var combo = NextComboSpell(npc, target, targetReflects);
        if (combo != SpellType.None && CanAffordNpcSpell(npc, combo))
            return (combo, target);
        if (dist > 4 && spells.Contains(SpellType.Paralyze) && CanAffordNpcSpell(npc, SpellType.Paralyze) &&
            !targetReflects && !target.IsStatFlag(StatFlag.Freeze))
        {
            var mem = FightMemory(npc);
            long now = Environment.TickCount64;
            if (now >= mem.ParalyzeReadyAt)
            {
                mem.ParalyzeReadyAt = now + ParalyzeRecastCooldownMs;
                return (SpellType.Paralyze, target);
            }
        }

        // 6. Area spells when three or more foes stand around the target.
        if (area.Count > 0 && !targetReflects)
        {
            int nearbyEnemies = 0;
            foreach (var ch in _world.GetCharsInRange(target.Position, 3))
            {
                if (ch == npc || ch.IsDead || !IsAttackable(ch)) continue;
                if (GetHostilityLevel(npc, ch) > 0)
                    nearbyEnemies++;
            }
            if (nearbyEnemies >= 3)
                return (PickByMana(area, strongest: true), target);
        }

        // 7. Poison a target that is not poisoned yet, direct damage, a curse.
        if (!targetReflects)
        {
            if (!target.IsPoisoned && poisons.Count > 0 && _rand.Next(3) == 0)
                return (PickByMana(poisons, strongest: true), target);
            if (damage.Count > 0)
                return (PickByMana(damage, strongest: true), target);
            if (curses.Count > 0 && _rand.Next(4) == 0)
                return (curses[_rand.Next(curses.Count)], target);
        }

        // 8. Summon or field at the enemy's position (the reference keeps the enemy
        //    as the spell's target point, CCharNPCAct_Magic.cpp:265-267).
        if (summons.Count > 0)
            return (PickByMana(summons, strongest: true), target);
        if (fields.Count > 0 && !targetReflects)
            return (PickByMana(fields, strongest: true), target);

        // 9. Fallback: a random spell that suits.
        int startIdx = _rand.Next(spells.Count);
        for (int i = 0; i < spells.Count; i++)
        {
            var spell = spells[(startIdx + i) % spells.Count];
            if (spell == SpellType.None) continue;
            if (TryResolveNpcSpellTarget(npc, target, spell, targetReflects, out Character castTarget))
                return (spell, castTarget);
        }

        return (SpellType.None, target);
    }

    /// <summary>A summoned creature fighting this NPC: the target itself, else the
    /// closest summoned attacker in sight within the reference's sight range.</summary>
    private Character? FindSummonedAttacker(Character npc, Character target)
    {
        if (target.IsSummoned && !target.IsDead)
            return target;
        Character? best = null;
        int bestDist = int.MaxValue;
        foreach (var rec in npc.Attackers)
        {
            var ch = _world.FindChar(rec.Uid);
            if (ch == null || ch == target || ch.IsDead || ch.IsDeleted || !ch.IsSummoned) continue;
            if (ch.MapIndex != npc.MapIndex) continue;
            int d = npc.Position.GetDistanceTo(ch.Position);
            if (d > UoMapViewSight || d >= bestDist) continue;
            if (!_world.CanSeeLOS(npc.Position, ch.Position)) continue;
            best = ch;
            bestDist = d;
        }
        return best;
    }

    /// <summary>Populate an NPC's spell list from any carried/equipped spellbook
    /// (Source-X NPC_AddSpellsFromBook). The book's itemdef carries the spell
    /// range: TDATA3 = first-spell offset, TDATA4 = max spells. Spell (offset+1+i)
    /// is present when bit i is set in the book's More1:More2 mask (bits 0-31 in
    /// More1, 32-63 in More2 — Source-X CItem::IsSpellInBook). This covers
    /// necro/chivalry/mysticism/spellweaving books, not just the classic 64-bit
    /// magery book. Tried once per NPC; the "already tried" mark is kept in
    /// memory (it used to be TAG.SPELLS_LOADED, which a save carried along).
    /// There is no body-based default list: Source-X spells come from the SPELLS
    /// list or a book only (CCharNPCAct_Magic.cpp:100-144).</summary>
    internal static void EnsureNpcSpellsFromBook(Character npc)
    {
        if (_spellbookScanned.TryGetValue(npc, out _)) return;
        _spellbookScanned.AddOrUpdate(npc, _spellbookScannedMark);

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
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Character, object> _spellbookScanned = new();
    private static readonly object _spellbookScannedMark = new();

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

    /// <summary>An equipped wand (IT_WAND) that can still cast: it carries ATTR_MAGIC
    /// and has charges left in MORE2 (Source-X NPC_FightMagery,
    /// CCharNPCAct_Magic.cpp:166). Its spell is MOREX.</summary>
    internal static Item? FindNpcWand(Character npc)
    {
        // Source-X only inspects LAYER_HAND1 (a HAND2-held wand is ignored).
        var held = npc.GetEquippedItem(Layer.OneHanded);
        if (held == null || held.ItemType != ItemType.Wand || SphereNet.Game.Magic.SpellEngine.MagicItemSpell(held) == SpellType.None)
            return null;
        if (!held.Attributes.HasFlag(ObjAttributes.Magic))
            return null;
        if (!SphereNet.Game.Magic.SpellEngine.WandHasCharge(held))
            return null;
        return held;
    }

    private static void ConsumeWandCharge(Item wand) => SphereNet.Game.Magic.SpellEngine.ConsumeWandCharge(wand);

    /// <summary>SmartCaster spell combo (in memory, see <see cref="NpcFightMemory"/>):
    /// step 0 may start a combo (Paralyze) when mana is high and the target is
    /// free; later steps unload damage while the target is locked. Returns None
    /// when no combo applies.</summary>
    private SpellType NextComboSpell(Character npc, Character target, bool targetReflects)
    {
        if (targetReflects) { ResetNpcCombo(npc); return SpellType.None; }
        var spells = npc.NpcSpells;
        var mem = FightMemory(npc);
        int step = mem.ComboStep;

        // Abandon a combo aimed at a different target.
        if (step > 0 && mem.ComboTarget != target.Uid.Value)
        {
            ResetNpcCombo(npc);
            step = 0;
        }

        if (step > 0)
        {
            // Advance the chain. Each step falls through to the next available
            // spell so a missing spell doesn't stall the combo.
            mem.ComboStep = 0;
            if (step <= 1)
            {
                if (spells.Contains(SpellType.Explosion)) { mem.ComboStep = 2; return SpellType.Explosion; }
                step = 2;
            }
            if (step <= 2)
            {
                if (spells.Contains(SpellType.EnergyBolt)) { mem.ComboStep = 3; return SpellType.EnergyBolt; }
                step = 3;
            }
            mem.ComboTarget = 0;
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
            mem.ComboStep = 1;
            mem.ComboTarget = target.Uid.Value;
            return SpellType.Paralyze;
        }
        return SpellType.None;
    }

    // Special creature actions (Source-X Action_StartSpecial, CCharNPCAct.cpp:65-134):
    // the fire elemental lays a fire path, the giant spider (or whatever
    // OVERRIDE.SPIDERWEB turns into one) a web. Started from the idle pass only
    // (NPC_Act_Idle, :1987-2024), never on every tick.
    private const ushort GiantSpiderBody = 0x001C;    // CREID_GIANT_SPIDER

    private const ushort FireElementalBody = 0x000F;  // CREID_FIRE_ELEM

    private const ushort FireEwId = 0x398C;           // ITEMID_FX_FIRE_F_EW

    private const ushort FireNsId = 0x3996;           // ITEMID_FX_FIRE_F_NS

    private const ushort WebFirstId = 0x0EE3;         // ITEMID_WEB1_1

    private const ushort WebLastId = 0x0EE6;          // ITEMID_WEB1_4

    /// <summary>Source-X Action_StartSpecial. Fire: ITEMID_FX_FIRE_F_EW or _NS at
    /// random, IT_FIRE carrying Fire Field at heat 100 + rand(500), linked to the
    /// creature, gone after 10 ms + rand(50) s. Web: one of ITEMID_WEB1_1..4, IT_WEB,
    /// gone after 10 ms + rand(170) s. Either costs 5 + rand(5) stamina, and the
    /// creature plays the area-cast animation.</summary>
    internal void ActStartSpecial(Character npc, bool fire)
    {
        if (npc.IsDead) return;

        OnNpcAnimate?.Invoke(npc, AnimationType.CastArea);

        var item = _world.CreateItem();
        int maxTimeoutS;
        if (fire)
        {
            item.BaseId = _rand.Next(2) != 0 ? FireEwId : FireNsId;
            item.ItemType = ItemType.Fire;
            // m_itSpell: spell = MOREX, level = MOREY, charges = MORE2
            // (CCharNPCAct.cpp:89-91; CItem.h:255-257).
            item.MoreP = new Point3D((short)SpellType.FireField, (short)(100 + _rand.Next(500)), 0, npc.MapIndex);
            item.More2 = 1;
            item.Link = npc.Uid;
            maxTimeoutS = 50;
        }
        else
        {
            item.BaseId = (ushort)_rand.Next(WebFirstId, WebLastId + 1);
            item.ItemType = ItemType.Web;
            maxTimeoutS = 170;
        }

        item.SetDecayAt(Environment.TickCount64 + 10 + _rand.Next(maxTimeoutS) * 1000L);
        if (!_world.PlaceItem(item, npc.Position))
            _world.RemoveItem(item);

        npc.Stam = (short)Math.Max(0, npc.Stam - (5 + _rand.Next(5)));
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

    /// <summary>True when the NPC carries a throwable rock (Source-X
    /// ContentFind(RES_TYPEDEF IT_AROCK, 0, 2), CCharNPCAct_Fight.cpp:327).
    /// <paramref name="acceptPlainRock"/> (NPCAIEXTRAS CombatExtras) also lets a
    /// plain IT_ROCK pile arm a default thrower.</summary>
    internal static bool HasThrowableRock(Character npc, bool acceptPlainRock = false) =>
        FindCarried(npc, it => it.ItemType == ItemType.ARock ||
                               (acceptPlainRock && it.ItemType == ItemType.Rock)) != null;

    /// <summary>True when the NPC carries an item of the THROWOBJ definition
    /// (Source-X ContentFind(RES_ITEMDEF obj, 0, 2), CCharNPCAct_Fight.cpp:317-323):
    /// a creature that has run out of its missiles stops throwing.</summary>
    internal static bool CarriesThrowObj(Character npc)
    {
        long id = ReadSpecialTagNumber(npc, "THROWOBJ", resolveItemDef: true);
        if (id <= 0)
            return false;
        return FindCarried(npc, it => it.BaseId == (ushort)id) != null;
    }

    /// <summary>Source-X CContainer::ContentFind with two levels of descent over a
    /// character: what it wears, and what is inside its containers two levels
    /// deep.</summary>
    private static Item? FindCarried(Character npc, Func<Item, bool> match)
    {
        for (int layer = 1; layer < (int)Layer.Qty; layer++)
        {
            var worn = npc.GetEquippedItem((Layer)layer);
            if (worn == null || worn.IsDeleted) continue;
            if (match(worn)) return worn;
            var inner = FindInContents(worn, match, 2);
            if (inner != null) return inner;
        }
        var pack = npc.Backpack;
        if (pack != null && npc.GetEquippedItem(Layer.Pack) != pack)
            return FindInContents(pack, match, 2);
        return null;
    }

    private static Item? FindInContents(Item container, Func<Item, bool> match, int levels)
    {
        if (levels <= 0) return null;
        foreach (var it in container.Contents)
        {
            if (it.IsDeleted) continue;
            if (match(it)) return it;
            if (it.Contents.Count > 0)
            {
                var inner = FindInContents(it, match, levels - 1);
                if (inner != null) return inner;
            }
        }
        return null;
    }

    /// <summary>Source-X Skill_Act_Breath: an explicit BREATH.DAM tag is used
    /// UNCLAMPED (script authority); the default is 5% of the CURRENT hit points
    /// (Stat_GetVal(STAT_STR), CCharSkill.cpp:3307-3313), clamped 1-65535 - a
    /// wounded dragon breathes weaker.</summary>
    private static int GetBreathDamage(Character npc)
    {
        // A zero BREATH.DAM means "use the default" (if (!iDamage), CCharSkill.cpp:3307).
        long custom = ReadSpecialTagNumber(npc, "BREATH.DAM", resolveItemDef: false);
        if (custom != 0)
            return (int)Math.Clamp(custom, 1, int.MaxValue);
        int dmg = npc.Hits * 5 / 100;
        return Math.Clamp(dmg, 1, ushort.MaxValue);
    }

    /// <summary>What a breath looks like and burns with (Source-X Skill_Act_Breath,
    /// CCharSkill.cpp:3316-3341): BREATH.ANIM (default ITEMID_FX_FIRE_BALL 0x36D4),
    /// BREATH.TYPE (EFFECT_*, default EFFECT_BOLT 0), BREATH.HUE, and
    /// BREATH.DAMTYPE (default DAMAGE_FIRE); the elemental split puts 100% on the
    /// first element the type names (fire, cold, poison, energy), else physical.</summary>
    public static (byte Motion, ushort Gfx, ushort Hue, DamageType DamageType,
        int Physical, int Fire, int Cold, int Poison, int Energy) ResolveBreath(Character npc)
    {
        ushort gfx = (ushort)ReadSpecialTagNumber(npc, "BREATH.ANIM", resolveItemDef: true);
        if (gfx == 0) gfx = 0x36D4;
        byte motion = (byte)ReadSpecialTagNumber(npc, "BREATH.TYPE", resolveItemDef: false);
        ushort hue = (ushort)ReadSpecialTagNumber(npc, "BREATH.HUE", resolveItemDef: false);
        var type = (DamageType)(ushort)ReadSpecialTagNumber(npc, "BREATH.DAMTYPE", resolveItemDef: false);
        if (type == 0) type = DamageType.Fire;
        int phys = 0, fire = 0, cold = 0, poison = 0, energy = 0;
        if (type.HasFlag(DamageType.Fire)) fire = 100;
        else if (type.HasFlag(DamageType.Cold)) cold = 100;
        else if (type.HasFlag(DamageType.Poison)) poison = 100;
        else if (type.HasFlag(DamageType.Energy)) energy = 100;
        else phys = 100;
        return (motion, gfx, hue, type, phys, fire, cold, poison, energy);
    }

    /// <summary>What an NPC throws (Source-X Skill_Act_Throwing,
    /// CCharSkill.cpp:3446-3462): the THROWOBJ item's graphic, else a random
    /// boulder (ITEMID_ROCK_B 0x134F-0x1361) two times in three or a small rock
    /// (ITEMID_ROCK_2 0x1363-0x136C) otherwise.</summary>
    public static ushort ResolveThrowGraphic(Character npc)
    {
        if (npc.TryGetTag("THROWOBJ", out string? obj) && !string.IsNullOrWhiteSpace(obj))
        {
            ushort gfx = (ushort)ReadSpecialTagNumber(npc, "THROWOBJ", resolveItemDef: true);
            if (gfx != 0) return gfx;
        }
        return _rand.Next(3) != 0
            ? (ushort)(0x134F + _rand.Next(0x1362 - 0x134F))
            : (ushort)(0x1363 + _rand.Next(0x136D - 0x1363));
    }

    private static long ReadSpecialTagNumber(Character npc, string key, bool resolveItemDef)
    {
        if (!npc.TryGetTag(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return 0;
        string s = raw.Trim();
        if (resolveItemDef && (char.IsLetter(s[0]) || s[0] == '_'))
            return Item.ResolveDefName?.Invoke(s) ?? 0;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out long h) ? h : 0;
        if (s.Length > 1 && s[0] == '0')
            return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out long h0) ? h0 : 0;
        return long.TryParse(s, out long d) ? d : 0;
    }

    /// <summary>Minimum gap between a SmartCaster's Paralyze re-casts on its target
    /// (ChooseBestSpell role 5). Stops the lock-down rule from firing ahead of
    /// the damage spells every tick — including against a target that breaks
    /// free instantly (trapped pouch) — so the caster actually deals damage
    /// between locks instead of looping on Paralyze.</summary>
    private const int ParalyzeRecastCooldownMs = 12000;

    /// <summary>Observer/test fallback for an accepted NPC cast. Production
    /// uses OnNpcTryStartSpellCast so SpellEngine can report rejection.</summary>
    public Action<Character, Character, SpellType>? OnNpcCastSpell { get; set; }

    /// <summary>Start an NPC spell and report whether SpellEngine accepted it.
    /// The bool prevents rejected casts from spending wand charges or advancing
    /// combat state. OnNpcCastSpell remains as the observer/test fallback.</summary>
    public Func<Character, Character, SpellType, bool>? OnNpcTryStartSpellCast { get; set; }

    /// <summary>Resolve loaded spell flags for safe fallback targeting.</summary>
    public Func<SpellType, SpellFlag?>? ResolveNpcSpellFlags { get; set; }

    /// <summary>A spell's [SPELL] MANAUSE, for the SmartCaster ranking (-1 = unknown).</summary>
    public Func<SpellType, int>? ResolveNpcSpellMana { get; set; }

    /// <summary>The layer a spell's effect memory is worn on (SPELL LAYER=), so an
    /// NPC does not recast a blessing it already carries.</summary>
    public Func<SpellType, Layer>? ResolveNpcSpellLayer { get; set; }

    /// <summary>Advance an NPC's in-progress spell cast timer. Returns true while still casting.</summary>
    public Func<Character, bool>? OnNpcTickSpellCast { get; set; }
}
