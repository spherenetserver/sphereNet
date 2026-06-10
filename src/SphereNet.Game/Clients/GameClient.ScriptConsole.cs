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

    public void SendSkillList()
    {
        if (_character == null) return;

        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserSkills,
            new TriggerArgs { CharSrc = _character });

        var skills = new List<(ushort Id, ushort Value, ushort RawValue, byte Lock, ushort Cap)>();
        for (int i = 0; i < (int)SkillType.Qty && i < 58; i++)
        {
            ushort val = _character.GetSkill((SkillType)i);
            byte lockState = _character.GetSkillLock((SkillType)i);
            ushort cap = (ushort)Skills.SkillEngine.GetSkillDisplayCap(_character, (SkillType)i);
            skills.Add(((ushort)i, val, val, lockState, cap));
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
    internal static byte BuildMobileFlags(Character ch)
    {
        byte flags = 0;
        if (ch.IsInvisible) flags |= 0x80;
        if (ch.IsInWarMode) flags |= 0x40;
        if (ch.BodyId == 0x0191 || ch.BodyId == 0x0193) flags |= 0x02;
        if (ch.IsStatFlag(StatFlag.Freeze)) flags |= 0x01;
        return flags;
    }

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

        if (!warMode && !preserveTarget)
        {
            _character.FightTarget = Serial.Invalid;
            _character.NextAttackTime = 0;
        }

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

    /// <summary>Map a weapon (or bare fists) to the correct humanoid
    /// 0x6E animation action index. Ranged weapons trigger the nock/fire
    /// action; blades use the slash; blunt/maces use the overhead swing;
    /// unarmed uses the wrestling punch. Exact values come from
    /// ServUO MobileAnimation / Source-X AnimationRange tables.</summary>
    internal static ushort GetSwingAction(Character attacker, Item? weapon)
    {
        bool mounted = attacker.IsMounted;

        if (weapon == null)
            return mounted ? (ushort)AnimationType.HorseSlap : (ushort)AnimationType.AttackWrestle;

        bool twoHand = weapon.EquipLayer == Layer.TwoHanded;

        if (mounted)
        {
            return weapon.ItemType switch
            {
                ItemType.WeaponBow => (ushort)AnimationType.HorseAttackBow,
                ItemType.WeaponXBow => (ushort)AnimationType.HorseAttackXBow,
                _ => (ushort)AnimationType.HorseAttack,
            };
        }

        return weapon.ItemType switch
        {
            ItemType.WeaponBow => (ushort)AnimationType.AttackBow,
            ItemType.WeaponXBow => (ushort)AnimationType.AttackXBow,
            ItemType.WeaponSword => twoHand
                ? (ushort)AnimationType.Attack2HSlash : (ushort)AnimationType.AttackWeapon,
            ItemType.WeaponAxe => twoHand
                ? (ushort)AnimationType.Attack2HPierce : (ushort)AnimationType.Attack1HPierce,
            ItemType.WeaponFence => twoHand
                ? (ushort)AnimationType.Attack2HPierce : (ushort)AnimationType.Attack1HPierce,
            ItemType.WeaponMaceSmith or ItemType.WeaponMaceSharp or
            ItemType.WeaponMaceStaff or ItemType.WeaponMaceCrook or
            ItemType.WeaponMacePick or ItemType.WeaponWhip => twoHand
                ? (ushort)AnimationType.Attack2HBash : (ushort)AnimationType.Attack1HBash,
            ItemType.WeaponThrowing => (ushort)AnimationType.Attack2HBash,
            _ => (ushort)AnimationType.AttackWeapon,
        };
    }

    public static ushort GetNpcSwingAction(Character npc)
    {
        // Non-humanoid bodies use their own anim.mul attack groups
        // (monster 4-6, animal 5-6) via the body translation table.
        if (!BodyAnimTranslator.IsHumanoidBody(npc.BodyId))
            return BodyAnimTranslator.Translate(npc.BodyId, (ushort)AnimationType.AttackWrestle);

        var weapon = npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        return GetSwingAction(npc, weapon);
    }

    internal static ushort GetSwingSound(Item? weapon)
    {
        if (weapon == null)
            return 0x023A;
        return weapon.ItemType switch
        {
            Core.Enums.ItemType.WeaponBow or
            Core.Enums.ItemType.WeaponXBow => 0x0223,
            Core.Enums.ItemType.WeaponSword or
            Core.Enums.ItemType.WeaponAxe or
            Core.Enums.ItemType.WeaponFence => 0x023B,
            Core.Enums.ItemType.WeaponMaceSmith or
            Core.Enums.ItemType.WeaponMaceSharp or
            Core.Enums.ItemType.WeaponMaceStaff or
            Core.Enums.ItemType.WeaponMaceCrook or
            Core.Enums.ItemType.WeaponMacePick or
            Core.Enums.ItemType.WeaponWhip => 0x023D,
            Core.Enums.ItemType.WeaponThrowing => 0x0238,
            _ => 0x023B,
        };
    }

    public static ushort GetSwingSoundPublic(Item? weapon) => GetSwingSound(weapon);

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

    private static byte ComputeNotorietyBase(GameWorld? world, Character? viewer, Character subject)
    {
        if (viewer == null)
            return 3;
        if (subject == viewer)
            return 1;

        if (subject.IsStatFlag(StatFlag.Invul))
            return 7;

        if (subject.IsStatFlag(StatFlag.Incognito))
            return 3;

        var targetRegion = world?.FindRegion(subject.Position);
        if (targetRegion != null && targetRegion.IsFlag(RegionFlag.Arena))
            return 3;

        bool isRedZone = targetRegion != null && targetRegion.IsFlag(RegionFlag.RedZone);
        if (isRedZone)
        {
            if (subject.IsMurderer) return 6;
            if (subject.Karma > 0) return 1;
        }

        if (subject.IsMurderer)
            return 6;

        if (subject.IsCriminal || subject.IsStatFlag(StatFlag.Criminal))
            return 4;

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

        var myParty = Character.ResolvePartyFinder?.Invoke(viewer.Uid);
        if (myParty != null && myParty.IsMember(subject.Uid))
            return 2;

        if (subject.TryGetTag("NOTO.PERMAGREY", out string? pg) && pg == "1")
            return 3;

        bool isActuallyPlayer = subject.IsPlayer || subject.TryGetTag("ACCOUNT", out _);
        if (!isActuallyPlayer)
            return GetNpcNotoriety(subject);

        return 1;
    }

    internal byte GetNotoriety(Character ch) => ComputeNotoriety(_world, _character, ch);

    /// <summary>Notoriety for non-player mobiles. Source-X Noto_CalcFlag
    /// for NPCs mixes brain type and karma:
    ///  - monster / berserk / dragon brain → red (always hostile)
    ///  - healer / banker → yellow (protected / invul-by-role)
    ///  - vendor / stable / guard / human → blue (friendly townfolk)
    ///  - animal → grey (neutral wildlife, huntable)
    ///  - karma overrides: very negative → red, negative → grey criminal,
    ///    very positive → blue — lets scripts flip a normally-blue
    ///    townsfolk into a red renegade via SET KARMA.</summary>
    /// <summary>Source-X Noto_IsEvil + Noto_CalcFlag for NPCs. Evil thresholds
    /// differ per brain type: Monster/Dragon karma&lt;0, Berserk always,
    /// Animal karma&lt;=-800, NPC karma&lt;=-3000.</summary>
    private static byte GetNpcNotoriety(Character ch)
    {
        switch (ch.NpcBrain)
        {
            case NpcBrainType.Monster:
            case NpcBrainType.Dragon:
            case NpcBrainType.Berserk:
                return 6; // always hostile / red
            case NpcBrainType.Healer:
            case NpcBrainType.Banker:
                return 7; // yellow — invul by role
            case NpcBrainType.Guard:
                return 1; // blue — law enforcement
            case NpcBrainType.Vendor:
            case NpcBrainType.Stable:
            case NpcBrainType.Human:
                if (ch.Karma <= -3000) return 6; // evil NPC
                if (ch.Karma <= -500) return 4;  // criminal NPC
                return 1; // friendly
            case NpcBrainType.Animal:
                if (ch.Karma <= -800) return 6; // evil animal
                return 3; // neutral wildlife
            default:
                if (ch.Karma <= -3000) return 6;
                if (ch.Karma <= -500) return 4;
                return ch.Karma > 500 ? (byte)1 : (byte)3;
        }
    }

    // ==================== ITextConsole ====================

    public PrivLevel GetPrivLevel() => _account?.PrivLevel ?? PrivLevel.Guest;

    public void SysMessage(string text)
    {
        string msg = ResolveMessage(text);
        _netState.Send(new PacketSpeechUnicodeOut(
            0xFFFFFFFF, 0xFFFF, 6, 0x0035, 3, "TRK", "System", msg
        ));
    }

    public void SysMessage(string text, ushort hue)
    {
        string msg = ResolveMessage(text);
        _netState.Send(new PacketSpeechUnicodeOut(
            0xFFFFFFFF, 0xFFFF, 6, hue, 3, "TRK", "System", msg
        ));
    }

    /// <summary>Send speech from an NPC to this client (overhead text above the NPC).</summary>
    internal void NpcSpeech(Character npc, string text)
    {
        var packet = new PacketSpeechUnicodeOut(
            npc.Uid.Value, npc.BodyId, 0, 0x03B2, 3, "TRK", npc.GetName(), text);
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
