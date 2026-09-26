using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;

namespace SphereNet.Game.Death;

/// <summary>
/// Corpse and loot system. Maps to CChar::Death and CItemCorpse in Source-X.
/// Handles death processing, corpse creation, loot drop, and decay.
/// </summary>
public sealed class DeathEngine
{
    private readonly GameWorld _world;

    /// <summary>Corpse decay time for players (in seconds).</summary>
    public int CorpseDecayPlayer { get; set; } = 900; // 15 minutes

    /// <summary>Corpse decay time for NPCs (in seconds).</summary>
    public int CorpseDecayNPC { get; set; } = 300; // 5 minutes

    /// <summary>Whether looting others' corpses is a criminal act.</summary>
    public bool LootingIsACrime { get; set; } = true;

    /// <summary>Fired when a character is killed.</summary>
    public event Action<Character, Character?>? OnDeath;

    /// <summary>Party manager reference for loot rights.</summary>
    public Party.PartyManager? PartyManager { get; set; }

    /// <summary>Optional script trigger dispatcher for corpse item hooks.</summary>
    public TriggerDispatcher? TriggerDispatcher { get; set; }

    /// <summary>Host hook: dismount a mounted victim before the corpse is
    /// made (Source-X CChar::Death runs Horse_UnMount first). Wired to the
    /// mount engine + appearance broadcasts; null in bare test setups.</summary>
    public Action<Character>? DismountHook { get; set; }

    /// <summary>Host hook: cancel any open secure trade the victim is part of
    /// (Source-X CChar::Death Trade_Delete) — otherwise the trade contents
    /// bypass the corpse loot drop and the partner keeps a stale window.</summary>
    public Action<Character>? CancelTradesHook { get; set; }

    /// <summary>Host hook: the "killed by ..." record (Source-X LOGM_KILLS log
    /// + party SysMessageAll). Args: the victim, the formatted message.</summary>
    public Action<Character, string>? KillMessageHook { get; set; }

    /// <summary>Host hook: the vanish burst when a summon dies corpseless
    /// (Source-X MakeCorpse ITEMID_FX_SPELL_FAIL).</summary>
    public Action<Character>? ConjuredVanishEffectHook { get; set; }

    /// <summary>Host hook: strip every active spell effect from the victim.
    ///
    /// Source-X CChar::Death runs Spell_Dispel(100) before the corpse is made
    /// (CCharAct.cpp:4397) — "get rid of all spell effects". Nothing did that
    /// here, so a player who died buffed rose from the dead still buffed and kept
    /// it until the timers ran out, and a curse outlived the death that ended it.
    ///
    /// The reference spares ATTR_MOVE_NEVER items on the spell layers; this engine
    /// puts nothing but its own effect memories there, so there is nothing to
    /// spare. Wired to SpellEngine.StripDispellableEffects; null in bare test
    /// setups.</summary>
    public Action<Character>? DispelEffectsHook { get; set; }

    /// <summary>Host hook: play a sound at the victim (args: victim, sound id).
    /// ProcessDeath uses it for the death cry - Source-X CChar::Death plays
    /// SoundChar(CRESND_DIE) for every death, player or creature, right before the
    /// character is flagged dead (CCharAct.cpp:4391-4393). Null in bare test setups.</summary>
    public Action<Character, ushort>? DeathSoundHook { get; set; }

    public DeathEngine(GameWorld world)
    {
        _world = world;
    }

    /// <summary>
    /// Process a character's death. Maps to CChar::Death in Source-X.
    /// Creates corpse, drops loot, handles NPC cleanup.
    /// </summary>
    public Item? ProcessDeath(Character victim, Character? killer = null)
    {
        if (victim.IsDead || victim.IsDeleted)
            return null;

        // Source-X CChar::Death: an invulnerable character cannot die.
        if (victim.IsStatFlag(StatFlag.Invul))
            return null;

        Character? effectiveKiller = killer;
        if (killer != null && killer.NpcMaster.IsValid)
        {
            var master = _world.FindChar(killer.NpcMaster);
            if (master != null && !master.IsDeleted)
                effectiveKiller = master;
        }
        if (effectiveKiller == null)
        {
            // Some death sources arrive without a final-blow argument even
            // though damage attribution is present (delayed poison/effects).
            // Source-X still credits m_lastAttackers; choose the strongest
            // valid contributor as the representative killer, while the
            // normal offender loop below continues to credit every attacker.
            foreach (var rec in victim.Attackers
                .Where(r => !r.Ignored && r.TotalDamage > 0)
                .OrderByDescending(r => r.TotalDamage))
            {
                var candidate = _world.FindChar(rec.Uid);
                if (candidate == null || candidate.IsDeleted) continue;
                effectiveKiller = candidate.ResolveOwnerCharacter() ?? candidate;
                if (!effectiveKiller.IsDeleted)
                    break;
                effectiveKiller = null;
            }
        }

        // @Death — Source-X fires it before any death processing; RETURN 1 cancels
        // the death entirely (no corpse, the victim is not killed). Centralised
        // here so every death entry point (combat, spell, NPC, GM, offline) honours
        // it instead of firing-and-ignoring at each call site.
        // Source-X OnTrigger(CTRIG_Death, <empty>, this): SRC is the dying char
        // itself, not the killer. The killer stays reachable as ARGO (a SphereNet
        // extension — Source-X seeds no args here, so faithful scripts never read it).
        if (TriggerDispatcher?.FireCharTrigger(victim, CharTrigger.Death,
                new TriggerArgs { CharSrc = victim, O1 = effectiveKiller }) == TriggerResult.True)
            return null;

        var creditedOffenders = new List<Character>();
        int attackerCount = 1;
        if (effectiveKiller != null)
        {
            var offenders = new List<Character>();
            var creditedUids = new HashSet<uint>();
            foreach (var offender in EnumerateOffenders(victim, effectiveKiller))
                if (creditedUids.Add(offender.Uid.Value))
                    offenders.Add(offender);
            attackerCount = Math.Max(1, offenders.Count);

            foreach (var offender in offenders)
            {
                if (TriggerDispatcher?.FireCharTrigger(offender, CharTrigger.Kill,
                        new TriggerArgs
                        {
                            CharSrc = offender,
                            O1 = victim,
                            N1 = victim.Attackers.Count
                        }) != TriggerResult.True)
                    creditedOffenders.Add(offender);
            }
        }

        // Sleeping is cleared by Kill() below (Source-X clears it before
        // MakeCorpse too) — capture it first for the corpse forensics stamp.
        bool wasSleeping = victim.IsStatFlag(StatFlag.Sleeping);

        // Champion wave credit — Source-X routes this through the spawn
        // back-link at object destroy (CObjBase dtor → CCChampion::DelObj);
        // we credit at death time via the SPAWNITEM tag so candles/levels
        // advance while the corpse still exists.
        if (!victim.IsPlayer &&
            victim.TryGetTag("SPAWNITEM", out string? spawnItemUid) &&
            !string.IsNullOrEmpty(spawnItemUid))
        {
            string hexPart = spawnItemUid.Trim();
            if (hexPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hexPart = hexPart[2..];
            else if (hexPart.StartsWith('0') && hexPart.Length > 1) hexPart = hexPart[1..];
            if (uint.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, null, out uint spawnerUid) &&
                _world.FindItem(new Serial(spawnerUid)) is { Champion: not null } altar)
            {
                altar.Champion.OnMemberDeath(victim);
            }
        }

        // The death cry, while the victim still has its living body (the chardef's
        // SOUNDDIE, else its SOUND= base + the die offset, else the human set).
        ushort deathSound = Combat.CharacterSounds.Resolve(victim, AI.CreatureSoundType.Die);
        if (deathSound != 0)
            DeathSoundHook?.Invoke(victim, deathSound);

        // Kill the character
        victim.Kill();

        // Source-X CChar::Death: Spell_Dispel(100) right after the skill cleanup
        // and before the corpse forms (CCharAct.cpp:4397). Death ends every spell
        // on you, good and bad alike.
        DispelEffectsHook?.Invoke(victim);

        // Source-X CChar::Death deletes any open trade window before the
        // corpse forms; the trade items return to the pack and so reach the
        // corpse with the rest of the loot.
        CancelTradesHook?.Invoke(victim);

        // Source-X CChar::Death clears the victim's FIGHT / HARMEDBY
        // memories — the ghost holds no grudges (and no self-defence
        // rights) from the fight that killed it.
        foreach (var mem in new List<Item>(victim.Memories))
            victim.Memory_ClearTypes(mem, MemoryType.Fight | MemoryType.HarmedBy);

        // Source-X CChar::Death order: the rider leaves the saddle before the
        // corpse is made — otherwise the mount-layer item is snapshotted into
        // the death state and the client keeps drawing a mounted body under
        // the ghost.
        if (victim.IsMounted)
            DismountHook?.Invoke(victim);

        // Karma/Fame/murder credit — skipped when @Kill returned 1.
        if (effectiveKiller != null)
        {
            // Source-X CChar::Death credits EVERY damaging attacker and divides the
            // fame/karma/experience reward by the attacker count (Noto_Kill's
            // iTotalKillers), so a group splits the spoils instead of the final
            // blow taking all of it. Deduped, pets credited to their master.
            foreach (var offender in creditedOffenders)
            {
                ApplyKarmaFameChange(offender, victim, attackerCount,
                    SphereNet.Game.Clients.GameClient.ComputeNotoriety(_world, offender, victim));

                // Experience award (Noto_Kill, CCharNotoriety.cpp:619-646): gated on
                // EXPERIENCESYSTEM + EXP_MODE_RAISE_COMBAT, a tenth of the victim's
                // experience split across the killers and scaled by
                // EXPERIENCEKOEFPVP/PVM and the relative totals.
                int expReward = Character.KillExperienceReward(offender, victim, attackerCount);
                if (expReward != 0)
                    offender.ChangeExperience(expReward);
            }

            // PvP murder tracking — Source-X Noto_Kill marks EVERY unprovoked
            // attacker of an innocent, not just the final-blow killer, so ganking
            // an innocent flags the whole group.
            MarkMurderers(victim, creditedOffenders);
        }

        // Source-X kill record (CCharAct.cpp:4357-4389): "'<victim>' was
        // killed by 'A', 'B'." — logged for player deaths and echoed to the
        // victim's party (an unattributed death reads "accident").
        if (KillMessageHook != null && victim.IsPlayer)
        {
            string names = creditedOffenders.Count == 0
                ? ""
                : string.Join(", ", creditedOffenders
                    .Select(o => $"'{o.GetDisplayName()}'").Distinct());
            KillMessageHook(victim,
                $"'{victim.GetDisplayName()}' was killed by {(names.Length > 0 ? names : "accident")}.");
        }

        // Source-X clears m_lastAttackers once the corpse and its @DeathCorpse are done
        // (CCharAct.cpp:4418), so @CreateLoot and @DeathCorpse still read ATTACKER.*.
        // Player ghosts otherwise retained stale damage contributors until
        // resurrection (and persisted them on save).
        int deathFlags = GetDeathFlags(victim);

        // Source-X CChar::Death player penalties (CCharAct.cpp:4443-4470):
        // a tenth of the experience is lost (min 1), a tenth of the fame
        // unless DEATHFLAGS & DEATH_NOFAMECHANGE (0x01), and the deaths
        // counter increments.
        if (victim.IsPlayer)
        {
            victim.ChangeExperience(-Math.Max(1, victim.Exp / 10));
            if ((deathFlags & 0x01) == 0)
                ApplyFame(victim, -(victim.Fame / 10));
            victim.Deaths = (short)Math.Min(victim.Deaths + 1, short.MaxValue);
        }

        // Source-X CChar::Death order is critical for players:
        //   1) MakeCorpse  (corpse.Amount = current/original body ID)
        //   2) Broadcast PacketDeath (0xAF) to nearby
        //   3) SetID(ghost) + SetHue(0)  ← only after the corpse exists
        // If we fire OnDeath here (which transitions the player to a ghost
        // body in OnCharacterDeath) BEFORE CreateCorpse runs, the corpse
        // will be created with amount=0x192 (ghost) instead of the player's
        // real body, and the corpse on the ground renders as a ghost shape
        // instead of a normal humanoid corpse. Source-X ordering avoids
        // exactly this.
        // Source-X MakeCorpse: summoned creatures and a DEATH_NOCORPSE flag leave
        // no corpse — they simply vanish (DeleteObject refreshes nearby clients).
        // Source-X CChar::Death runs @CreateLoot once, immediately before
        // MakeCorpse. Running it during NPC initialization duplicated spawn
        // loot and made death-time conditions observe a living creature. It runs for
        // every death, summons included (CCharAct.cpp:4402-4406).
        TriggerDispatcher?.FireCharTrigger(victim, CharTrigger.CreateLoot,
            new TriggerArgs { CharSrc = victim });

        if (ShouldLeaveNoCorpse(victim, deathFlags))
        {
            victim.ClearAttackers();
            // Source-X MakeCorpse: a summon that leaves no corpse bursts a
            // spell-fizzle effect instead of silently vanishing.
            if (victim.IsSummoned)
                ConjuredVanishEffectHook?.Invoke(victim);
            if (!victim.IsPlayer && !victim.IsBonded)
            {
                _world.DeleteObject(victim);
            }
            return null;
        }

        var corpse = CreateCorpse(victim, wasSleeping);
        corpse.SetTag("OWNER_UID", victim.Uid.Value.ToString());
        corpse.SetTag("OWNER_UUID", victim.Uuid.ToString("D"));

        if (effectiveKiller != null)
        {
            corpse.SetTag("KILLER_UID", effectiveKiller.Uid.Value.ToString());
            corpse.SetTag("KILLER_UUID", effectiveKiller.Uuid.ToString("D"));
        }

        // Drop equipped items and backpack contents to corpse — unless DEATH_NOLOOTDROP
        // keeps everything on the (now-dead) body. (DEATH_NOLOOTDROP = 0x04.)
        if ((deathFlags & 0x04) == 0)
        {
            if (victim.IsPlayer)
                DropLootToCorpse(victim, corpse);
            else
                DropNpcLootToCorpse(victim, corpse);
        }

        // @DeathCorpse — fired on the victim once the corpse exists and the
        // loot has been transferred (Source-X CChar::Death fires it right
        // after MakeCorpse, which moves the items itself), with the corpse
        // as the argument object.
        TriggerDispatcher?.FireCharTrigger(victim, CharTrigger.DeathCorpse, new TriggerArgs
        {
            CharSrc = victim,
            O1 = corpse
        });
        victim.ClearAttackers();

        // Now that the corpse has snapshotted the original body, fire the
        // death callbacks so OnCharacterDeath / OnNpcKill can swap the
        // mobile to its ghost body and broadcast the new appearance.
        OnDeath?.Invoke(victim, effectiveKiller);

        // Set corpse decay timer. Using the Item.DecayTime field (not a
        // TAG) routes this through the sector-tick Item.OnTick path —
        // the same mechanism spell fields and summoned items use.
        // Source-X does the same via CItem::_SetTimeout on the corpse,
        // driven from its sector, with no central scanner.
        int decaySeconds = victim.IsPlayer ? CorpseDecayPlayer : CorpseDecayNPC;
        corpse.SetDecayAt(Environment.TickCount64 + decaySeconds * 1000);

        // For NPCs, remove the mobile from world state immediately so it no
        // longer blocks movement or lingers in sector/object queries after the
        // corpse has been created. Bonded pets stay as ghosts (like players).
        if (!victim.IsPlayer && !victim.IsBonded)
        {
            _world.DeleteObject(victim);
        }

        return corpse;
    }

    /// <summary>Source-X DEATHFLAGS (CChar.h): a per-character bitmask controlling
    /// corpse/loot/fame behaviour on death. Parsed from the DEATHFLAGS tag (hex or
    /// decimal). 0 when unset.</summary>
    private static int GetDeathFlags(Character victim)
    {
        if (!victim.TryGetTag("DEATHFLAGS", out string? df) || string.IsNullOrWhiteSpace(df))
            return 0;
        df = df.Trim();
        bool ok = df.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(df.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int v)
            : int.TryParse(df, out v);
        return ok ? v : 0;
    }

    /// <summary>Source-X MakeCorpse: no corpse for DEATH_NOCORPSE (0x02), or for a
    /// summoned creature unless DEATH_NOCONJUREDEFFECT (0x08) / DEATH_HASCORPSE
    /// (0x10) is set. Players always leave a corpse.</summary>
    private static bool ShouldLeaveNoCorpse(Character victim, int deathFlags)
    {
        if (victim.IsPlayer) return false;
        if ((deathFlags & 0x02) != 0) return true;
        if (victim.IsSummoned && (deathFlags & (0x08 | 0x10)) == 0) return true;
        return false;
    }

    /// <summary>Whether killer→victim is an unprovoked kill of an innocent, the
    /// only case Source-X Noto_Kill counts as murder (NotoThem &lt; NOTO_GUILD_SAME).
    /// False when the victim is a criminal/murderer (red or grey), or aggressed the
    /// killer first — in which case the killer holds a HarmedBy memory of the victim
    /// (Memory_Fight_Start tags the defender HarmedBy and the aggressor IAggressor).</summary>
    /// <summary>
    /// Mark a murder against every attacker of an innocent player victim — the
    /// final-blow killer plus everyone in the victim's attacker log — instead of
    /// just the killer (Source-X Noto_Kill loops the damage list). Each offender
    /// is resolved pet→master, deduped, and gated through @MurderMark (which can
    /// adjust the count, suppress the criminal flag, or block the mark).
    /// </summary>
    private void MarkMurderers(Character victim, IEnumerable<Character> offenders)
    {
        if (!victim.IsPlayer) return;

        var marked = new HashSet<uint>();
        foreach (var offender in offenders)
        {
            if (!offender.IsPlayer) continue;
            if (!marked.Add(offender.Uid.Value)) continue;
            if (!IsUnprovokedInnocentKill(offender, victim)) continue;

            int proposed = offender.Kills + 1;
            var decision = Character.OnMurderMark == null
                ? new Character.MurderMarkDecision(proposed, true)
                : Character.OnMurderMark(offender, victim, proposed);
            if (decision.Count.HasValue)
            {
                offender.Kills = (short)Math.Clamp(decision.Count.Value, 0, short.MaxValue);
                // ARGN2 asks for Noto_Criminal (CCharNotoriety.cpp:600-601): the
                // regular criminal flag, @Criminal and CRIMINALTIMER included.
                if (decision.MakeCriminal)
                    offender.MakeCriminal();
            }
        }
    }

    /// <summary>The final-blow killer first, then every logged attacker that
    /// actually DEALT DAMAGE, each resolved to its effective offender (a pet's hits
    /// credit its master). Ignored attackers (ATTACKER.n.IGNORE) are skipped.
    ///
    /// The damage gate is the reference's (CCharAct.cpp:4361 credits a row only
    /// while <c>amountDone &gt; 0</c>) and it is load-bearing now that the list also
    /// holds characters this one merely ENGAGED: without it, swinging once and
    /// missing would earn a share of the kill.</summary>
    private IEnumerable<Character> EnumerateOffenders(Character victim, Character effectiveKiller)
    {
        yield return effectiveKiller;
        foreach (var rec in victim.Attackers)
        {
            if (rec.Ignored || rec.TotalDamage <= 0) continue;
            var attacker = _world.FindChar(rec.Uid);
            if (attacker == null || attacker.IsDeleted) continue;
            if (attacker.NpcMaster.IsValid)
            {
                var master = _world.FindChar(attacker.NpcMaster);
                if (master != null && !master.IsDeleted) { yield return master; continue; }
            }
            yield return attacker;
        }
    }

    private static bool IsUnprovokedInnocentKill(Character killer, Character victim)
    {
        if (victim.IsCriminal || victim.IsMurderer || victim.IsStatFlag(StatFlag.Criminal))
            return false;
        var killerMemOfVictim = killer.Memory_FindObj(victim.Uid);
        if (killerMemOfVictim != null && killerMemOfVictim.IsMemoryTypes(MemoryType.HarmedBy))
            return false; // victim struck first — self-defence, not murder
        return true;
    }

    /// <summary>Apply Karma/Fame changes when killer kills victim, divided by the
    /// total attacker count (Source-X Noto_Kill's iTotalKillers split):
    /// Calc_FameKill + Calc_KarmaKill + Calc_KarmaScale, each share /attackerCount.</summary>
    private static void ApplyKarmaFameChange(Character killer, Character victim, int attackerCount,
        byte notoThem = 1)
    {
        if (attackerCount < 1) attackerCount = 1;

        // Fame: Source-X Calc_FameKill — PC kill /10, NPC kill /200 — then split by
        // the attacker count. No per-kill magnitude cap (Source-X only clamps the
        // running total to 0..10000, which ApplyFame does). A zero-fame victim
        // still grants zero — no forced minimum — so trash mobs can't be farmed.
        int rawFame = Math.Max(0, (int)victim.Fame);
        int fameGain = (victim.IsPlayer ? rawFame / 10 : rawFame / 200) / attackerCount;
        ApplyFame(killer, fameGain);

        // Source-X Calc_KarmaKill (CResourceCalc.cpp:353-384): the change is minus the
        // victim's karma. When the victim was criminal or worse TO THE KILLER
        // (NotoThem >= NOTO_CRIMINAL) only a LOSS is cancelled - killing an evil red
        // still earns karma.
        int karmaChange = -victim.Karma;
        if (notoThem >= 4 && karmaChange < 0) // NOTO_CRIMINAL
            karmaChange = 0;
        if (victim.IsPlayer)
        {
            if (karmaChange < 0 && karmaChange > -5000)
                karmaChange = -5000;
            karmaChange /= 10;
        }
        else
        {
            if (karmaChange < 0 && karmaChange > -1000)
                karmaChange = -1000;
            karmaChange /= 20;
        }

        // Split across attackers BEFORE the diminishing-returns scale (Source-X
        // passes Calc_KarmaKill / iTotalKillers into Noto_Karma, which then scales).
        karmaChange = ScaleKarma(killer.Karma, karmaChange / attackerCount);
        ApplyKarma(killer, karmaChange);
    }

    // Config-driven fame/karma limits (sphere.ini MaxFame / MaxKarma / MinKarma).
    // Defaults match the previous hardcoded values, so unset configs behave identically.
    public static int MaxFame = 10000;
    public static int MaxKarma = 10000;
    public static int MinKarma = -10000;

    /// <summary>Apply a Fame delta after firing @FameChange (Source-X Noto_Fame).
    /// A script returning null cancels the change; otherwise the (possibly
    /// adjusted) delta is clamped into [0, MaxFame].</summary>
    private static void ApplyFame(Character killer, int delta)
    {
        if (delta == 0) return;
        if (Character.OnFameChanging != null)
        {
            int? adjusted = Character.OnFameChanging(killer, delta);
            if (adjusted == null) return;
            delta = adjusted.Value;
        }
        killer.Fame = (short)Math.Clamp(killer.Fame + delta, 0, MaxFame);
    }

    /// <summary>Apply a Karma delta after firing @KarmaChange (Source-X Noto_Karma).
    /// A script returning null cancels the change; otherwise the (possibly
    /// adjusted) delta is clamped into [-10000, 10000].</summary>
    private static void ApplyKarma(Character killer, int delta)
    {
        if (delta == 0) return;
        if (Character.OnKarmaChanging != null)
        {
            int? adjusted = Character.OnKarmaChanging(killer, delta);
            if (adjusted == null) return;
            delta = adjusted.Value;
        }
        killer.Karma = (short)Math.Clamp(killer.Karma + delta, MinKarma, MaxKarma);
    }

    /// <summary>Source-X Calc_KarmaScale (CResourceCalc.cpp:387-407): a good
    /// character loses karma twice as fast and gains it at half rate, and THEN a gain
    /// below a 64th of the current karma is dropped.</summary>
    private static int ScaleKarma(short currentKarma, int change)
    {
        if (currentKarma > 0)
            change = change < 0 ? change * 2 : change / 2;
        if (change > 0 && change < currentKarma / 64)
            return 0;
        return change;
    }

    /// <summary>Create a corpse item at the victim's position.</summary>
    private Item CreateCorpse(Character victim, bool wasSleeping = false)
    {
        var corpse = _world.CreateItem();
        corpse.BaseId = 0x2006; // ITEMID_CORPSE
        corpse.Amount = victim.BodyId; // body type for corpse display
        string victimName = victim.GetDisplayName();
        corpse.Name = ServerMessages.GetFormatted(Msg.CorpseName, "corpse", victimName);
        corpse.SetTag("CORPSE_NAME", victimName);
        corpse.ItemType = ItemType.Corpse;
        // Source-X MakeCorpse: m_itCorpse.m_BaseID = _iPrev_id — the creature TYPE
        // the corpse came from, which carving reads its RESOURCES from. The owner
        // cannot stand in for it later: a resurrected player may have changed
        // body, and an NPC's mobile is deleted with its death.
        int corpseDefIndex = ResolveCorpseCharDefIndex(victim);
        if (corpseDefIndex != 0)
            corpse.SetTag("CORPSE_CHARDEF", corpseDefIndex.ToString());
        corpse.Hue = victim.Hue;
        corpse.Direction = (byte)((byte)victim.Direction & 0x07); // facing snapshot for carve/forensics
        corpse.SetAttr(ObjAttributes.Move_Never); // a corpse can't be dragged, only looted (Source-X)

        // A corpse holds no more than its owner could carry. The reference sets
        // this and says why: "set corpse maxweight to prevent weird exploits like
        // when someone place many items on an player corpse just to make this
        // player get stuck on resurrect" (CItemCorpse.cpp:194). Without it a
        // corpse was an unbounded public container — the drop path already
        // enforces a container's MODMAXWEIGHT, it just had none to enforce.
        corpse.ModMaxWeight = victim.MaxWeight;

        // Forensics reads DEATH_TIME / CORPSE_CARVED / CORPSE_SLEEPING. Stamp the
        // death time so the skill can report how long ago the death occurred; the
        // carved/sleeping flags default to unset and are set when carved/sleeping.
        corpse.SetTag("DEATH_TIME", Environment.TickCount64.ToString());
        if (wasSleeping || victim.IsStatFlag(StatFlag.Sleeping))
            corpse.SetTag("CORPSE_SLEEPING", "1");

        // Source-X MakeCorpse: corpses of bonded pets, summoned creatures and
        // sleeping bodies are born uncarvable (m_itCorpse.m_carved = 1).
        if (victim.IsBonded || victim.IsSummoned || wasSleeping ||
            victim.IsStatFlag(StatFlag.Sleeping))
            corpse.SetTag("CORPSE_CARVED", "1");

        _world.PlaceItem(corpse, victim.Position);
        return corpse;
    }

    /// <summary>The chardef a corpse is typed by (Source-X _iPrev_id): the
    /// character's own definition, else the one its original (pre-polymorph)
    /// body belongs to. 0 when neither resolves.</summary>
    private static int ResolveCorpseCharDefIndex(Character victim)
    {
        int own = victim.CharDefIndex;
        if (own != 0 && Definitions.DefinitionLoader.GetCharDef(own) != null)
            return own;
        ushort body = victim.OBody != 0 ? victim.OBody : victim.BodyId;
        if (Definitions.DefinitionLoader.GetCharDef(body) != null)
            return body;
        return Definitions.DefinitionLoader.GetCharDefByBody(body)?.Id.Index ?? 0;
    }

    /// <summary>
    /// Take whatever the character is holding on the cursor off the cursor and hand
    /// it back as an object, cancelling the client-side drag. Returns null when
    /// nothing was held.
    ///
    /// The item is NOT settled into the pack here. Source-X carries a dragged item
    /// on LAYER_DRAGGING and resolves it inside UnEquipAllItems (CCharAct.cpp:636),
    /// so it is judged by the EQUIPMENT protected set and lands in the pack when it
    /// is protected. Dropping it into the pack first instead subjected it to the
    /// narrower pack set, which sends a plain Blessed item to the corpse.
    /// </summary>
    private Item? TakeDraggedItem(Character victim)
    {
        if (!victim.TryGetTag("DRAGGING", out string? raw) ||
            !uint.TryParse(raw, out uint uid) || uid == 0)
            return null;

        victim.RemoveTag("DRAGGING");

        // Cancel the drag cursor client-side. The bridge also moves the item, so it
        // is told to bounce into the pack; whichever parent it ends up with, the
        // caller takes it from there and applies the equipment rules.
        Character.OnDragCancel?.Invoke(victim);

        var item = _world.FindItem(new Serial(uid));
        return item is { IsDeleted: false } ? item : null;
    }

    /// <summary>
    /// Move the victim's belongings to the corpse.
    ///
    /// Order matters and follows Source-X CChar::DropAll (CCharAct.cpp:564): the
    /// PACK is transferred first, then equipment. Protected equipment goes into the
    /// pack, and because the pack has already been emptied it stays there. Running
    /// equipment first — and re-equipping protected pieces instead of packing them —
    /// left the ghost still wearing them, and anything moved to the pack would then
    /// have been judged again by the narrower pack rules.
    /// </summary>
    private void DropLootToCorpse(Character victim, Item corpse)
    {
        // 1) Pack contents (Source-X CContainer::ContentsTransfer).
        var pack = victim.Backpack;
        if (pack != null)
        {
            var contents = new List<Item>(pack.Contents);
            foreach (var item in contents)
            {
                if (StaysInPackOnDeath(item))
                    continue;

                pack.RemoveItem(item);
                AddToCorpseOrGround(corpse, item);
            }
        }

        // 2) Equipment, and the item on the cursor with it — Source-X treats
        //    LAYER_DRAGGING as one more layer in the same pass.
        Layer[] dropLayers = [
            Layer.OneHanded, Layer.TwoHanded, Layer.Shoes, Layer.Pants, Layer.Shirt,
            Layer.Helm, Layer.Gloves, Layer.Ring, Layer.Talisman, Layer.Neck,
            Layer.Waist, Layer.Chest, Layer.Bracelet, Layer.Tunic, Layer.Earrings,
            Layer.Arms, Layer.Cape, Layer.Robe, Layer.Skirt, Layer.Legs
        ];

        foreach (var layer in dropLayers)
        {
            var item = victim.Unequip(layer);
            if (item == null)
                continue;

            if (StaysWithOwnerOnDeath(item))
            {
                KeepWithOwner(victim, item);
                continue;
            }

            item.SetTag("EQUIPLAYER", ((byte)layer).ToString());
            AddToCorpseOrGround(corpse, item);
        }

        var dragged = TakeDraggedItem(victim);
        if (dragged != null)
        {
            if (StaysWithOwnerOnDeath(dragged))
                KeepWithOwner(victim, dragged);
            else
                AddToCorpseOrGround(corpse, dragged);
        }

        // Source-X UnEquipAllItems: hair and beard are COPIED onto the corpse
        // (CreateDupeItem) so it renders with them; the originals stay on the
        // ghost and the dupes are discarded on corpse rejoin (RaiseCorpse
        // skips IT_HAIR/IT_BEARD).
        foreach (var hairLayer in new[] { Layer.Hair, Layer.FacialHair })
        {
            var hair = victim.GetEquippedItem(hairLayer);
            if (hair == null) continue;
            var dupe = _world.CreateItem();
            dupe.BaseId = hair.BaseId;
            dupe.Hue = hair.Hue;
            dupe.ItemType = hair.ItemType;
            dupe.Name = hair.GetName();
            dupe.SetTag("EQUIPLAYER", ((byte)hairLayer).ToString());
            dupe.SetTag("CORPSE_HAIR", "1"); // a render copy, never loot/restore
            AddToCorpseOrGround(corpse, dupe);
        }
    }

    /// <summary>Put a protected item back with its owner. Source-X UnEquipAllItems
    /// moves it into the pack (CCharAct.cpp:664) rather than leaving it worn, so a
    /// ghost is not still wearing its blessed gear.</summary>
    private void KeepWithOwner(Character victim, Item item)
    {
        var pack = victim.Backpack;
        if (pack != null && pack.TryAddItem(item))
            return;

        // No pack, or it would not take it: the ground at the victim's feet.
        item.ContainedIn = Serial.Invalid;
        _world.PlaceItemWithDecay(item, victim.Position);
    }

    /// <summary>Items that are NOT transferred to the corpse on death and remain
    /// with the owner (re-equipped or kept in the pack). Source-X MakeCorpse keeps
    /// blessed/newbie/move-never/no-trade items (plus the shard's insured/quest
    /// equivalents). SphereNet maps: Blessed/Blessed2/Newbie/Nodropt (no-drop),
    /// Move_Never (cannot be moved by players, so must not land in a lootable
    /// corpse), NotRading (no-trade) and Cursed2 (stays-with-owner cursed).</summary>
    private static bool StaysWithOwnerOnDeath(Item item) =>
        item.IsAttr(ObjAttributes.Blessed) || item.IsAttr(ObjAttributes.Blessed2) ||
        item.IsAttr(ObjAttributes.Newbie) || item.IsAttr(ObjAttributes.Nodropt) ||
        item.IsAttr(ObjAttributes.Move_Never) || item.IsAttr(ObjAttributes.NotRading) ||
        item.IsAttr(ObjAttributes.Cursed2);

    /// <summary>Items that stay in the pack rather than moving to the corpse.
    ///
    /// Source-X uses a NARROWER set here than for equipment: CContainer::ContentsTransfer
    /// keeps only ATTR_NEWBIE / ATTR_MOVE_NEVER / ATTR_CURSED2 / ATTR_BLESSED2
    /// (CContainer.cpp:528), while UnEquipAllItems additionally keeps ATTR_BLESSED,
    /// ATTR_INSURED, ATTR_NODROP, ATTR_NOTRADE and ATTR_QUESTITEM. Plain "blessed"
    /// therefore protects an item you are WEARING, not one loose in your pack.
    ///
    /// Note both engines transfer the pack one level deep: a protected item inside a
    /// plain bag travels with the bag in Source-X too. That is parity, not an
    /// oversight — the protection is a property of what you carry directly.</summary>
    private static bool StaysInPackOnDeath(Item item) =>
        item.IsAttr(ObjAttributes.Newbie) || item.IsAttr(ObjAttributes.Move_Never) ||
        item.IsAttr(ObjAttributes.Cursed2) || item.IsAttr(ObjAttributes.Blessed2);

    /// <summary>Drop NPC loot to corpse (all inventory + level-based loot).</summary>
    private void DropNpcLootToCorpse(Character victim, Item corpse)
    {
        DropLootToCorpse(victim, corpse);

        // Roll the NPC's deferred plain loot (chardef ITEM= entries that
        // were intentionally not materialised at spawn) straight into the
        // corpse — Source-X CTRIG_CreateLoot parity, and the reason living
        // NPCs carry no transient loot in the world save.
        victim.MaterializeDeathLoot(corpse);

        // Source-X drops ONLY what the chardef/loot scripts define (ITEM= /
        // @CreateLoot). A stat-tier gold/reagent/gem generator used to run on
        // top of the script loot here — every kill minted extra gold and
        // materials the pack never granted, inflating the economy.
    }

    /// <summary>
    /// Source-X "Resurrect with Corpse" — when a character is resurrected
    /// while standing on (or owning) their own corpse, automatically
    /// re-equip every item that was equipped at death (using the
    /// EQUIPLAYER tag DropLootToCorpse stamped on it) and dump the rest
    /// back into the backpack. The corpse is deleted once empty.
    ///
    /// Returns true iff a matching corpse was found and processed (the
    /// caller can then skip the "you are still naked" path). If the
    /// resurrected character has no backpack yet (rare — fresh char),
    /// remaining items fall to the ground at the corpse position so
    /// they aren't lost.
    ///
    /// Edge cases handled:
    ///   * The corpse may have decayed/been looted between death and
    ///     resurrect — search returns no match, return false.
    ///   * An equip slot may already be occupied (e.g. NPC healer
    ///     handed the player a robe) — fall back to the backpack.
    ///   * The character may be standing one tile off — we sweep the
    ///     character's tile only (matches Source-X CChar::ResurrectFromCorpse
    ///     which reads <c>g_World.GetItemsAt(GetTopPoint())</c>).
    /// </summary>
    public bool RestoreFromCorpse(Character resurrected)
    {
        Item? corpse = null;
        foreach (var item in _world.GetItemsInRange(resurrected.Position, 2))
        {
            if (item.ItemType != ItemType.Corpse) continue;

            // Source-X FindMyCorpse gates: the corpse must be top-level (not in a
            // container), not flagged NOREJOIN (e.g. a decayed bones pile the owner
            // can no longer rejoin), in line of sight, and owned by this character.
            if (item.ContainedIn.IsValid) continue;
            if (item.TryGetTag("NOREJOIN", out _)) continue;
            if (!_world.CanSeeLOS(resurrected.Position, item.Position)) continue;

            bool owned =
                (item.TryGetTag("OWNER_UUID", out string? uuidStr) &&
                 Guid.TryParse(uuidStr, out Guid uuid) && uuid == resurrected.Uuid) ||
                (item.TryGetTag("OWNER_UID", out string? ownerStr) &&
                 uint.TryParse(ownerStr, out uint ownerUid) && ownerUid == resurrected.Uid.Value);
            if (!owned) continue;

            corpse = item;
            break;
        }
        if (corpse == null) return false;

        // Snapshot first — RemoveItem mutates the underlying list and
        // would otherwise invalidate the iterator after the first call.
        var contents = new List<Item>(corpse.Contents);
        var pack = resurrected.Backpack;

        foreach (var item in contents)
        {
            corpse.RemoveItem(item);

            // The corpse-render hair/beard dupes are not real loot — the
            // originals never left the ghost (Source-X RaiseCorpse skips
            // IT_HAIR/IT_BEARD on rejoin).
            if (item.TryGetTag("CORPSE_HAIR", out _))
            {
                _world.RemoveItem(item);
                continue;
            }

            Layer? targetLayer = null;
            if (item.TryGetTag("EQUIPLAYER", out string? layerStr) &&
                byte.TryParse(layerStr, out byte layerByte))
            {
                targetLayer = (Layer)layerByte;
                item.RemoveTag("EQUIPLAYER");
            }

            bool placed = false;
            if (targetLayer.HasValue && targetLayer.Value != Layer.None)
            {
                // Re-equip on the original layer if free. Equip()
                // returns false on out-of-range; double-equipping the
                // same layer is handled internally by unequipping the
                // previous item, but we keep the slot-occupied check
                // explicit so that unexpected new gear (e.g. a healer
                // robe) isn't silently dropped on the ground.
                if (resurrected.GetEquippedItem(targetLayer.Value) == null &&
                    resurrected.Equip(item, targetLayer.Value))
                {
                    placed = true;
                }
            }

            if (!placed)
            {
                if (pack != null && pack.TryAddItem(item))
                {
                    placed = true;
                }
                else
                {
                    _world.PlaceItem(item, resurrected.Position);
                    placed = true;
                }
            }
        }

        // Drop any remaining tags so a half-looted corpse doesn't keep
        // the killer/owner metadata alive on the recycled UID.
        corpse.RemoveTag("OWNER_UID");
        corpse.RemoveTag("OWNER_UUID");
        corpse.RemoveTag("KILLER_UID");
        corpse.RemoveTag("KILLER_UUID");

        _world.DeleteObject(corpse);
        return true;
    }

    // === Death shroud / resurrection robe (Source-X CChar::Death / Spell_Resurrection) ===

    /// <summary>ITEMID_DEATHSHROUD — the grey robe a ghost wears.</summary>
    private const ushort DeathShroudId = 0x204E;

    /// <summary>ITEMID_ROBE — plain robe handed out on resurrection when no body
    /// covering was restored, so the player isn't resurrected naked.</summary>
    private const ushort ResurrectRobeId = 0x1F03;

    /// <summary>
    /// When true, a death shroud is equipped on the ghost at death and a
    /// resurrection robe is granted on resurrection when no robe was restored.
    /// Source-X gates the same behaviour behind a server flag.
    /// </summary>
    public static bool EnableDeathShroud { get; set; } = true;

    /// <summary>NORESROBE — suppress the robe a resurrected player is handed (Source-X
    /// m_fNoResRobe, default off: the robe IS given). Its own setting, separate from the
    /// ghost's death shroud above.</summary>
    public static bool NoResRobe { get; set; }

    /// <summary>
    /// Equip a death shroud on the dying player's Robe layer. The real robe (if
    /// any) has already dropped to the corpse by the time the ghost transition
    /// runs, so the layer is free. The shroud is Move_Never (can't be dragged off
    /// the ghost) and Newbie (never drops), tagged DEATHSHROUD so resurrection can
    /// remove it. NPCs and already-robed bodies are skipped. Returns the shroud,
    /// or null when none was equipped. Maps to CChar::Death's death-shroud equip.
    /// </summary>
    public Item? EquipDeathShroud(Character victim)
    {
        if (!EnableDeathShroud) return null;
        if (!victim.IsPlayer) return null;
        if (victim.GetEquippedItem(Layer.Robe) != null) return null;

        var shroud = _world.CreateItem();
        shroud.BaseId = DeathShroudId;
        shroud.ItemType = ItemType.Clothing;
        shroud.Name = "death shroud";
        shroud.Hue = Color.Default;
        shroud.SetAttr(ObjAttributes.Move_Never); // looters can't strip the ghost
        shroud.SetAttr(ObjAttributes.Newbie);     // stays with the owner, never drops
        shroud.SetTag("DEATHSHROUD", "1");
        victim.Equip(shroud, Layer.Robe);
        // CItem::CreateScript (CCharAct.cpp:4474): the ITEMDEF's @Create runs.
        shroud.FireCreateTrigger();
        return shroud;
    }

    /// <summary>
    /// Remove the death shroud (if present) from a character's Robe layer. Called
    /// at the start of resurrection so a robe restored from the corpse — or a
    /// resurrection robe — can take the Robe slot. DeleteObject unequips and
    /// unlinks in one step.
    /// </summary>
    public void RemoveDeathShroud(Character ch)
    {
        var robe = ch.GetEquippedItem(Layer.Robe);
        if (robe == null || !robe.TryGetTag("DEATHSHROUD", out _)) return;
        _world.DeleteObject(robe);
    }

    /// <summary>
    /// Hand a resurrected player a plain robe (Source-X Spell_Resurrection,
    /// CCharSpell.cpp:503-508): only when the corpse was NOT rejoined
    /// (<paramref name="raisedCorpse"/>) and NORESROBE is off - a CreateBase robe named
    /// DEFMSG_SPELL_RES_ROBENAME, with no newbie flag. Returns the robe, or null when
    /// none was given (or the Robe layer is already covered).
    /// </summary>
    public Item? EnsureResurrectionRobe(Character ch, bool raisedCorpse = false)
    {
        // NORESROBE, not the death-shroud flag. Upstream keeps them apart
        // (CCharSpell.cpp:503 asks only m_fNoResRobe), and tying them together meant a
        // shard that wanted invisible ghosts also resurrected everyone naked.
        if (NoResRobe || raisedCorpse) return null;
        if (!ch.IsPlayer) return null;
        if (ch.GetEquippedItem(Layer.Robe) != null) return null;

        var robe = _world.CreateItem();
        robe.BaseId = ResurrectRobeId;
        robe.ItemType = ItemType.Clothing;
        robe.Name = ServerMessages.Get(Msg.SpellResRobename);
        robe.Hue = Color.Default;
        ch.Equip(robe, Layer.Robe);
        return robe;
    }

    /// <summary>
    /// Check if looting a corpse is a criminal act.
    /// Maps to CChar::CheckCorpseCrime in Source-X.
    /// </summary>
    public bool IsLootingCriminal(Character looter, Item corpse)
    {
        if (!LootingIsACrime) return false;
        if (looter.PrivLevel >= PrivLevel.GM) return false;

        // Own corpse is not criminal — UUID check first, then Serial fallback
        if (corpse.TryGetTag("OWNER_UUID", out string? ownerUuidStr) &&
            Guid.TryParse(ownerUuidStr, out Guid ownerUuid) &&
            ownerUuid == looter.Uuid)
            return false;

        if (corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
            uint.TryParse(ownerStr, out uint ownerUid) &&
            ownerUid == looter.Uid.Value)
            return false;

        // Resolve the still-living owner. Source-X CheckCorpseCrime keys off the
        // corpse's owner-ghost link: if the owner no longer exists, looting is never
        // a crime. A normal NPC is deleted on death (its corpse has no living
        // owner), so monster corpses are free to loot; only a corpse whose owner is
        // a still-present, innocent player makes looting criminal.
        if (!corpse.TryGetTag("OWNER_UID", out string? ownerUidStr) ||
            !uint.TryParse(ownerUidStr, out uint ownerUid2))
            return false;

        var ownerSerial = new Serial(ownerUid2);
        var owner = _world.FindChar(ownerSerial);
        if (owner == null || owner.IsDeleted) return false; // owner gone (NPC corpse)
        if (!owner.IsPlayer) return false;                  // creature corpse — free loot
        if (owner.IsCriminal || owner.IsMurderer) return false; // looting a red/criminal is allowed

        // Looting a party member's corpse is not criminal when that member granted
        // loot rights. The flag belongs to the CORPSE OWNER ("party may loot me"), not
        // the looter — checking the looter's own flag was inverted (a looter who
        // enabled their own flag could freely loot every party member).
        if (PartyManager != null)
        {
            var party = PartyManager.FindParty(looter.Uid);
            if (party != null && party.IsMember(ownerSerial) && party.GetLootFlag(ownerSerial))
                return false;
        }

        // Guild member is not criminal (same guild = shared loot rights)
        var guildMgr = Character.ResolveGuildManager?.Invoke(looter.Uid);
        if (guildMgr != null)
        {
            var looterGuild = guildMgr.FindGuildFor(looter.Uid);
            var ownerGuild = guildMgr.FindGuildFor(ownerSerial);
            if (looterGuild != null && ownerGuild != null && looterGuild == ownerGuild)
                return false;
        }

        return true;
    }

    /// <summary>The corpse's owner, when one is still around to be wronged.
    /// Source-X CheckCorpseCrime keys the whole rule off this link
    /// (pCorpse->m_uidLink.CharFind(), CItemCorpse.cpp:123).</summary>
    public Character? ResolveCorpseOwner(Item corpse)
    {
        if (!corpse.TryGetTag("OWNER_UID", out string? ownerUidStr) ||
            !uint.TryParse(ownerUidStr, out uint ownerUid))
            return null;
        var owner = _world.FindChar(new Serial(ownerUid));
        return owner is { IsDeleted: false } ? owner : null;
    }

    /// <summary>Answer for looting or carving someone else's corpse.
    ///
    /// Source-X CheckCorpseCrime does TWO things when the act is criminal
    /// (CItemCorpse.cpp:132-133): it runs the WITNESS pipeline with the corpse's
    /// owner as the mark — an overt crime, so everyone in line of sight notices
    /// it — and only then flags the criminal. Only the flag was raised here: the
    /// people watching recorded no SAWCRIME (so the looter did not even show grey
    /// to them), a guarded town's NPCs never called the guards, and @SeeCrime
    /// never fired for a script to react to.</summary>
    public void ReportCorpseCrime(Character criminal, Item corpse)
    {
        // SKILL_NONE in the reference: looting is not a covert act, so there is
        // no perception contest — line of sight is the whole test.
        CrimeWitnessService.CheckCrimeSeen(_world, criminal, ResolveCorpseOwner(corpse),
            skillToSee: null, Random.Shared);
        criminal.MakeCriminal();
    }

    /// <summary>
    /// Carve a corpse with a blade (Source-X CChar::Use_CarveCorpse).
    ///
    /// The parts come from the RESOURCES of the creature type the corpse was made
    /// from. @CarveCorpse sees them as LOCAL.resource.N.ID / .amount (ARGN1 = the
    /// count, ARGO = the carving item), may rewrite them, and RETURN 1 cancels the
    /// carve. A player's parts fall to the ground renamed "&lt;part&gt; of &lt;victim&gt;"
    /// and the corpse turns to bones at once; a creature's parts go into its
    /// corpse. A corpse with no type or already carved yields nothing.
    /// </summary>
    public List<Item> CarveCorpse(Character carver, Item corpse, Item? carvingItem = null)
    {
        var results = new List<Item>();
        if (corpse.ItemType != ItemType.Corpse) return results;

        var charDef = ResolveCorpseCharDef(corpse);
        // Once per corpse (Source-X m_carved). Forensics reads CORPSE_CARVED, so
        // use that tag name here too (the old "CARVED" tag was never read back).
        if (charDef == null || corpse.TryGetTag("CORPSE_CARVED", out _) || corpse.TryGetTag("CARVED", out _))
        {
            SendCarveMessage(carver, Msg.CarveCorpseNothing);
            return results;
        }

        Character? owner = ResolveCorpseOwner(corpse);
        var pos = corpse.GetTopLevelObj().Position;

        PlayCarveAnimation(carver);
        if (corpse.TryGetTag("BLOOD", out string? bloodTag) && long.TryParse(bloodTag, out long bloodOn) && bloodOn != 0)
            SpillCarveBlood(charDef, pos);

        var resources = Definitions.DefinitionLoader.StaticResources;
        int total = charDef.CarveResources.Count;
        var args = new TriggerArgs
        {
            CharSrc = carver,
            ItemSrc = corpse,
            N1 = total,
            O1 = carvingItem,
            Locals = new SphereNet.Scripting.Variables.VarMap()
        };
        for (int i = 0; i < total; i++)
        {
            var (_, amount, defName) = charDef.CarveResources[i];
            int defIndex = resources != null ? Definitions.TemplateEngine.ResolveItemDefIndex(resources, defName) : 0;
            if (defIndex == 0)
                continue; // not an ITEMDEF (Source-X skips non-RES_ITEMDEF rows)
            args.Locals.SetInt($"resource.{i}.ID", defIndex);
            args.Locals.SetInt($"resource.{i}.amount", amount);
        }

        if (TriggerDispatcher?.FireItemTrigger(corpse, ItemTrigger.CarveCorpse, args) == TriggerResult.True)
            return results;

        bool playerCorpse = owner?.IsPlayer ?? false;
        string victimName = owner?.GetDisplayName()
            ?? (corpse.TryGetTag("CORPSE_NAME", out string? vn) ? vn ?? "" : "");

        for (int i = 0; i < total; i++)
        {
            int defIndex = ReadCarvedItemIndex(args.Locals, $"resource.{i}.ID", resources);
            if (defIndex == 0)
                break; // ITEMID_NOTHING ends the list
            int qty = (int)Math.Clamp(args.Locals.GetInt($"resource.{i}.amount"), 0, ushort.MaxValue);

            var idef = Definitions.DefinitionLoader.GetItemDef(defIndex);
            ushort dispId = Definitions.ItemDefHelper.CreateGraphic(idef, defIndex);
            if (dispId == 0)
                continue;

            var part = _world.CreateItem();
            part.BaseId = dispId;
            Definitions.ItemDefHelper.ApplyInstanceMetadata(part, defIndex,
                setDisplayId: false, setName: false);
            if (idef != null && !string.IsNullOrWhiteSpace(idef.Name))
                part.Name = idef.Name;
            results.Add(part);

            switch (part.ItemType)
            {
                case ItemType.Food:
                case ItemType.FoodRaw:
                case ItemType.MeatRaw:
                    SendCarveMessage(carver, Msg.CarveCorpseMeat);
                    break;
                case ItemType.Hide:
                    SendCarveMessage(carver, Msg.CarveCorpseHides);
                    // RACIALF_HUMAN_WORKHORSE: humans find 10% more hides.
                    if ((((RacialFlags)Character.RacialFlags) & RacialFlags.HumanWorkhorse) != 0 && carver.IsHuman)
                        qty = qty * 110 / 100;
                    break;
                case ItemType.Feather:
                    SendCarveMessage(carver, Msg.CarveCorpseFeathers);
                    break;
                case ItemType.Wool:
                    SendCarveMessage(carver, Msg.CarveCorpseWool);
                    break;
            }

            if (qty > 1)
                part.Amount = (ushort)Math.Min(qty, ushort.MaxValue);

            if (playerCorpse)
            {
                part.Name = ServerMessages.GetFormatted(Msg.CorpseName, part.GetName(), victimName);
                part.Link = owner!.Uid;
                _world.PlaceItemWithDecay(part, pos);
                continue;
            }
            AddToCorpseOrGround(corpse, part);
        }

        if (results.Count == 0)
            SendCarveMessage(carver, Msg.CarveCorpseNothing);

        // Source-X CheckCorpseCrime(fLooting=false): carving an innocent
        // player's corpse is as criminal as looting it, witnesses and all.
        if (IsLootingCriminal(carver, corpse))
            ReportCorpseCrime(carver, corpse);

        corpse.SetTag("CORPSE_CARVED", "1");
        corpse.SetTag("KILLER_UID", carver.Uid.Value.ToString());   // m_uidKiller = carver
        corpse.SetTag("KILLER_UUID", carver.Uuid.ToString("D"));

        // A carved player corpse turns to bones right away (SetTimeout(0)).
        if (playerCorpse)
            corpse.SetDecayAt(Environment.TickCount64);
        return results;
    }

    /// <summary>The creature type a corpse carves as: the chardef stamped at
    /// death, else (corpses made before that stamp existed) the owner's
    /// definition or the corpse body.</summary>
    private SphereNet.Scripting.Definitions.CharDef? ResolveCorpseCharDef(Item corpse)
    {
        if (corpse.TryGetTag("CORPSE_CHARDEF", out string? idx) && int.TryParse(idx, out int defIndex))
            return Definitions.DefinitionLoader.GetCharDef(defIndex);
        var owner = ResolveCorpseOwner(corpse);
        if (owner != null)
        {
            int ownerDef = ResolveCorpseCharDefIndex(owner);
            if (ownerDef != 0)
                return Definitions.DefinitionLoader.GetCharDef(ownerDef);
        }
        return Definitions.DefinitionLoader.GetCharDef(corpse.Amount)
            ?? Definitions.DefinitionLoader.GetCharDefByBody(corpse.Amount);
    }

    /// <summary>LOCAL.resource.N.ID read back as an item definition (Source-X
    /// GetKeyNum + ResGetIndex): a number, or a defname a script wrote.</summary>
    private static int ReadCarvedItemIndex(SphereNet.Scripting.Variables.VarMap locals, string key,
        SphereNet.Scripting.Resources.ResourceHolder? resources)
    {
        if (!locals.Has(key))
            return 0;
        if (locals.IsInteger(key))
            return (int)locals.GetInt(key);
        long n = locals.GetInt(key, long.MinValue);
        if (n != long.MinValue)
            return (int)n;
        string? text = locals.Get(key);
        return resources != null && !string.IsNullOrWhiteSpace(text)
            ? Definitions.TemplateEngine.ResolveItemDefIndex(resources, text)
            : 0;
    }

    private static void SendCarveMessage(Character carver, string msgKey) =>
        Objects.ObjBase.ResolveClientConsole?.Invoke(carver)?.SysMessage(ServerMessages.Get(msgKey));

    /// <summary>UpdateAnimate(ANIM_BOW) for the carver, through the one animation
    /// door (body/mount translation and the per-viewer packet).</summary>
    private static void PlayCarveAnimation(Character carver) =>
        Clients.GameClient.PlayAnimation(carver, (ushort)AnimationType.Bow, 18,
            Character.BroadcastNearby, forEachClientInRange: null);

    /// <summary>A corpse flagged TAG.BLOOD leaves a pool (ITEMID_BLOOD4) in the
    /// creature's blood hue for five seconds.</summary>
    private void SpillCarveBlood(SphereNet.Scripting.Definitions.CharDef charDef, Point3D pos)
    {
        var blood = _world.CreateItem();
        blood.BaseId = 0x122D; // ITEMID_BLOOD4
        blood.Hue = new Color(unchecked((ushort)charDef.BloodColor));
        _world.PlaceItemWithDecay(blood, pos, 5000);
    }

    private void AddToPackOrGround(Character ch, Item item)
    {
        var pack = ch.Backpack;
        if (pack == null || !pack.TryAddItem(item))
            _world.PlaceItem(item, ch.Position);
    }

    private void AddToCorpseOrGround(Item corpse, Item item)
    {
        if (!corpse.TryAddItem(item))
            _world.PlaceItemWithDecay(item, corpse.Position);
        else
            Item.OnVisualUpdate?.Invoke(item); // redraw it in any open corpse gump
    }

    // Corpse decay is now driven by the per-item Item.DecayTime /
    // Item.OnTick path (see Program.cs wiring of Item.OnCorpseDecay).
    // The full-world scan that used to live here burned ~100 ms per
    // tick on busy worlds and fought the sector-sleep design.
}
