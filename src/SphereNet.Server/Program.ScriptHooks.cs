using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.Scripting;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Server;

public static partial class Program
{
    // Reinstall startup-gated callbacks after RESYNC as well: Source-X checks
    // IsTrigUsed at runtime. Removed hooks must return to the cheap null path.
    private static void RefreshScriptTriggerHooks()
    {
        _triggerDispatcher?.BuildUsedTriggerCache();
        RefreshCharacterScriptHooks();
        RefreshNpcScriptHooks();
    }

    private static void RefreshCharacterScriptHooks()
    {
        Character.OnNotoSend = null;
        Character.OnEffectAdd = null;
        Character.OnRevealing = null;
        Character.OnSpellEffectAdd = null;
        Character.OnSpellEffectRemove = null;
        Character.OnSpellEffectTick = null;
        CharacterPoisonState.OnSpellEffectAdd = null;
        Character.OnMemoryEquip = null;
        Character.OnSkillUseQuickDetailed = null;
        Character.OnNpcSeeNewPlayer = null;
        Character.OnPersonalSpace = null;
        SphereNet.Game.Housing.CustomHousingEngine.KeepCommitItem = null;
        Character.OnCharShove = null;
        Character.OnAfkMode = null;
        Character.OnSeeHidden = null;
        Character.OnFollowersUpdate = null;
        SphereNet.Game.Trade.VendorEngine.OnPayGold = null;
        SphereNet.Game.Housing.HousingEngine.OnDelMulti = null;
        Character.OnPetDesert = null;
        Character.OnPetRelease = null;
        Character.OnJailed = null;
        Character.OnEnvironChange = null;
        Character.OnRegenStat = null;
        Character.OnArrowQuest = null;
        SkillEngine.OnSkillGainCheck = null;
        Character.OnSkillChange = null;
        if (_triggerDispatcher == null) return;
        // @RegenStat — per-stat regeneration hook; unhooked it stays a null check
        // on the character tick (Source-X IsTrigUsed(TRIGGER_REGENSTAT)).
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.RegenStat))
            Character.OnRegenStat = _triggerDispatcher.FireRegenStat;
        // @ArrowQuest_Add / @ArrowQuest_Close — the ARROWQUEST verb.
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.ArrowQuestAdd) ||
            _triggerDispatcher.IsCharTriggerUsed(CharTrigger.ArrowQuestClose))
            Character.OnArrowQuest = _triggerDispatcher.FireArrowQuest;
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NotoSend))
        {
            SphereNet.Game.Objects.Characters.Character.OnNotoSend = (viewer, subject, noto) =>
            {
                // ARGN1 starts as NOTO_INVALID (0); a script that leaves it there gets
                // the computed notoriety (CCharNotoriety.cpp:120-131).
                var args = new TriggerArgs { CharSrc = viewer, N1 = 0 };
                _triggerDispatcher.FireCharTrigger(subject, CharTrigger.NotoSend, args);
                return args.N1 == 0 ? noto : (byte)Math.Clamp(args.N1, 0, 255);
            };
        }
        // <NOTOGETFLAG uid> script property → full Noto_GetFlag of the subject
        // as seen by the given viewer.
        SphereNet.Game.Objects.Characters.Character.ResolveNotoFlag =
            (subject, viewer) => GameClient.ComputeNotoriety(_world, viewer, subject);
        // @EffectAdd — fired per applied buff; gate to a null check when unhooked.
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.EffectAdd))
        {
            SphereNet.Game.Objects.Characters.Character.OnEffectAdd = (target, spellId) =>
                _triggerDispatcher.FireCharTrigger(target, CharTrigger.EffectAdd,
                    new TriggerArgs { CharSrc = target, N1 = spellId });
        }
        // [SPELL 20] @EffectAdd on the poison memory as it is worn (Source-X
        // Spell_Effect_Add, CCharSpell.cpp:1000): ARGO = the memory, SRC = the
        // poisoner, ARGN1 = the spell. The other effects run the stage from
        // SpellEngine; poison makes its memory outside the spell engine.
        if (_triggerDispatcher.IsTriggerNameUsed("EffectAdd"))
        {
            CharacterPoisonState.OnSpellEffectAdd = (owner, memory, caster) =>
                _triggerDispatcher.FireSpellTrigger(SpellType.Poison, "EffectAdd", owner,
                    new TriggerArgs { CharSrc = caster ?? owner, N1 = (int)SpellType.Poison, O1 = memory });
        }
        // @Reveal — fired before hidden/invisible state drops; RETURN 1
        // keeps the character concealed (Source-X CChar::Reveal).
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.Reveal))
        {
            SphereNet.Game.Objects.Characters.Character.OnRevealing = ch =>
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.Reveal,
                    new TriggerArgs { CharSrc = ch }) != TriggerResult.True;
        }
        // @SpellEffectAdd / @SpellEffectRemove — timed buff lifecycle
        // (Source-X CCharSpell). ARGN1 = spell id; SRC = caster on add.
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SpellEffectAdd))
        {
            SphereNet.Game.Objects.Characters.Character.OnSpellEffectAdd = (target, caster, spellId) =>
                _triggerDispatcher.FireCharTrigger(target, CharTrigger.SpellEffectAdd,
                    new TriggerArgs { CharSrc = caster ?? target, N1 = spellId });
        }
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SpellEffectRemove))
        {
            SphereNet.Game.Objects.Characters.Character.OnSpellEffectRemove = (target, spellId) =>
                _triggerDispatcher.FireCharTrigger(target, CharTrigger.SpellEffectRemove,
                    new TriggerArgs { CharSrc = target, N1 = spellId });
        }
        // @SpellEffectTick — Source-X SPELLFLAG_TICK bridge on the native
        // poison tick (the era's only TICK consumer). Script contract:
        // ARGN1 = spell id, ARGN2 = strength, ARGO = memory shim (BASEID =
        // the spell's RUNE_ITEM, MOREY = strength, LINK = poisoner);
        // LOCAL.EFFECT/DELAY/CHARGES/DAMAGETYPE seeded, script writes read
        // back from the shared pool; RETURN 1 destroys the effect (cure).
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SpellEffectTick) ||
            _triggerDispatcher.IsTriggerNameUsed("EffectTick"))
        {
            SphereNet.Game.Objects.Characters.Character.OnSpellEffectTick = (victim, ctx) =>
            {
                var locals = new SphereNet.Scripting.Variables.VarMap();
                locals.Set("EFFECT", ctx.Damage.ToString());
                locals.Set("DELAY", (ctx.DelayMs / 1000.0).ToString(
                    "0.###", System.Globalization.CultureInfo.InvariantCulture));
                locals.Set("CHARGES", ctx.Charges.ToString());
                locals.Set("DAMAGETYPE", "08"); // dam_poison
                var spellDef = _spellEngine?.GetSpellDef((SpellType)ctx.SpellId);
                // ARGO is the effect's memory item itself (upstream pItem); the shim
                // stands in only for an effect that has no real memory.
                SphereNet.Core.Interfaces.IScriptObj memory = (SphereNet.Core.Interfaces.IScriptObj?)ctx.Memory ?? new SpellMemoryShim
                {
                    SpellId = ctx.SpellId,
                    BaseId = spellDef?.RuneItemId ?? 0,
                    MoreY = ctx.Strength,
                    LinkUid = ctx.SourceUid.IsValid ? ctx.SourceUid.Value : 0,
                    Name = spellDef?.Name ?? "poison",
                };
                var args = new TriggerArgs
                {
                    CharSrc = victim,
                    N1 = ctx.SpellId,
                    N2 = ctx.Strength,
                    O1 = memory,
                    Locals = locals,
                };
                if (_triggerDispatcher.FireCharTrigger(victim, CharTrigger.SpellEffectTick, args)
                    == TriggerResult.True)
                    return false;
                // [SPELL n] @EffectTick resource-section stage (Source-X
                // SPTRIG_EFFECTTICK) — shares the same args/LOCAL pool, so
                // a section script can adjust EFFECT/DELAY/CHARGES too.
                if (_triggerDispatcher.FireSpellTrigger((SpellType)ctx.SpellId, "EffectTick",
                        victim, args) == TriggerResult.True)
                    return false;
                ctx.Damage = (int)locals.GetInt("EFFECT", ctx.Damage);
                ctx.Charges = (int)locals.GetInt("CHARGES", ctx.Charges);
                // ARGN2 is read back as the level too (Source-X CCharSpell.cpp:2011
                // iLevel = m_iN2) - a script caps a lethal poison with ARGN2=3.
                ctx.Strength = SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N2);
                if (double.TryParse(locals.Get("DELAY"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double delaySec) && delaySec > 0)
                    ctx.DelayMs = (int)(delaySec * 1000);
                return true;
            };
        }
        // @MemoryEquip — memory items are created frequently in combat; install
        // the fire only when a script hooks the item trigger (item IsTrigUsed gate).
        if (_triggerDispatcher.IsItemTriggerUsed(ItemTrigger.MemoryEquip))
        {
            SphereNet.Game.Objects.Characters.Character.OnMemoryEquip = mem =>
                _triggerDispatcher.FireItemTrigger(mem, ItemTrigger.MemoryEquip,
                    new TriggerArgs { ItemSrc = mem });
        }
        // @SkillUseQuick — fires per quick skill check; install only when hooked
        // (IsTrigUsed gate). N1 = skill, N2 = difficulty; RETURN 1 cancels the use.
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SkillUseQuick) ||
            _triggerDispatcher.IsTriggerNameUsed("UseQuick"))
        {
            // N1 = skill, N2 = difficulty, N3 = rolled result (1/0). RETURN 1
            // cancels the use; otherwise ARGN3 is read back as the final result.
            SphereNet.Game.Objects.Characters.Character.OnSkillUseQuickDetailed =
                (SphereNet.Game.Objects.Characters.Character ch, int skillId, ref int difficulty, int result) =>
            {
                var args = new TriggerArgs { CharSrc = ch, N1 = skillId, N2 = difficulty, N3 = result };
                var triggerResult = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillUseQuick, args);
                difficulty = SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N2);
                // RETURN 1 = success, RETURN 0 = failure, both without experience;
                // otherwise ARGN3 is the result (CCharSkill.cpp:566-579).
                if (triggerResult == TriggerResult.True)
                    return SphereNet.Game.Skills.SkillEngine.UseQuickHandledSuccess;
                if (triggerResult == TriggerResult.False) return -1;
                return args.N3 != 0 ? 1 : 0;
            };
        }
        // @NPCSeeNewPlayer — install only when hooked so the per-NPC perception
        // scan is skipped entirely otherwise. O1 = the newly-seen player.
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCSeeNewPlayer))
        {
            SphereNet.Game.Objects.Characters.Character.OnNpcSeeNewPlayer = (npc, player) =>
                _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCSeeNewPlayer,
                    new TriggerArgs { CharSrc = npc, O1 = player });
        }

        // @PetDesert — fired on the pet when loyalty hits zero; RETURN 1 cancels
        // the desertion. SRC is the owner (OnTrigger(CTRIG_PetDesert, args, pCharOwn),
        // CCharNPCPet.cpp:914); O1 = owner (may be null if it could not be resolved).
        SphereNet.Game.Objects.Characters.Character.OnPetDesert = (pet, owner) =>
            _triggerDispatcher.FireCharTrigger(pet, CharTrigger.PetDesert,
                new TriggerArgs { CharSrc = owner ?? pet, O1 = owner }) == TriggerResult.True;

        // @PersonalSpace on the one walked into (SRC = mover), @charShove on the mover
        // (SRC = the one in the way); RETURN 1 keeps the mover out.
        SphereNet.Game.Objects.Characters.Character.OnPersonalSpace = (blocker, mover) =>
            _triggerDispatcher.FireCharTrigger(blocker, CharTrigger.PersonalSpace,
                new TriggerArgs { CharSrc = mover }) == TriggerResult.True;
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.charShove))
            SphereNet.Game.Objects.Characters.Character.OnCharShove = (mover, blocker) =>
                _triggerDispatcher.FireCharTrigger(mover, CharTrigger.charShove,
                    new TriggerArgs { CharSrc = blocker }) == TriggerResult.True;

        // @AfkMode — ARGN1 current, ARGN2 requested, both read back; RETURN 1 cancels.
        SphereNet.Game.Objects.Characters.Character.OnAfkMode = (ch, afk, mode) =>
        {
            var afkArgs = new TriggerArgs { CharSrc = ch, N1 = afk ? 1 : 0, N2 = mode ? 1 : 0 };
            bool cancel = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.AfkMode, afkArgs) == TriggerResult.True;
            return (cancel, afkArgs.N1 > 0, afkArgs.N2 > 0);
        };

        // @SeeHidden — asked for every hidden character in view, so only when hooked.
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SeeHidden))
            SphereNet.Game.Objects.Characters.Character.OnSeeHidden = (viewer, hidden, n1) =>
            {
                var seeArgs = new TriggerArgs { CharSrc = hidden, N1 = n1 };
                _triggerDispatcher.FireCharTrigger(viewer, CharTrigger.SeeHidden, seeArgs);
                return seeArgs.N1;
            };

        // @FollowersUpdate — on the owner, SRC = the pet; RETURN 1 refuses an addition.
        SphereNet.Game.Objects.Characters.Character.OnFollowersUpdate = (owner, pet, adding, slots) =>
            _triggerDispatcher.FireCharTrigger(owner, CharTrigger.FollowersUpdate,
                new TriggerArgs { CharSrc = pet, N1 = adding ? 0 : 1, N2 = Math.Abs(slots) }) == TriggerResult.True;

        // @PayGold — on the payer, SRC = who is paid; ARGN1 (the amount) is read back.
        SphereNet.Game.Trade.VendorEngine.OnPayGold = (payer, payee, amount, reason) =>
        {
            var payArgs = new TriggerArgs { CharSrc = payee, N1 = amount, N2 = reason };
            _triggerDispatcher.FireCharTrigger(payer, CharTrigger.PayGold, payArgs);
            return payArgs.N1;
        };

        // @HouseDesignCommitItem — per piece at commit; RETURN 0 leaves it out.
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.HouseDesignCommitItem))
            SphereNet.Game.Housing.CustomHousingEngine.KeepCommitItem = (ch, multi, tile) =>
            {
                var locals = new SphereNet.Scripting.Variables.VarMap();
                locals.SetInt("ID", tile.TileId);
                locals.SetInt("P.X", tile.X);
                locals.SetInt("P.Y", tile.Y);
                locals.SetInt("P.Z", tile.Z);
                locals.SetInt("VISIBLE", tile.Visible ? 1 : 0);
                var itemArgs = new TriggerArgs { CharSrc = ch, O1 = multi, Locals = locals };
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.HouseDesignCommitItem, itemArgs);
                return itemArgs.ReturnNumber != 0;
            };

        // @DelMulti — on the owner a house leaves; ARGO1 = the multi, ARGN3 = 1 (owner).
        SphereNet.Game.Housing.HousingEngine.OnDelMulti = (owner, multi) =>
            _triggerDispatcher.FireCharTrigger(owner, CharTrigger.DelMulti,
                new TriggerArgs { CharSrc = owner, O1 = multi, N1 = 1, N2 = 1, N3 = 1 });

        // @PetRelease — fired on the pet with its owner as SRC before it is let go;
        // RETURN 1 keeps it (NPC_PetRelease, CCharNPCPet.cpp:870).
        SphereNet.Game.Objects.Characters.Character.OnPetRelease = (pet, owner) =>
            _triggerDispatcher.FireCharTrigger(pet, CharTrigger.PetRelease,
                new TriggerArgs { CharSrc = owner, O1 = owner }) == TriggerResult.True;

        // @Jail — fired on a character sent to jail. N1 = sentence minutes (0 = indefinite).
        SphereNet.Game.Objects.Characters.Character.OnJailed = (ch, minutes) =>
            _triggerDispatcher.FireCharTrigger(ch, CharTrigger.Jail,
                new TriggerArgs { CharSrc = ch, N1 = minutes });

        // @EnvironChange — fired when a character's perceived light level changes
        // (surface/dungeon boundary). N1 = new light level. Only fires on an
        // actual change (UpdateEnvironLight), which is infrequent.
        SphereNet.Game.Objects.Characters.Character.OnEnvironChange =
            _triggerDispatcher.FireEnvironChange;

        // @SkillGain (Source-X Skill_Experience) — pre-roll hook: fires before the
        // gain roll so a script can tune the gain chance (ARGN2) / effective cap
        // (ARGN3) or RETURN 1 to cancel the attempt. Installed only when hooked so
        // unscripted shards skip it on every gain attempt.
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SkillGain) ||
            _triggerDispatcher.IsTriggerNameUsed("Gain"))
        {
            SkillEngine.OnSkillGainCheck =
                (SphereNet.Game.Objects.Characters.Character ch, SkillType skill, ref int chance, ref int skillMax) =>
            {
                var args = new TriggerArgs { CharSrc = ch, N1 = (int)skill, N2 = chance, N3 = skillMax };
                bool cancel = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillGain, args) == TriggerResult.True;
                chance = SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N2);
                skillMax = SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N3);
                return cancel;
            };
        }

        // @SkillChange as a cancelable SETTER guard (Source-X) for RUNTIME value
        // changes — GM .ADDSKILL and script property assignment (MAGERY=80) — so a
        // script can adjust the new value (ARGN2) or RETURN 1 to veto it. Gated so
        // unscripted shards skip it; load/spawn/decay/gain use the raw setter and do
        // NOT fire here (gain keeps its own post @SkillChange notification below).
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SkillChange))
        {
            SphereNet.Game.Objects.Characters.Character.OnSkillChange =
                (SphereNet.Game.Objects.Characters.Character ch, SkillType skill, int oldVal, ref int newVal) =>
            {
                var args = new TriggerArgs { CharSrc = ch, N1 = (int)skill, N2 = newVal, N3 = newVal - oldVal };
                bool cancel = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillChange, args) == TriggerResult.True;
                newVal = SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N2);
                return cancel;
            };
        }


    }

    private static void RefreshNpcScriptHooks()
    {
        if (_npcAI == null) return;
        _npcAI.OnNpcLookAtChar = null;
        _npcAI.OnNpcActFight = null;
        _npcAI.OnNpcActWander = null;
        _npcAI.OnNpcActFollow = null;
        _npcAI.OnNpcActCast = null;
        _npcAI.OnNpcLookAtItem = null;
        _npcAI.OnNpcSeeWantItem = null;
        _npcAI.OnNpcAction = null;
        if (_triggerDispatcher == null) return;
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCLookAtChar))
            _npcAI.OnNpcLookAtChar = (npc, target) =>
                _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCLookAtChar,
                    new TriggerArgs { CharSrc = target, N1 = target.Uid.Value > int.MaxValue ? 0 : (int)target.Uid.Value }) == TriggerResult.True;
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCActFight))
            _npcAI.OnNpcActFight = (npc, target, dist, motivation) =>
            {
                // Source-X NPC_Act_Fight args: ARGN1=distance, ARGN2=motivation,
                // ARGO=target. The script may flip motivation (ARGN2 readback,
                // wired through RunWrapped) or force a cast via LOCAL.skill +
                // LOCAL.spell. RETURN 1 fully handles the action.
                var locals = new SphereNet.Scripting.Variables.VarMap();
                var args = new TriggerArgs
                {
                    CharSrc = target, O1 = target,
                    N1 = dist, N2 = motivation, Locals = locals
                };
                var res = _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCActFight, args);
                // RETURN 0 = fSkipHardcoded (CCharNPCAct_Fight.cpp:232-234): skip the
                // breath/throw specials, keep flee/magery/melee. The forced
                // LOCAL.skill / LOCAL.spell is read only when the trigger fell
                // through (TRIGRET_RET_DEFAULT, :235-250).
                bool skipHardcoded = res == TriggerResult.False;
                var forcedSkill = !skipHardcoded && locals.Has("skill")
                    ? (SkillType)(int)locals.GetInt("skill") : SkillType.None;
                var forcedSpell = !skipHardcoded && locals.Has("spell")
                    ? (SpellType)(int)locals.GetInt("spell") : SpellType.None;
                return new NpcAI.NpcFightDecision(
                    res == TriggerResult.True, SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N2), forcedSkill, forcedSpell, skipHardcoded);
            };
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCActWander))
            _npcAI.OnNpcActWander = npc =>
                _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCActWander,
                    new TriggerArgs { CharSrc = npc }) == TriggerResult.True;
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCActFollow))
            _npcAI.OnNpcActFollow = (npc, target, followArgs) =>
            {
                // Source-X NPC_Act_Follow args: ARGN1 = flee, ARGN2 = the distance
                // to keep, ARGN3 = move away, ARGO = the target - seeded before the
                // trigger and read back when it falls through (CCharNPCAct.cpp:1357).
                // The old adapter passed none of them and threw the answer away as
                // a bool.
                var args = new TriggerArgs
                {
                    CharSrc = target,
                    O1 = target,
                    N1 = followArgs.Flee ? 1 : 0,
                    N2 = followArgs.MaxDistance,
                    N3 = followArgs.MoveAway ? 1 : 0,
                };

                var result = _triggerDispatcher.FireCharTrigger(
                    npc, CharTrigger.NPCActFollow, args);
                if (result == TriggerResult.True)
                    return SphereNet.Game.AI.NpcAI.FollowTriggerResult.GiveUp;

                // NOTE: a script's RETURN 0 cannot be told from falling off the end
                // here - ScriptInterpreter maps zero onto Default - so only a native
                // handler can answer Handled today. Recorded as an open item rather
                // than pretended away.
                if (result == TriggerResult.False)
                    return SphereNet.Game.AI.NpcAI.FollowTriggerResult.Handled;

                followArgs.Flee = args.N1 != 0;
                followArgs.MaxDistance = SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N2);
                followArgs.MoveAway = args.N3 != 0;
                return SphereNet.Game.AI.NpcAI.FollowTriggerResult.Continue;
            };
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCActCast))
            _npcAI.OnNpcActCast = (npc, target, spell, wandUse) =>
            {
                // Source-X NPC_FightMagery args (CCharNPCAct_Magic.cpp:223-242):
                // ARGN1=spell, ARGN2=wand-use, ARGO=target, LOCAL.HealThreshold
                // seeded from config and read back. RETURN 1 aborts the cast and
                // reverts to melee; otherwise ARGN1 carries the (possibly
                // script-overridden) spell back, and REF1 = a character redirects
                // the cast and overrides the AI's own friend choice.
                var locals = new SphereNet.Scripting.Variables.VarMap();
                locals.SetInt("HealThreshold", _config.NpcHealThreshold);
                var refs = new Dictionary<int, string>();
                var args = new TriggerArgs
                {
                    CharSrc = target, O1 = target, N1 = (int)spell,
                    N2 = wandUse ? 1 : 0, Locals = locals, Refs = refs
                };
                var res = _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCActCast, args);
                if (res == TriggerResult.True)
                    return new NpcAI.NpcCastDecision(true, spell, target); // abort → melee
                var newSpell = (SpellType)args.N1;
                if (newSpell == SpellType.None) newSpell = spell;
                var newTarget = target;
                bool retargeted = false;
                if (refs.TryGetValue(1, out var ref1) && !string.IsNullOrWhiteSpace(ref1))
                {
                    var redirect = _world.FindChar(new Serial(SphereNet.Game.Objects.ObjBase.ParseHexOrDecUInt(ref1)));
                    if (redirect != null && !redirect.IsDeleted)
                    {
                        newTarget = redirect;
                        retargeted = true;
                    }
                }
                int healThreshold = locals.Has("HealThreshold")
                    ? (int)locals.GetInt("HealThreshold") : _config.NpcHealThreshold;
                return new NpcAI.NpcCastDecision(false, newSpell, newTarget, retargeted, healThreshold);
            };
        bool npcLookAtItemUsed = _triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCLookAtItem);
        bool npcSeeWantItemUsed = _triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCSeeWantItem);
        if (npcLookAtItemUsed || npcSeeWantItemUsed)
            _npcAI.OnNpcLookAtItem = (npc, item, dist, want) =>
            {
                // Source-X NPC_LookAtItem contract (CCharNPCAct.cpp:954):
                // ARGN1 = distance, ARGN2 = want-score (writable), ARGO =
                // the item. RETURN 1 = script took the item over, RETURN 0
                // = ignore it; otherwise ARGN2 reads back.
                var args = new TriggerArgs
                {
                    CharSrc = npc, O1 = item, ItemSrc = item, N1 = dist, N2 = want
                };
                var res = npcLookAtItemUsed
                    ? _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCLookAtItem, args)
                    : TriggerResult.Default;
                return new NpcAI.NpcLookDecision(
                    res == TriggerResult.True, res == TriggerResult.False, SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N2));
            };
        if (npcSeeWantItemUsed)
            _npcAI.OnNpcSeeWantItem = (npc, item) =>
                _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCSeeWantItem,
                    new TriggerArgs { CharSrc = npc, O1 = item, ItemSrc = item }) == TriggerResult.True;
        if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCAction))
            _npcAI.OnNpcAction = npc =>
                _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCAction,
                    new TriggerArgs { CharSrc = npc }) == TriggerResult.True;


    }
}
