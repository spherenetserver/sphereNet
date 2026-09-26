using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Game.Objects;
using SphereNet.Game.Gumps;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;
using SphereNet.Game.Messages;
using ScriptDbAdapter = SphereNet.Scripting.Execution.ScriptDbAdapter;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>Script-verb handler (decomposition phase 3) — the members
    /// below delegate so every call site (including the ITextConsole
    /// interface map) stays unchanged. The logic lives in
    /// <see cref="ClientScriptConsoleHandler"/>.</summary>
    internal ClientScriptConsoleHandler ScriptVerbs => _scriptVerbs ??= new ClientScriptConsoleHandler(this);
    private ClientScriptConsoleHandler? _scriptVerbs;

    public bool TryExecuteScriptCommand(IScriptObj target, string key, string args, ITriggerArgs? triggerArgs) =>
        ScriptVerbs.TryExecuteScriptCommand(target, key, args, triggerArgs);

    public bool TryResolveScriptVariable(string varName, IScriptObj target, ITriggerArgs? triggerArgs, out string value) =>
        ScriptVerbs.TryResolveScriptVariable(varName, target, triggerArgs, out value);

    public IReadOnlyList<IScriptObj> QueryScriptObjects(string query, IScriptObj target, string args, ITriggerArgs? triggerArgs) =>
        ScriptVerbs.QueryScriptObjects(query, target, args, triggerArgs);

    internal bool CanSendStatusFor(Character ch)
    {
        if (_character == null)
            return false;
        if (ch == _character)
            return true;
        if (ch.MapIndex != _character.MapIndex)
            return false;

        int range = Math.Max(5, (int)_netState.ViewRange);
        return _character.Position.GetDistanceTo(ch.Position) <= range;
    }

    private static string ResolveStatusName(Character ch)
    {
        string name = ch.GetName()
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (name.Length > 0)
            return name;

        var def = DefinitionLoader.GetCharDef(ch.CharDefIndex);
        name = def?.Name?.Trim() ?? "";
        if (name.Length == 0)
            name = def?.DefName?.Trim() ?? "";

        if (name.Length == 0)
            name = ch.IsPlayer ? "Player" : $"NPC_{ch.BodyId:X}";

        return name.Length > 30 ? name[..30] : name;
    }

    /// <summary>Source-X addSkillWindow(skill): a single update lets the client
    /// report a skill delta instead of silently replacing the complete list.</summary>
    public void SendSkillUpdate(SkillType skill)
    {
        if (_character == null || (uint)skill >= (uint)SkillType.Qty) return;

        ushort raw = _character.GetSkill(skill);
        ushort adjusted = (ushort)Math.Clamp(SkillEngine.GetAdjustedSkill(_character, skill), 0, ushort.MaxValue);
        ushort cap = (ushort)SkillEngine.GetSkillDisplayCap(_character, skill);
        _netState.Send(new PacketSkillSingle((ushort)skill, adjusted, raw,
            _character.GetSkillLock(skill), cap));
    }

    public void SendSkillList()
    {
        if (_character == null) return;

        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserSkills,
            new TriggerArgs { CharSrc = _character });

        var skills = new List<(ushort Id, ushort Value, ushort RawValue, byte Lock, ushort Cap)>();
        for (int i = 0; i < (int)SkillType.Qty && i < 58; i++)
        {
            // 0x3A field order: modified value (with stat + equipment bonuses)
            // then raw base. Reference send.cpp sends Skill_GetAdjusted as the
            // value; the base is the second word.
            ushort raw = _character.GetSkill((SkillType)i);
            ushort val = (ushort)Math.Clamp(
                Skills.SkillEngine.GetAdjustedSkill(_character, (SkillType)i), 0, ushort.MaxValue);
            byte lockState = _character.GetSkillLock((SkillType)i);
            ushort cap = (ushort)Skills.SkillEngine.GetSkillDisplayCap(_character, (SkillType)i);
            skills.Add(((ushort)i, val, raw, lockState, cap));
        }
        _netState.Send(new PacketSkillList(skills.ToArray()));
    }

    internal void SendPickupFailed(byte reason)
    {
        var buf = new PacketBuffer(2);
        buf.WriteByte(0x27);
        buf.WriteByte(reason);
        _netState.Send(buf);
    }

    internal static (uint Serial, ushort ItemId, byte Layer, ushort Hue)[] BuildEquipmentList(Character ch)
    {
        var list = new List<(uint, ushort, byte, ushort)>();
        for (int i = 0; i <= (int)Layer.Horse; i++)
        {
            var item = ch.GetEquippedItem((Layer)i);
            if (item == null) continue;
            list.Add((item.Uid.Value, item.DispIdFull, (byte)i, item.Hue));
        }
        return list.ToArray();
    }

    /// <summary>
    /// Build the network MobileFlags byte (0x77/0x78/0x20 packets).
    ///
    /// Wire format mirrors ClassicUO <c>MobileFlags</c>:
    ///   0x01 = Frozen
    ///   0x02 = Female  (NOT "Dead" — ghost state is read from body ID
    ///                   0x192/0x193 client-side; setting 0x02 on a male
    ///                   ghost makes ClassicUO short-circuit
    ///                   <c>CheckGraphicChange()</c> because the cached
    ///                   IsFemale state contradicts the male body, and
    ///                   the sprite stays on the previous human atlas —
    ///                   the root cause of the "ghost body never
    ///                   renders" bug for both self and staff observers
    ///                   in the death rebuild logs.)
    ///   0x04 = Flying / Poisoned (client interprets per body type)
    ///   0x08 = YellowBar
    ///   0x10 = IgnoreMobiles
    ///   0x40 = WarMode
    ///   0x80 = Hidden / Invisible
    ///
    /// Female is derived from the body ID (Source-X
    /// <c>CChar::IsFemale()</c> returns the same lookup): human female
    /// = 0x191, female ghost = 0x193.
    /// </summary>
    /// <summary>The hues that turn a state's "draw me greyed out" bit off. Upstream
    /// keeps them as COLORINVIS / COLORHIDDEN / COLORINVISSPELL: a shard that gives
    /// one of those states its own colour does NOT want the client's grey overlay on
    /// top of it, so the INVIS bit is withheld for that state (CCharStatus.cpp:687).
    /// Wired from the ini at startup; zero (the default) means "use the grey".</summary>
    public static ushort ColorInvisHue, ColorHiddenHue, ColorInvisSpellHue;

    /// <summary>The mobile flags byte, as the VIEWER must read it.
    ///
    /// Source-X builds it per viewer (CChar::GetModeFlag, CCharStatus.cpp:659-702)
    /// and the viewer matters: bit 0x04 is POISONED for a pre-Stygian-Abyss client
    /// and FLYING for a newer one - the same bit, two meanings, decided by who is
    /// looking (ClassicUO Mobile.cs:126/145 reads it exactly that way).</summary>
    internal static byte BuildMobileFlagsFor(Character ch, NetState? viewer)
    {
        byte flags = 0;

        // Frozen covers being turned to stone as well (STATF_FREEZE|STATF_STONE).
        if (ch.IsStatFlag(StatFlag.Freeze) || ch.IsStatFlag(StatFlag.Stone))
            flags |= 0x01;

        // Female. Upstream asks the DEFINITION; the body is what this engine has,
        // and the ghost bodies count too - a dead woman is still drawn as one.
        if (IsFemaleBody(ch.BodyId))
            flags |= 0x02;

        // 0x04: one bit, two meanings, chosen by the viewer's client.
        bool viewerIsModern = viewer == null ||
            viewer.SupportsStygianAbyss || viewer.IsEnhancedClient || viewer.IsKingdomRebornClient;
        if (viewerIsModern)
        {
            if (ch.IsStatFlag(StatFlag.Hovering)) flags |= 0x04;   // gargoyle flight
        }
        else
        {
            if (ch.IsStatFlag(StatFlag.Poisoned)) flags |= 0x04;   // green health bar
        }

        if (ch.IsStatFlag(StatFlag.Invul)) flags |= 0x08;          // yellow health bar
        if (ch.PrivLevel > PrivLevel.Player) flags |= 0x10;        // staff walk through mobiles
        if (ch.IsInWarMode) flags |= 0x40;

        // The grey overlay. Upstream folds four states into this one bit and lets a
        // shard opt each of them out by giving that state its own hue instead.
        bool greyed = ch.IsStatFlag(StatFlag.Sleeping)
            || (ColorInvisHue == 0 && ch.IsStatFlag(StatFlag.Insubstantial))
            || (ColorHiddenHue == 0 && ch.IsStatFlag(StatFlag.Hidden))
            || (ColorInvisSpellHue == 0 && ch.IsStatFlag(StatFlag.Invisible));
        if (greyed) flags |= 0x80;

        return flags;
    }

    /// <summary>Female player bodies, living and ghost, across the three races.</summary>
    private static bool IsFemaleBody(ushort bodyId) => bodyId is
        0x0191 or 0x0193 or        // human, and her ghost
        0x025E or 0x0260 or        // elf
        0x029B or 0x02B7;          // gargoyle

    /// <summary>This client's own reading of another mobile's flags.</summary>
    internal byte BuildMobileFlags(Character ch) => BuildMobileFlagsFor(ch, _netState);

    /// <summary>
    /// Turn the player to face <paramref name="target"/>, update the player's own
    /// client, and broadcast the new facing to nearby clients via 0x77. Mirrors
    /// Source-X <c>CChar::UpdateDir(pCharTarg)</c> -> <c>UpdateMove(GetTopPoint())</c>:
    /// when an attacker starts a swing or a spell, the engine first turns the
    /// caster/attacker so that the animation plays in the correct direction.
    /// Without this, melee/cast animations look broken from the side and bow
    /// shots may visually fly the wrong way.
    ///
    /// Source-X's <c>UpdateMove</c> also calls <c>addPlayerView</c> /
    /// <c>addPlayerUpdate</c> for the mover's OWN client (0x20 + walk-seq reset),
    /// so in top-down view the attacker sees their own avatar turn. We mirror
    /// that with <see cref="SendSelfRedraw"/> (0x20 PlayerUpdate + 0x78 to keep
    /// equipment for ClassicUO + WalkSequence reset). Skips everything when the
    /// facing is already correct, to avoid packet spam during continuous combat.
    /// </summary>
    public void FaceTarget(Character target)
    {
        if (_character == null || target == null) return;
        if (target.Position.Equals(_character.Position)) return;

        var newDir = _character.Position.GetDirectionTo(target.Position);
        if (newDir == _character.Direction) return;

        _character.Direction = newDir;

        byte flags = BuildMobileFlags(_character);
        byte noto = GetNotoriety(_character);
        byte dirByte = (byte)((byte)_character.Direction & 0x07);

        var pkt = new PacketMobileMoving(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z, dirByte,
            _character.Hue, flags, noto);

        if (BroadcastMoveNearby != null)
            BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, pkt, _character.Uid.Value, _character);
        else
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, pkt, _character.Uid.Value);

        // Source-X parity: the mover's own client gets addPlayerUpdate so the
        // local avatar visibly turns toward the target (UO is top-down).
        SendSelfRedraw();
    }

    /// <summary>A command line run as if this client's player typed it after the
    /// command prefix (the admin panel's "run as the character"): the player's own
    /// plevel gates it and its output reaches this client.</summary>
    public bool RunCommandAsPlayer(string commandLine) =>
        TryHandleCommandSpeech("." + commandLine.TrimStart('.', '/'));

    internal bool TryHandleCommandSpeech(string text)
    {
        if (_character == null || _commands == null)
            return false;

        // Some clients may prepend invisible/null whitespace-like chars in unicode speech.
        // Normalize before checking command prefix to keep command parsing resilient.
        string normalized = text
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .TrimStart(' ', '\t', '\r', '\n');
        if (normalized.Length <= 1)
            return false;

        char prefix = _commands.CommandPrefix;
        // Accept '.' and '/' regardless of configured prefix for Source-X compatibility.
        if (normalized[0] != prefix && normalized[0] != '.' && normalized[0] != '/')
            return false;

        string commandLine = normalized[1..]
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (string.IsNullOrEmpty(commandLine))
            return true;

        _logger.LogDebug("[command_dispatch] account={Account} char=0x{Char:X8} raw='{Raw}' normalized='{Norm}' prefix='{Prefix}' cmd='{Cmd}'",
            _account?.Name ?? "?", _character.Uid.Value, text, normalized, prefix, commandLine);

        var posBefore = _character.Position;
        byte speedModeBefore = _character.SpeedMode;
        var result = _commands.TryExecute(_character, commandLine);
        // Source-X CClient::Event_Command logs every command at or above COMMANDLOG
        // as "'name' commands 'line'=allowed" (CClientEvent.cpp:1113). It sat at Debug
        // here, below the file log's level, so no staff command ever reached the log.
        if (_commands.CommandLogPrivLevel >= 0 && (int)_character.PrivLevel >= _commands.CommandLogPrivLevel)
            _logger.LogWarning("[AUDIT] [game] account={Account} char=0{Char:X8} '{Name}' plevel={PLevel} commands '{Cmd}'={Allowed}",
                _account?.Name ?? "?", _character.Uid.Value, _character.Name, (int)_character.PrivLevel,
                commandLine, result == CommandResult.Executed ? 1 : 0);
        switch (result)
        {
            case CommandResult.Executed:
                if (_character.SpeedMode != speedModeBefore)
                    SendSpeedMode();
                if (!_character.Position.Equals(posBefore))
                {
                    // Teleport-like commands (.GO, .JAIL, script-based moves, etc.) must
                    // force a client-side world refresh so the player actually relocates.
                    Resync();
                    _mountEngine?.EnsureMountedState(_character);
                    BroadcastDrawObject(_character);
                }
                return true;

            case CommandResult.InsufficientPriv:
                var required = _commands.GetRequiredPrivLevel(commandLine) ?? PrivLevel.Counsel;
                SysMessage(ServerMessages.GetFormatted("gm_insuf_priv", required, _character.PrivLevel));
                _logger.LogDebug("[command_priv_reject] account={Account} accountPLEVEL={AccLvl} char=0x{Char:X8} charPrivLevel={ChLvl} cmd='{Cmd}' required={Req}",
                    _account?.Name ?? "?", _account?.PrivLevel, _character.Uid.Value, _character.PrivLevel, commandLine, required);
                return true;

            case CommandResult.NotFound:
                SysMessage(ServerMessages.GetFormatted("cmd_invalid", commandLine));
                _logger.LogDebug("[speech_fallback] account={Account} char=0x{Char:X8} unknownCmd='{Cmd}'",
                    _account?.Name ?? "?", _character.Uid.Value, commandLine);
                return true;

            case CommandResult.Failed:
                _logger.LogDebug("[command_failed] account={Account} char=0x{Char:X8} cmd='{Cmd}'",
                    _account?.Name ?? "?", _character.Uid.Value, commandLine);
                return true;

            default:
                return false;
        }
    }

    internal void SetWarMode(bool warMode, bool syncClients, bool preserveTarget)
    {
        if (_character == null) return;

        bool oldState = _character.IsInWarMode;
        if (warMode)
            _character.SetStatFlag(StatFlag.War);
        else
            _character.ClearStatFlag(StatFlag.War);

        // Source-X ghost manifest (Event_CombatMode): a dead char's war toggle
        // flips STATF_INSUBSTANTIAL — war = manifest (visible to the living),
        // peace = insubstantial. The view filter reads this flag, and scripts
        // see/set the same bit as in the reference.
        if (_character.IsDead)
        {
            if (warMode)
                _character.ClearStatFlag(StatFlag.Insubstantial);
            else
                _character.SetStatFlag(StatFlag.Insubstantial);
        }

        if (!warMode && !preserveTarget)
        {
            _character.FightTarget = Serial.Invalid;
            _character.NextAttackTime = 0;
        }
        if (!warMode)
            _character.ClearPendingHit();

        if (!syncClients) return;

        // Send 0x72 war mode confirmation — client expects this to actually toggle
        _netState.Send(new PacketWarModeResponse(warMode));

        // Broadcast appearance update to nearby players. For a LIVING
        // character a single 0x77 update is enough — every observer
        // already has the mobile in their world.Mobiles.
        //
        // Ghosts are special. While in peace mode the ghost is hidden
        // from plain observers via the BuildViewDelta filter (their
        // View.KnownChars never tracked the mobile). A blanket 0x77
        // broadcast on a peace→war transition would target a serial
        // that ClassicUO doesn't know about and silently drop.
        // So for a manifesting/un-manifesting ghost we skip the
        // BroadcastNearby and use per-observer dispatch instead:
        //
        //   peace → war (manifest):
        //     plain observer  → 0x78 PacketDrawObject (hue 0x4001
        //                       translucent grey) + cache add so the
        //                       next view-delta tick doesn't double-spawn
        //     staff observer  → 0x77 normal update (already had the
        //                       ghost mobile in cache)
        //
        //   war → peace (un-manifest):
        //     plain observer  → 0x1D delete + cache drop
        //     staff observer  → 0x77 normal update
        //
        //   self                → 0x77 always (own client always knows
        //                         the mobile)
        byte warFlags = BuildMobileFlags(_character);
        byte warNoto = GetNotoriety(_character);
        var mobileMoving = new PacketMobileMoving(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z, (byte)_character.Direction,
            _character.Hue, warFlags, warNoto);

        if (_character.IsDead && ForEachClientInRange != null)
        {
            uint selfUid = _character.Uid.Value;

            ForEachClientInRange.Invoke(_character.Position, UpdateRange, selfUid,
                (observerCh, observerClient) =>
                {
                    bool isStaff = observerCh.AllShow ||
                        observerCh.PrivLevel >= Core.Enums.PrivLevel.Counsel;
                    if (isStaff)
                    {
                        observerClient.Send(mobileMoving);
                        return;
                    }

                    if (warMode)
                    {
                        // Manifest: spawn ghost as translucent grey on this
                        // plain observer's client and start tracking it so
                        // BuildViewDelta will keep it in sync (manifested
                        // ghosts are now in the delta filter's allow-list).
                        // NotifyCharacterAppear handles the hue=0x4001 draw
                        // and View.KnownChars insert in one call (mirroring the
                        // login/teleport entry path).
                        observerClient.NotifyCharacterAppear(_character);
                    }
                    else
                    {
                        // Un-manifest: drop the mobile from the plain
                        // observer's view so they no longer see it.
                        observerClient.RemoveKnownChar(selfUid, sendDelete: true);
                    }
                });
        }
        else
        {
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, mobileMoving,
                _character.Uid.Value);
        }

        _logger.LogDebug("[war_mode] client={ClientId} char=0x{Char:X8} {Old}->{New}",
            _netState.Id, _character.Uid.Value, oldState ? "war" : "peace", _character.IsInWarMode ? "war" : "peace");
    }

    public static int GetSwingDelayMs(Character attacker, Item? weapon)
        => CombatEngine.GetSwingDelayMs(attacker, weapon);

    /// <summary>The swing animation - Source-X Fight_CanHit's
    /// GenerateAnimate(ANIM_ATTACK_WEAPON) (CCharFight.cpp:1925), which the swing then
    /// plays untranslated (:1990). The weapon in hand picks the swing
    /// (sword/axe/pick slash, fencing pierce, the mace family bash, one- or
    /// two-handed; bow and crossbow their own draws), the saddle turns it into the
    /// rider's action and a creature's body into its own attack group
    /// (BodyAnimTranslator.Generate).
    ///
    /// Bare hands swing ANIM_ATTACK_WEAPON itself - the weapon table applies only
    /// when there is a weapon (CCharAct.cpp:814) - and on horseback that is the
    /// horse attack. This sent the wrestling punch (0x1F) and, mounted, the slap;
    /// and every mounted weapon rode as the attack, where upstream's two-handed
    /// swings ride as the slap (:880-883). A player in a creature's body swung the
    /// human groups, which the creature has not got.</summary>
    internal static ushort GetSwingAction(Character attacker, Item? weapon) =>
        BodyAnimTranslator.Generate(attacker, (ushort)AnimationType.AttackWeapon, weapon);

    public static ushort GetNpcSwingAction(Character npc) =>
        GetNpcSwingAction(npc, BodyAnimTranslator.WeaponInHand(npc));

    public static ushort GetNpcSwingAction(Character npc, Item? weapon) =>
        GetSwingAction(npc, weapon);

    /// <summary>The sound of a landed strike: the ATTACKER's SoundChar(CRESND_HIT)
    /// (Fight_Hit, CCharFight.cpp:2222) - the weapon's hit prop or class sound when
    /// armed, the creature's own hit sound (SOUNDHIT or SOUND= + offset) otherwise.
    /// See <see cref="CharacterSounds"/>. 0 = nothing to play.</summary>
    internal static ushort GetAttackerHitSound(Character attacker, Item? weapon) =>
        CharacterSounds.Resolve(attacker, AI.CreatureSoundType.Hit, weapon);

    /// <summary>Fight_Hit's miss sound (CCharFight.cpp:2057-2072).</summary>
    internal static ushort GetWeaponMissSound(Item? weapon) => CharacterSounds.GetMissSound(weapon);

    public static ushort GetAttackerHitSoundPublic(Character attacker, Item? weapon) =>
        GetAttackerHitSound(attacker, weapon);
    public static ushort GetWeaponMissSoundPublic(Item? weapon) => GetWeaponMissSound(weapon);

    /// <summary>The struck character's SoundChar(CRESND_GETHIT) (OnTakeDamage,
    /// CCharFight.cpp:1031): SOUNDGETHIT, else the SOUND= base + offset, else the
    /// human "oomf" set. 0 = nothing to play.</summary>
    internal static ushort GetDefenderHitSound(Character defender) =>
        CharacterSounds.Resolve(defender, AI.CreatureSoundType.GetHit);

    public static ushort GetDefenderHitSoundPublic(Character defender) => GetDefenderHitSound(defender);

    /// <summary>Compute the UO notoriety byte for <paramref name="ch"/> as
    /// seen by this client's character. The client reads this byte (part of
    /// 0x77/0x78/etc.) to pick the overhead-name hue:
    /// 1=blue/innocent, 2=green/friend, 3=grey/neutral NPC,
    /// 4=grey/criminal, 5=orange/enemy-guild, 6=red/murderer,
    /// 7=yellow/invul. Returning 1 for everyone (as we did until now)
    /// rendered every mobile in neutral grey. Source-X:
    /// CChar::Noto_GetFlag / Noto_CalcFlag in CCharNotoriety.cpp.</summary>
    /// <summary>Compute notoriety byte for <paramref name="subject"/> as seen by
    /// <paramref name="viewer"/>. Used by per-observer combat/move broadcasts.</summary>
    public static byte ComputeNotoriety(GameWorld? world, Character? viewer, Character subject)
    {
        byte noto = ComputeNotorietyBase(world, viewer, subject);
        // @NotoSend override gate. The hook is non-null only when a script hooks
        // @NotoSend (TriggerDispatcher.IsCharTriggerUsed), so this hot per-observer
        // path costs just a null check when nothing hooks it.
        var hook = Character.OnNotoSend;
        if (hook != null && viewer != null)
            noto = hook(viewer, subject, noto);
        return noto;
    }

    // Source-X Noto_CalcFlag decision order (CCharNotoriety.cpp): OVERRIDE.NOTO →
    // incognito → invul → arena → [not-self: party → pet-owner/inherit → guild
    // same/ally/war] → evil → viewer-memory grey → criminal → neutral/permagrey →
    // good. Party/guild come BEFORE criminal/murderer: a same-party or same-guild
    // murderer renders GREEN to his fellows.
    private static byte ComputeNotorietyBase(GameWorld? world, Character? viewer, Character subject)
    {
        // TAG.OVERRIDE.NOTO on the subject wins over every computed branch.
        if (subject.TryGetTag("OVERRIDE.NOTO", out string? notoOverride) &&
            byte.TryParse(notoOverride, out byte forced) && forced >= 1 && forced <= 7)
            return forced;

        if (viewer == null)
            return 3;

        // Incognito checked BEFORE invul (an incognito+invul char shows neutral).
        if (subject.IsStatFlag(StatFlag.Incognito))
            return 3;

        if (subject.IsStatFlag(StatFlag.Invul))
            return 7;

        var targetRegion = world?.FindRegion(subject.Position);
        if (targetRegion != null && targetRegion.IsFlag(RegionFlag.Arena))
            return 3;

        // Source-X fSelfCheck: looking at yourself skips party/guild/pet but
        // still resolves evil/criminal — a murderer sees himself red.
        bool selfCheck = subject == viewer;
        bool subjectIsPlayerChar = subject.IsPlayer || subject.TryGetTag("ACCOUNT", out _);

        if (!selfCheck)
        {
            // Same party → green, before any criminal/murderer branch.
            var myParty = Character.ResolvePartyFinder?.Invoke(viewer.Uid);
            if (myParty != null && myParty.IsMember(subject.Uid))
                return 2;

            if (!subjectIsPlayerChar && subject.OwnerSerial.IsValid && world != null)
            {
                // Your own pet renders NEUTRAL by default in Source-X (the
                // OF_PetBehaviorOwnerNeutral flag flips it to true notoriety).
                if (subject.OwnerSerial == viewer.Uid)
                    return 3;
                // PETSINHERITNOTORIETY (CCharNotoriety.cpp:185-200): a pet shows its
                // master's notoriety only when that notoriety's bit (1 << (noto-1))
                // is set in the mask; 0 (the default) disables inheritance.
                if (Character.PetsInheritNotoriety != 0)
                {
                    var owner = world.FindChar(subject.OwnerSerial);
                    if (owner != null && !owner.IsDeleted && owner != subject && owner != viewer)
                    {
                        byte notoMaster = ComputeNotoriety(world, viewer, owner);
                        int bit = notoMaster >= 1 ? 1 << (notoMaster - 1) : 0;
                        if (bit != 0 && (Character.PetsInheritNotoriety & bit) == bit)
                            return notoMaster;
                    }
                }
            }

            // Guild relations — same/ally → green, declared war → orange.
            var guildMgr = Character.ResolveGuildManager?.Invoke(viewer.Uid);
            if (guildMgr != null)
            {
                var myGuild = guildMgr.FindGuildFor(viewer.Uid);
                var theirGuild = guildMgr.FindGuildFor(subject.Uid);
                if (myGuild != null && theirGuild != null)
                {
                    if (myGuild == theirGuild) return 2;
                    if (myGuild.IsAlliedWith(theirGuild.StoneUid)) return 2;
                    if (myGuild.IsAtWarWith(theirGuild.StoneUid)) return 5;
                }
            }
        }

        // skip_guilds:
        if (IsNotoEvil(subject, targetRegion))
            return 6;

        // Personal grey (MEMORY_SAWCRIME | MEMORY_AGGREIVED, CCharNotoriety.cpp:265-271):
        // grey to THIS viewer only — the victim/witness of an otherwise-blue
        // aggressor, NPC or player alike.
        if (!selfCheck &&
            viewer.Memory_FindObjTypes(subject.Uid, MemoryType.SawCrime | MemoryType.Aggreived) != null)
            return 4;

        if (subject.IsCriminal || subject.IsStatFlag(StatFlag.Criminal))
            return 4;

        if (IsNotoNeutral(subject) ||
            (subject.TryGetTag("NOTO.PERMAGREY", out string? pg) && pg == "1"))
            return 3;

        // NOTO_GOOD for everyone else. Healers and bankers are not special here:
        // only STATF_INVUL gives NOTO_INVUL (CCharNotoriety.cpp:152-153, 281).
        return 1;
    }

    /// <summary>Source-X CChar::Noto_IsEvil — murderer, karma-evil player
    /// (PLAYEREVIL), brain-based NPC karma thresholds, and the guarded
    /// RED-zone inversion (murderers are normal there, good karma is not).</summary>
    private static bool IsNotoEvil(Character subject, SphereNet.Game.World.Regions.Region? region)
    {
        short karma = subject.Karma;

        // Red zone inverts: murderers pass as normal, low karma is evil.
        if (region != null && region.IsFlag(RegionFlag.RedZone))
        {
            if (subject.IsMurderer)
                return false;
            if (subject.IsPlayer)
                return karma < Character.PlayerKarmaEvil;
            return karma < 0;
        }

        if (subject.IsMurderer)
            return true;

        switch (subject.NpcBrain)
        {
            case NpcBrainType.Monster:
            case NpcBrainType.Dragon:
                if (!subject.IsPlayer) return karma < 0;
                break;
            case NpcBrainType.Berserk:
                if (!subject.IsPlayer) return true;
                break;
            case NpcBrainType.Animal:
                if (!subject.IsPlayer) return karma <= -800;
                break;
        }

        if (subject.IsPlayer)
            return karma < Character.PlayerKarmaEvil;
        return karma <= -3000;
    }

    /// <summary>Source-X CChar::Noto_IsNeutral — brain/karma neutrality
    /// thresholds; players use PLAYERNEUTRAL.</summary>
    private static bool IsNotoNeutral(Character subject)
    {
        short karma = subject.Karma;
        if (!subject.IsPlayer)
        {
            switch (subject.NpcBrain)
            {
                case NpcBrainType.Monster:
                case NpcBrainType.Berserk:
                    return karma <= 0;
                case NpcBrainType.Animal:
                    return karma <= 100;
            }
        }
        if (subject.IsPlayer)
            return karma < Character.PlayerKarmaNeutral;
        return karma < 0;
    }

    internal byte GetNotoriety(Character ch) => ComputeNotoriety(_world, _character, ch);

    // ==================== ITextConsole ====================

    public PrivLevel GetPrivLevel() => _account?.PrivLevel ?? PrivLevel.Guest;

    public void SysMessage(string text) =>
        SysMessage(text, SphereNet.Game.Messages.ServerMessages.HueOf(SphereNet.Game.Messages.ServerMessages.TalkDefault.System));

    public void SysMessage(string text, ushort hue)
    {
        // Upstream routes every system message through addBarkParse
        // (CClientLog.cpp:200), which is where the `@hue,font,unicode ` prefix a
        // script may have written is taken off the front of the line. Without it
        // the prefix was printed as part of the text.
        var fmt = SpeechPrefix.Parse(ResolveMessage(text), hue);
        if (fmt.Drop)
            return;
        _netState.Send(new PacketSpeechUnicodeOut(
            0xFFFFFFFF, 0xFFFF, 6, fmt.Hue, fmt.Font, PacketSpeechUnicodeOut.SystemLanguage, "System", fmt.Text
        ));
    }

    /// <summary>Send speech from an NPC to this client (overhead text above the NPC).
    /// Hue: Source-X Speak defaults to HUE_TEXT_DEF 0x03B2 (light gray,
    /// uofiles_enums.h:58) unless the char carries SPEECHCOLOROVERRIDE
    /// (CChar.cpp:4147) — honor the per-char override so scripts can color
    /// NPC speech.</summary>
    internal void NpcSpeech(Character npc, string text)
    {
        ushort hue = npc.SpeechColor != 0
            ? npc.SpeechColor
            : SphereNet.Game.Messages.ServerMessages.HueOf(SphereNet.Game.Messages.ServerMessages.TalkDefault.Say);
        var packet = new PacketSpeechUnicodeOut(
            npc.Uid.Value, npc.BodyId, 0, hue,
            SphereNet.Game.Messages.ServerMessages.FontOf(SphereNet.Game.Messages.ServerMessages.TalkDefault.Say), PacketSpeechUnicodeOut.SystemLanguage, npc.GetName(), text);
        BroadcastNearby?.Invoke(npc.Position, 18, packet, 0);
    }

    public string GetName() => _account?.Name ?? "?";

    private string ResolveMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _defMessageLookup == null)
            return text;

        string key = text.Trim();
        if (key.StartsWith('@'))
            key = key[1..];
        if (key.StartsWith("DEFMSG.", StringComparison.OrdinalIgnoreCase))
            key = key[7..];
        if (key.Contains(' '))
            return text;

        return _defMessageLookup(key) ?? text;
    }

    private static string EscapeHtml(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    // ==================== Phase 1: Critical Stability Handlers ====================
}
