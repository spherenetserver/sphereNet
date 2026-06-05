using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Messages;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using System.Collections.Concurrent;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using GameRegion = SphereNet.Game.World.Regions.Region;
using SphereNet.Game.World.Regions;
using SphereNet.Panel;
using SphereNet.Server.Admin;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;


namespace SphereNet.Server;

public static partial class Program
{
    private static bool TryGetClientFor(Character ch, out GameClient client) =>
        _clientsByCharUid.TryGetValue(ch.Uid, out client!) && client.Character == ch;

    private static void InitializeGameEngines(string basePath)
    {
            // --- 7b. Game Engines ---
            _log.LogInformation("Initializing game engines...");
            GameClient.WalkBufferMax = Math.Max(1, _config.WalkBuffer);
            GameClient.WalkRegenPerSecond = Math.Max(0, _config.WalkRegen);
            GameClient.MoveToleranceMs = 80;
            // After a movement rejection the client (ClassicUO) clears its step
            // queue, resets its walk sequence to 0 and resends from seq 0. The
            // in-flight steps it had already predicted (seq > 1) arrive next and
            // are now dropped SILENTLY by the stale-seq path — no extra 0x21, no
            // 0x20 redraw — until that fresh seq-0 stream begins. A fixed time
            // window is therefore unnecessary and counter-productive (it also
            // swallowed the fresh seq-0/1 steps, delaying recovery), so it is
            // disabled. See RejectStaleMove for the reasoning.
            GameClient.MoveRejectResyncMs = 0;
            GameClient.MoveViolationKickThreshold = 0;
            GameClient.MovementCreditEnabled = _config.MovementCreditEnabled;
            GameClient.MovementCreditBaseMs = _config.MovementCreditBaseMs;
            GameClient.MovementCreditMaxMs = _config.MovementCreditMaxMs;
            GameClient.MovementQueueCapacity = _config.MovementQueueCapacity;
            GameClient.SpeedHackDetectionEnabled = _config.SpeedHackDetectionEnabled;
            GameClient.SpeedHackRateThreshold = _config.SpeedHackRateThreshold;
            GameClient.SpeedHackBurstWindow = _config.SpeedHackBurstWindow;
            GameClient.SpeedHackHistorySize = _config.SpeedHackHistorySize;
            GameClient.SpeedHackCooldownMs = _config.SpeedHackCooldownMs;

            MovementEngine.WalkDelayFoot = _config.WalkDelayFoot;
            MovementEngine.WalkDelayMount = _config.WalkDelayMount;
            MovementEngine.RunDelayFoot = _config.RunDelayFoot;
            MovementEngine.RunDelayMount = _config.RunDelayMount;

            NetState.RttPingIntervalMs = _config.RttPingIntervalMs;

            _triggerDispatcher = new TriggerDispatcher();
            _triggerDispatcher.Resources = _resources;
            var exprParser = new ExpressionParser
            {
                DiagnosticLogger = msg =>
                {
                    // Surface unresolved script expressions both in the log and in
                    // the GUI console so the user spots missing properties while a
                    // specific command/dialog is running.
                    _log?.LogWarning("{Msg}", msg);
                    ConsoleAppend(msg);
                }
            };
            var scriptInterpreter = new ScriptInterpreter(exprParser, _loggerFactory.CreateLogger<ScriptInterpreter>());
            _triggerRunner = new TriggerRunner(scriptInterpreter, _resources, _loggerFactory.CreateLogger<TriggerRunner>());
            _triggerRunner.ScriptDebug = _config.ScriptDebug;
            _triggerDispatcher.ScriptDebug = _config.ScriptDebug;
            _triggerDispatcher.DebugLog = msg => _log?.LogDebug("{Msg}", msg);
            CharDefHelper.AfterApplyDefName = ch =>
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.Create,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch });
            _systemHooks = new ScriptSystemHooks(_triggerRunner);
            RegisterDbProviders();
            _scriptDb = new ScriptDbAdapter(_loggerFactory.CreateLogger<ScriptDbAdapter>());
            _scriptLdb = new ScriptDbAdapter(_loggerFactory.CreateLogger<ScriptDbAdapter>());
            InitDbConnections(_config, _scriptDb);
            if (_config.HasFileCommands)
            {
                string fileBasePath = Path.Combine(Path.GetDirectoryName(_config.ScpFilesDir) ?? ".", "files");
                _scriptFile = new ScriptFileHandle(fileBasePath);
                _log.LogInformation("Script FILE commands enabled, base path: {Path}", fileBasePath);
            }
            scriptInterpreter.CallFunction = (name, target, source, args) =>
                _triggerRunner.TryRunFunction(name, target, source, args, out var callResult)
                    ? callResult
                    : TriggerResult.Default;
            scriptInterpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
                _triggerRunner.TryRunFunction(name, target, source, args, scope, out var callResult)
                    ? callResult
                    : TriggerResult.Default;
            scriptInterpreter.ResolveFunctionExpression = (name, argString, target, source, args) =>
                _triggerRunner.TryEvaluateFunction(name, argString, target, source, args, out var value)
                    ? value
                    : null;
            scriptInterpreter.ResolveFunctionExpressionWithScope = (name, argString, target, source, args, scope) =>
                _triggerRunner.TryEvaluateFunction(name, argString, target, source, args, scope, out var value)
                    ? value
                    : null;
            scriptInterpreter.ServerPropertyResolver = ResolveServerProperty;
            _triggerDispatcher.Runner = _triggerRunner;

            // Wire @SkillGain trigger + blue system message (Source-X parity)
            SkillEngine.OnSkillGain = (ch, skill, newVal) =>
            {
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillGain,
                    new TriggerArgs { CharSrc = ch, N1 = (int)skill, N2 = newVal });

                if (ch.IsPlayer && _clientsByCharUid.TryGetValue(ch.Uid, out var gc))
                {
                    var def = SphereNet.Game.Definitions.DefinitionLoader.GetSkillDef((int)skill);
                    string skillName = !string.IsNullOrEmpty(def?.Name) ? def.Name : skill.ToString();
                    string valStr = $"{newVal / 10}.{newVal % 10}";
                    gc.SysMessage($"Your skill in {skillName} has increased to {valStr}.", 0x0480);
                    gc.SendSkillList();
                }
            };
            // Wire stat gain message (Source-X: "You feel stronger/more agile/smarter")
            SkillEngine.OnStatGain = (ch, statIdx, newVal) =>
            {
                if (ch.IsPlayer && _clientsByCharUid.TryGetValue(ch.Uid, out var gc))
                {
                    string msg = statIdx switch
                    {
                        0 => "You feel stronger.",
                        1 => "You feel more agile.",
                        2 => "You feel smarter.",
                        _ => ""
                    };
                    if (msg.Length > 0)
                        gc.SysMessage(msg, 0x0480);
                    gc.SendCharacterStatus(ch);
                }
            };
            // Wire scripted (custom) skill trigger chain
            SkillHandlers.OnScriptedSkillUse = (ch, skill) =>
            {
                var args = new TriggerArgs { CharSrc = ch, N1 = (int)skill };

                // @SkillSelect — return 1 cancels
                var selResult = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillSelect, args);
                if (selResult == TriggerResult.True)
                    return false;

                // @SkillStart — scripts can set ACTDIFF via tags
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillStart, args);

                // Difficulty from ACTDIFF property (scripts set via ACTDIFF=, not TAG)
                int difficulty = ch.ActDiff != 0 ? ch.ActDiff : 50;

                bool success = SkillEngine.CheckSuccess(ch, skill, difficulty);

                if (success)
                {
                    _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillSuccess,
                        new TriggerArgs { CharSrc = ch, N1 = (int)skill });
                }
                else
                {
                    _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillFail,
                        new TriggerArgs { CharSrc = ch, N1 = (int)skill });
                }

                // Gain experience (fires @SkillGain via existing callback)
                SkillEngine.GainExperience(ch, skill, success ? difficulty : -difficulty);

                return success;
            };
            SkillHandlers.OnCraftSkillUsed = (ch, skill) =>
            {
                // Find the GameClient for this character and open crafting gump
                foreach (var client in _clients.Values)
                {
                    if (client.Character == ch)
                    {
                        client.OpenCraftingGump(skill);
                        break;
                    }
                }
            };
            _terrain = new TerrainEngine(_mapData);
            _movement = new MovementEngine(_world, _triggerDispatcher);
            _movement.SpellEngine = _spellEngine;
            _movement.OnSysMessage = (mover, text) =>
            {
                // Source-X CClient::SysMessage routes region-enter/PvP/guard text
                // only to the moving player's own client.
                if (TryGetClientFor(mover, out var c))
                    c.SysMessage(text);
            };

            _movement.CanEnterHouse = (mover, dest) =>
            {
                if (mover.PrivLevel >= PrivLevel.GM) return true;
                var house = _housingEngine?.FindHouseAt(dest);
                if (house == null) return true;
                return house.CanAccess(mover.Uid);
            };

            _movement.OnTeleport = (mover, dest, oldMap) =>
            {
                if (TryGetClientFor(mover, out var c))
                {
                    if (oldMap != mover.MapIndex)
                        c.HandleMapChanged();
                    else
                        c.Resync();
                    var snd = new PacketSound(0x01FE, mover.X, mover.Y, mover.Z);
                    BroadcastNearby(mover.Position, 18, snd, 0);
                }
            };

            // Source-X CCharBase::Region_Notify centralised at world level so
            // walk, .go teleport, recall and gate all hit one notify path.
            _world.OnRegionChanged = (mover, oldRegion, newRegion) =>
            {
                _log.LogDebug(
                    "[REGION_CHANGE] {Name} {Old} -> {New} player={IsPlayer}",
                    mover.Name,
                    oldRegion?.Name ?? "<null>",
                    newRegion?.Name ?? "<null>",
                    mover.IsPlayer);

                if (!mover.IsPlayer || newRegion == null) return;
                if (!TryGetClientFor(mover, out var gc)) { _log.LogDebug("[REGION_CHANGE] no GameClient for {Name}", mover.Name); return; }

                gc.SysMessage(SphereNet.Game.Messages.ServerMessages.GetFormatted(
                    SphereNet.Game.Messages.Msg.MsgRegionEnter, newRegion.Name));
                if (newRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded))
                {
                    // Source-X CChar::Region_Notify: SysMessagef(DEFMSG_REGION_GUARDS_1,
                    // <GUARDOWNER tag value>). When the AREADEF omits TAG.GUARDOWNER
                    // it falls back to DEFMSG_REGION_GUARD_ART ("the") so the literal
                    // "%s" never reaches the player.
                    string guardOwner;
                    if (!newRegion.TryGetTag("GUARDOWNER", out var owner) || string.IsNullOrEmpty(owner))
                        guardOwner = SphereNet.Game.Messages.ServerMessages.Get(
                            SphereNet.Game.Messages.Msg.MsgRegionGuardArt);
                    else
                        guardOwner = owner!;
                    gc.SysMessage(SphereNet.Game.Messages.ServerMessages.GetFormatted(
                        SphereNet.Game.Messages.Msg.MsgRegionGuards1, guardOwner));
                }
                if (newRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.NoPvP))
                    gc.SysMessage(SphereNet.Game.Messages.ServerMessages.Get(
                        SphereNet.Game.Messages.Msg.MsgRegionPvpsafe));

                // Source-X also fires DEFMSG_REGION_GUARDS_2 when leaving a guarded
                // zone for an unguarded one — keeps the "you have left the
                // protection of the city guards" callout symmetric.
                if (oldRegion != null &&
                    oldRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded) &&
                    !newRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded))
                {
                    string guardOwner;
                    if (!oldRegion.TryGetTag("GUARDOWNER", out var owner) || string.IsNullOrEmpty(owner))
                        guardOwner = SphereNet.Game.Messages.ServerMessages.Get(
                            SphereNet.Game.Messages.Msg.MsgRegionGuardArt);
                    else
                        guardOwner = owner!;
                    gc.SysMessage(SphereNet.Game.Messages.ServerMessages.GetFormatted(
                        SphereNet.Game.Messages.Msg.MsgRegionGuards2, guardOwner));
                }
            };

            // Source-X parity: spawn-driven NPCs (CItemSpawn) need the same
            // trigger sequence the manual ".add" pipeline runs. Without this
            // hook a vendor that comes from a SPAWN never fires @NPCRestock,
            // so its stock list stays empty and "buy" responds with "no
            // goods". Mirrors the GameClient.CreateNpcFromDef ordering.
            _world.OnNpcSpawned = npc =>
            {
                try
                {
                    _triggerDispatcher?.FireCharTrigger(
                        npc, SphereNet.Core.Enums.CharTrigger.Create,
                        new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });

                    if (npc.NpcBrain == SphereNet.Core.Enums.NpcBrainType.None)
                        npc.NpcBrain = SphereNet.Core.Enums.NpcBrainType.Animal;

                    if (npc.NpcBrain == SphereNet.Core.Enums.NpcBrainType.Vendor)
                    {
                        _triggerDispatcher?.FireCharTrigger(
                            npc, SphereNet.Core.Enums.CharTrigger.NPCRestock,
                            new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
                    }

                    _triggerDispatcher?.FireCharTrigger(
                        npc, SphereNet.Core.Enums.CharTrigger.CreateLoot,
                        new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });

                    var spawnCharDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
                    if (spawnCharDef != null)
                    {
                        foreach (int spellId in spawnCharDef.NpcSpells)
                        {
                            if (Enum.IsDefined(typeof(SpellType), spellId))
                                npc.NpcSpellAdd((SpellType)spellId);
                        }
                    }

                    npc.Hits = npc.MaxHits;
                    npc.Stam = npc.MaxStam;
                    npc.Mana = npc.MaxMana;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[spawn_hook] failed for {Name}", npc.Name);
                }
            };

            // Wire spawn trigger dispatch (@PreSpawn, @Spawn, @AddObj, @DelObj)
            SphereNet.Game.Components.SpawnComponent.OnSpawnTrigger = (item, trigger, args) =>
            {
                if (_triggerDispatcher == null) return TriggerResult.Default;
                var targs = new SphereNet.Game.Scripting.TriggerArgs();
                if (args.SpawnedChar != null)
                {
                    targs.O1 = args.SpawnedChar;
                    targs.CharSrc = args.SpawnedChar;
                }
                targs.N1 = args.SpawnDefIndex;
                return _triggerDispatcher.FireItemTrigger(item, trigger, targs);
            };

            _partyManager = new PartyManager();
            _guildManager = new GuildManager();
            _guildManager.DeserializeFromWorld(_world);
            if (_guildManager.GuildCount > 0)
                _log.LogInformation("Restored {Count} guilds from world save", _guildManager.GuildCount);
            _speech = new SpeechEngine(_world);
            _speech.PartyManager = _partyManager;
            _speech.GuildManager = _guildManager;
            _speech.OnNpcHear += OnNpcHearSpeech;
            _speech.OnPlayerSpeech += OnPlayerSpeech;
            _commands = new CommandHandler();
            _commands.TriggerDispatcher = _triggerDispatcher;
            _commands.CommandPrefix = string.IsNullOrEmpty(_config.CommandPrefix) ? '.' : _config.CommandPrefix[0];
            _commands.Resources = _resources;
            _commands.ScriptFallbackExecutor = (gm, commandLine) =>
            {
                int spaceIdx = commandLine.IndexOf(' ');
                string verb = (spaceIdx > 0 ? commandLine[..spaceIdx] : commandLine).Trim();
                if (string.IsNullOrEmpty(verb))
                    return false;
                string args = spaceIdx > 0 ? commandLine[(spaceIdx + 1)..].Trim() : "";

                // Run script function in the active client context when possible,
                // so client-bound verbs (DIALOG, TARGET*, SERV.ALLCLIENTS, etc.)
                // and compatibility vars (GETREFTYPE/ISDIALOGOPEN) resolve correctly.
                TryGetClientFor(gm, out var scriptConsole);

                if (exprParser.DebugUnresolved)
                {
                    _log.LogDebug("[script_call] verb={Verb} char=0x{Char:X8} sourceConsole={HasConsole} args='{Args}'",
                        verb, gm.Uid.Value, scriptConsole != null, args);
                }

                var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(gm, 0, 0, args)
                {
                    Object1 = gm,
                    Object2 = gm
                };

                if (!_triggerRunner.TryRunFunction(verb, gm, scriptConsole, trigArgs, out var result))
                {
                    if (exprParser.DebugUnresolved)
                        _log.LogDebug("[script_call] verb={Verb} not found", verb);
                    return false;
                }

                if (exprParser.DebugUnresolved)
                    _log.LogDebug("[script_call] verb={Verb} result={Result}", verb, result);

                // Sphere parity:
                // Script verbs are typically written as [FUNCTION ADMIN], [FUNCTION SHOW], etc.,
                // and many do not use explicit RETURN 1. If the function exists and ran, treat
                // it as handled. Built-ins are still preserved because SpeechEngine invokes
                // script fallback first, then built-ins only when script function is missing.
                if (verb.StartsWith("f_", StringComparison.OrdinalIgnoreCase) ||
                    !verb.Contains('_'))
                    return true;

                return result == TriggerResult.True;
            };
            _commands.RegisterDefaults(_world);
            int scriptCmdCount = _commands.LoadScriptCommandPrivileges(_resources);
            _log.LogInformation("Loaded {Count} script command privilege entries from [PLEVEL] sections.", scriptCmdCount);
            _commands.OnResyncCommand += PerformScriptResync;
            _commands.OnSysMessage += (ch, msg) =>
            {
                // Also log so diagnostic command output (e.g. .statics) is visible
                // in the log file without decoding the 0xAE unicode speech packet.
                _log.LogDebug("[sysmsg → {Name}] {Message}", ch.GetName(), msg);
                if (TryGetClientFor(ch, out var c))
                    c.SysMessage(msg);
            };
            _commands.OnCharVisualUpdate += (ch) =>
            {
                if (TryGetClientFor(ch, out var c))
                    c.SendSelfRedraw();
            };
            _commands.OnScriptParityWarning += (ch, verb, reason) =>
            {
                _log.LogWarning("Script parity warning: char=0x{Char:X8} cmd={Cmd} reason={Reason}",
                    ch.Uid.Value, verb, reason);
            };
            _commands.OnCharacterResyncRequested += target =>
            {
                if (TryGetClientFor(target, out var c))
                    c.Resync();
            };
            _commands.OnCharacterMapChanged += target =>
            {
                if (TryGetClientFor(target, out var c))
                    c.HandleMapChanged();
            };
            _commands.OnCharacterSelfRedraw += target =>
            {
                if (TryGetClientFor(target, out var c))
                    c.SendSelfRedraw();
            };
            _commands.OnTeleportTargetRequested += gm =>
            {
                if (TryGetClientFor(gm, out var c))
                    c.BeginTeleportTarget();
            };
            _commands.OnAddTargetRequested += (gm, addToken) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginAddTarget(addToken);
                        break;
                    }
                }
            };
            _commands.OnShowDialogRequested += (gm, title, lines) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.ShowTextDialog(title, lines);
                        break;
                    }
                }
            };
            _commands.OnShowTargetRequested += (gm, args) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginShowTarget(args);
                        break;
                    }
                }
            };
            _commands.OnEditTargetRequested += (gm, args) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginEditTarget(args);
                        break;
                    }
                }
            };
            _commands.OnEditRequested += (gm, uid, page) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.ShowInspectDialog(uid, page);
                        break;
                    }
                }
            };
            // Source-X parity (CClient.cpp:921) — generic X-prefix verb fallback.
            _commands.OnAddVerbTargetRequested += (gm, verb, verbArgs) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginXVerbTarget(verb, verbArgs);
                        break;
                    }
                }
            };
            // Phase C: NUKE / NUKECHAR / NUDGE area-target verbs.
            _commands.OnAreaTargetRequested += (gm, verb, range) =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginAreaTarget(verb, range); break; }
            };
            _commands.OnSummonToTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginSummonToTarget(); break; }
            };
            _commands.OnControlTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginControlTarget(); break; }
            };
            _commands.OnDupeTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginDupeTarget(); break; }
            };
            _commands.OnHealTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginHealTarget(); break; }
            };
            _commands.OnBankTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginBankTarget(); break; }
            };
            _commands.OnBankSelfRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.OpenBankBox(); break; }
            };
            _commands.OnUnmountRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.UnmountSelf(); break; }
            };
            _commands.OnAnimRequested += (gm, animId) =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.PlayOwnAnimation(animId); break; }
            };
            _commands.OnMountTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginMountTarget(); break; }
            };
            _commands.OnOpenPaperdollRequested += (gm, target) =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.SendPaperdoll(target); break; }
            };
            _commands.OnShowSkillsRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.SendSkillList(); break; }
            };
            _commands.OnPageReceived += (player, message) =>
            {
                var pageMsg = $"[PAGE from {player.GetName()}] {message}";
                _log.LogInformation("{PageMessage}", pageMsg);
                var staffNotified = false;
                foreach (var c in _clients.Values)
                {
                    if (c.Character != null && c.Character != player &&
                        c.Character.PrivLevel >= PrivLevel.Counsel)
                    {
                        c.SysMessage(pageMsg);
                        staffNotified = true;
                    }
                }
                if (!staffNotified)
                {
                    foreach (var c in _clients.Values)
                    {
                        if (c.Character == player)
                        {
                            c.SysMessage("No staff members are currently online. Your page has been logged.");
                            break;
                        }
                    }
                }
            };
            _commands.OnSummonCageTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginSummonCageTarget(); break; }
            };
            _commands.OnInspectRequested += (gm, uid) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        var obj = _world.FindObject(new Serial(uid));
                        if (obj != null)
                            c.OpenInspectPropDialog(obj, 0);
                        break;
                    }
                }
            };
            _commands.OnInspectTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginInspectTarget();
                        break;
                    }
                }
            };
            _commands.OnRemoveTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginRemoveTarget();
                        break;
                    }
                }
            };
            _commands.OnKillRequested += (gm, targetUid) =>
            {
                if (!targetUid.HasValue || targetUid.Value.Value == 0)
                {
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient))
                    {
                        gmClient.SysMessage("Select target to kill.");
                        gmClient.BeginKillTarget();
                    }
                    return;
                }

                var victim = _world.FindChar(targetUid.Value);

                if (victim == null)
                {
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient))
                        gmClient.SysMessage("Kill: target not found.");
                    return;
                }

                if (victim.IsDead || victim.IsDeleted)
                {
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient2))
                        gmClient2.SysMessage($"'{victim.Name}' is already dead.");
                    return;
                }

                BroadcastLightningStrike(victim);
                _deathEngine.ProcessDeath(victim, gm);
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient3))
                    gmClient3.SysMessage($"Killed '{victim.Name}'.");
            };
            _commands.OnResurrectRequested += (gm, targetUid) =>
            {
                // No UID  → resurrect self.
                // With UID → resurrect that character (GM-only command, so we
                // don't gate on PrivLevel here; SpeechEngine.Register already
                // restricts the verb).
                var victim = !targetUid.HasValue || targetUid.Value.Value == 0
                    ? gm
                    : _world.FindChar(targetUid.Value);
                if (victim == null)
                {
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient))
                        gmClient.SysMessage("Resurrect: target not found.");
                    return;
                }

                if (!_clientsByCharUid.TryGetValue(victim.Uid, out var victimClient))
                {
                    // Offline / NPC — no client-side ghost transition needed,
                    // just clear the dead state on the character object so the
                    // next login or AI tick sees them alive.
                    if (victim.IsDead)
                        victim.Resurrect();
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient2))
                        gmClient2.SysMessage($"Resurrected '{victim.Name}'.");
                    return;
                }

                if (!victim.IsDead)
                {
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient3))
                        gmClient3.SysMessage($"'{victim.Name}' is not dead.");
                    return;
                }

                victimClient.OnResurrect();
            };
            _commands.OnResurrectTargetRequested += gm =>
            {
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient))
                    gmClient.BeginResurrectTarget();
            };
            _commands.OnCastRequested += (ch, spellId) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == ch)
                    {
                        c.HandleCastSpell((SpellType)spellId, 0);
                        break;
                    }
                }
            };
            _commands.OnScriptDialogRequested += (ch, dialogName, page) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == ch)
                    {
                        if (c.TryShowScriptDialog(dialogName, page))
                            break;

                        // Collect a few close-match suggestions so the user
                        // can tell if it's a typo ("d_moongate" vs
                        // "d_moongates") or a truly missing dialog.
                        var suggestions = CollectDialogSuggestions(dialogName, maxCount: 5);
                        string hint = suggestions.Count == 0
                            ? ""
                            : "  Similar: " + string.Join(", ", suggestions);
                        c.SysMessage($"Dialog '{dialogName}' not found.{hint}");
                        break;
                    }
                }
            };
            _spellRegistry = new SpellRegistry();
            _spellEngine = new SpellEngine(_world, _spellRegistry);
            _spellEngine.OnPlaySound = (pos, soundId) =>
            {
                var pkt = new PacketSound(soundId, pos.X, pos.Y, pos.Z);
                BroadcastNearby(pos, 18, pkt, 0);
            };
            _spellEngine.OnItemRemoved = item =>
            {
                if (item == null || item.IsDeleted) return;
                BroadcastNearby(item.Position, 18, new PacketDeleteObject(item.Uid.Value), 0);
                _world.DeleteObject(item);
                item.Delete();
            };
            _spellEngine.OnSpellInterrupt = (caster, _) =>
            {
                _triggerDispatcher?.FireCharTrigger(caster, CharTrigger.SpellInterrupt,
                    new TriggerArgs { CharSrc = caster });
            };
            _spellEngine.OnPersonalLightChanged = target =>
            {
                if (TryGetClientFor(target, out var c))
                    c.SendPersonalLight();
            };
            _spellEngine.OnSysMessage = (recipient, text) =>
            {
                // Route Recall/Mark/Gate/Poison spell messages to the recipient's
                // own client, mirroring Source-X CClientMsg::SysMessage semantics.
                if (TryGetClientFor(recipient, out var c))
                    c.SysMessage(text);
            };
            _spellEngine.OnCasterFacingChanged = caster =>
            {
                // Source-X UpdateMove(GetTopPoint()) — broadcast new facing only.
                // Reuse the lightweight 0x77 MobileMoving so nearby clients
                // re-render the mobile in its new direction without a full
                // 0x78 char info refresh.
                byte dirByte = (byte)((byte)caster.Direction & 0x07);
                byte flags = 0;
                if (caster.IsInWarMode) flags |= 0x40;
                if (caster.IsDead) flags |= 0x02;
                byte noto = caster.IsPlayer ? (byte)1 : (byte)3;
                var pkt = new PacketMobileMoving(
                    caster.Uid.Value, caster.BodyId,
                    caster.X, caster.Y, caster.Z, dirByte,
                    caster.Hue, flags, noto);
                BroadcastNearby(caster.Position, 18, pkt, 0);
            };
            _spellEngine.OnSpellWords = (caster, words) =>
            {
                var pkt = new PacketSpeechUnicodeOut(
                    caster.Uid.Value,
                    caster.BodyId,
                    0x00,
                    caster.SpeechColor != 0 ? caster.SpeechColor : (ushort)0x03B2,
                    3,
                    "TRK",
                    caster.Name ?? "",
                    words);
                BroadcastNearby(caster.Position, 18, pkt, 0);
            };
            _spellEngine.OnCastAnimation = (caster, animId) =>
            {
                ushort anim = caster.IsMounted ? MapAnimToMounted(animId) : animId;
                var animPkt = new PacketAnimation(caster.Uid.Value, anim);
                BroadcastNearby(caster.Position, 18, animPkt, 0);
            };
            _spellEngine.OnSpellTeleport = (caster, dest, oldMap) =>
            {
                if (TryGetClientFor(caster, out var c))
                {
                    if (oldMap != caster.MapIndex)
                        c.HandleMapChanged();
                    else
                        c.Resync();
                    var snd = new PacketSound(0x01FE, caster.X, caster.Y, caster.Z);
                    BroadcastNearby(caster.Position, 18, snd, 0);
                }
            };
            _spellEngine.OnTargetKilled = (victim, killer) =>
            {
                var effectiveKiller = killer != null ? ResolveEffectiveOffender(killer) : null;

                if (effectiveKiller != null)
                    _triggerDispatcher?.FireCharTrigger(effectiveKiller, CharTrigger.Kill,
                        new TriggerArgs { CharSrc = effectiveKiller, O1 = victim });
                _triggerDispatcher?.FireCharTrigger(victim, CharTrigger.Death,
                    new TriggerArgs { CharSrc = effectiveKiller });

                var victimPos = victim.Position;
                byte victimDir = (byte)((byte)victim.Direction & 0x07);
                var corpse = _deathEngine.ProcessDeath(victim, effectiveKiller);
                if (effectiveKiller != null)
                    effectiveKiller.FightTarget = Serial.Invalid;

                if (corpse != null)
                {
                    if (victim.IsPlayer)
                    {
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue, victimDir);
                        BroadcastNearby(victimPos, 18, corpsePacket, 0);

                        foreach (var corpseItem in corpse.Contents)
                        {
                            var containerItem = new PacketContainerItem(
                                corpseItem.Uid.Value, corpseItem.DispIdFull, 0,
                                corpseItem.Amount, corpseItem.X, corpseItem.Y,
                                corpse.Uid.Value, corpseItem.Hue, useGridIndex: true);
                            BroadcastNearby(victimPos, 18, containerItem, 0);
                        }

                        var corpseEquipEntries = new List<(byte Layer, uint ItemSerial)>();
                        var usedLayers = new HashSet<byte>();
                        foreach (var item in corpse.Contents)
                        {
                            byte layer = (byte)item.EquipLayer;
                            if (layer == (byte)Layer.None || layer == (byte)Layer.Face || layer == (byte)Layer.Pack)
                                continue;
                            if (!usedLayers.Add(layer))
                                continue;
                            corpseEquipEntries.Add((layer, item.Uid.Value));
                        }

                        var corpseEquip = new PacketCorpseEquipment(corpse.Uid.Value, corpseEquipEntries);
                        BroadcastNearby(victimPos, 18, corpseEquip, 0);
                    }
                    else
                    {
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue, victimDir);
                        BroadcastNearby(victimPos, 18, corpsePacket, 0);

                        var dirToKiller = effectiveKiller != null
                            ? victim.Position.GetDirectionTo(effectiveKiller.Position)
                            : victim.Direction;
                        uint npcFallDir = (uint)dirToKiller <= 3 ? 1u : 0u;
                        var deathAnim = new PacketDeathAnimation(victim.Uid.Value, corpse.Uid.Value, npcFallDir);
                        BroadcastNearby(victimPos, 18, deathAnim, 0);

                        var removeMobile = new PacketDeleteObject(victim.Uid.Value);
                        BroadcastNearby(victimPos, 18, removeMobile, 0);
                    }
                }

                foreach (var c in _clients.Values)
                {
                    if (c.Character == victim)
                    {
                        c.OnCharacterDeath();
                        break;
                    }
                }
            };
            Character.OnLifecycleKill = (victim, killer) =>
            {
                if (victim.IsDead) return;

                var effectiveKiller = killer != null ? ResolveEffectiveOffender(killer) : null;

                if (effectiveKiller != null)
                    _triggerDispatcher?.FireCharTrigger(effectiveKiller, CharTrigger.Kill,
                        new TriggerArgs { CharSrc = effectiveKiller, O1 = victim });
                _triggerDispatcher?.FireCharTrigger(victim, CharTrigger.Death,
                    new TriggerArgs { CharSrc = effectiveKiller });

                var victimPos = victim.Position;
                byte victimDir = (byte)((byte)victim.Direction & 0x07);
                var corpse = _deathEngine.ProcessDeath(victim, effectiveKiller);
                if (effectiveKiller != null)
                    effectiveKiller.FightTarget = Serial.Invalid;

                if (corpse != null)
                {
                    if (victim.IsPlayer)
                    {
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue, victimDir);
                        BroadcastNearby(victimPos, 18, corpsePacket, 0);

                        foreach (var corpseItem in corpse.Contents)
                        {
                            var containerItem = new PacketContainerItem(
                                corpseItem.Uid.Value, corpseItem.DispIdFull, 0,
                                corpseItem.Amount, corpseItem.X, corpseItem.Y,
                                corpse.Uid.Value, corpseItem.Hue, useGridIndex: true);
                            BroadcastNearby(victimPos, 18, containerItem, 0);
                        }

                        var corpseEquipEntries = new List<(byte Layer, uint ItemSerial)>();
                        var usedLayers = new HashSet<byte>();
                        foreach (var item in corpse.Contents)
                        {
                            byte layer = (byte)item.EquipLayer;
                            if (layer == (byte)Layer.None || layer == (byte)Layer.Face || layer == (byte)Layer.Pack)
                                continue;
                            if (!usedLayers.Add(layer))
                                continue;
                            corpseEquipEntries.Add((layer, item.Uid.Value));
                        }

                        var corpseEquip = new PacketCorpseEquipment(corpse.Uid.Value, corpseEquipEntries);
                        BroadcastNearby(victimPos, 18, corpseEquip, 0);
                    }
                    else
                    {
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue, victimDir);
                        BroadcastNearby(victimPos, 18, corpsePacket, 0);

                        var dirToKiller = effectiveKiller != null
                            ? victim.Position.GetDirectionTo(effectiveKiller.Position)
                            : victim.Direction;
                        uint npcFallDir = (uint)dirToKiller <= 3 ? 1u : 0u;
                        var deathAnim = new PacketDeathAnimation(victim.Uid.Value, corpse.Uid.Value, npcFallDir);
                        BroadcastNearby(victimPos, 18, deathAnim, 0);

                        var removeMobile = new PacketDeleteObject(victim.Uid.Value);
                        BroadcastNearby(victimPos, 18, removeMobile, 0);
                    }
                }

                foreach (var c in _clients.Values)
                {
                    if (c.Character == victim)
                    {
                        c.OnCharacterDeath();
                        break;
                    }
                }
            };
            Character.OnLifecycleResurrect = victim =>
            {
                if (!victim.IsDead) return;
                if (_clientsByCharUid.TryGetValue(victim.Uid, out var victimClient))
                    victimClient.OnResurrect();
                else
                    victim.Resurrect();
            };
            Character.OnJailReleaseRequested = inmate =>
            {
                inmate.ClearStatFlag(StatFlag.Freeze);
                inmate.RemoveTag("JAIL_RELEASE");
                var spawnPos = new Point3D(1495, 1629, 10, 0);
                _world.MoveCharacter(inmate, spawnPos);
                if (_clientsByCharUid.TryGetValue(inmate.Uid, out var inmateClient))
                    inmateClient.Resync();
            };
            Character.OnHungerDecay = ch =>
            {
                _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.Hunger,
                    new TriggerArgs { CharSrc = ch });
            };
            Character.OnCriminalCheck = ch =>
            {
                bool cancelled = _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.Criminal,
                    new TriggerArgs { CharSrc = ch }) == TriggerResult.True;
                if (!cancelled && _world != null && _triggerDispatcher != null)
                {
                    // Nearby NPCs/guards witness the crime (@SeeCrime, <src> = criminal).
                    foreach (var witness in _world.GetCharsInRange(ch.Position, 12))
                    {
                        if (witness == ch || witness.IsPlayer || witness.IsDead) continue;
                        _triggerDispatcher.FireCharTrigger(witness, CharTrigger.SeeCrime,
                            new TriggerArgs { CharSrc = ch });
                    }
                }
                return cancelled;
            };
            GameRegion.ClientCountProvider = regionObj =>
            {
                int count = 0;
                foreach (var c in _clients.Values)
                {
                    if (c.Character == null || !c.IsPlaying) continue;
                    switch (regionObj)
                    {
                        case GameRegion reg when reg.Contains(c.Character.Position):
                        case Room room when room.Contains(c.Character.Position):
                            count++;
                            break;
                    }
                }
                return count;
            };
            _deathEngine = new DeathEngine(_world);
            // NOTE: DeathEngine.OnDeath fires from inside ProcessDeath, after the
            // corpse object is created but BEFORE the corpse spawn packets are
            // broadcast. We deliberately do NOT call GameClient.OnCharacterDeath
            // here, because:
            //   * Source-X order is: corpse appears → 0xAF death anim → ghost
            //     transition. Calling OnCharacterDeath inside this callback
            //     flips the player to a ghost body BEFORE the killer's client
            //     has even received the corpse packet, so observers briefly
            //     see "ghost without a corpse on the floor".
            //   * Both code paths that trigger player death (NpcAI.OnNpcKill
            //     in Program.cs and Player-vs-Player in GameClient.TrySwingAt)
            //     already invoke c.OnCharacterDeath() AFTER they finish sending
            //     corpse + 0xAF packets. Doing it here would call it twice.
            // This hook is kept for non-visual side effects (logging, party
            // bookkeeping, etc.) — currently nothing else needs it.
            _deathEngine.LootingIsACrime = _config.LootingIsACrime;
            _deathEngine.CorpseDecayNPC = _config.CorpseNpcDecay * 60;
            _deathEngine.CorpseDecayPlayer = _config.CorpsePlayerDecay * 60;
            _deathEngine.PartyManager = _partyManager;
            _deathEngine.TriggerDispatcher = _triggerDispatcher;
            _tradeManager = new TradeManager();
            _npcAI = new NpcAI(_world, _config);
            _npcTimerWheel = new SphereNet.Game.Scheduling.TimerWheel(Environment.TickCount64);
            _recordingEngine = new SphereNet.Game.Recording.RecordingEngine(
                Path.Combine(ResolvePath(basePath, _config.WorldSaveDir), "recordings"));
            _recordingEngine.SnapshotNearbyCharacters = (center, range) =>
            {
                var result = new List<byte[]>();
                foreach (var ch in _world.GetCharsInRange(center, range))
                {
                    if (ch.IsPlayer && !ch.IsOnline) continue;
                    if (ch.IsInvisible || ch.IsStatFlag(StatFlag.Hidden)) continue;
                    var equip = new List<(uint, ushort, byte, ushort)>();
                    for (int layer = 1; layer <= (int)Layer.Horse; layer++)
                    {
                        var item = ch.GetEquippedItem((Layer)layer);
                        if (item != null)
                            equip.Add((item.Uid.Value, item.DispIdFull, (byte)layer, item.Hue));
                    }
                    byte flags = 0;
                    if (ch.IsInWarMode) flags |= 0x40;
                    if (ch.IsInvisible) flags |= 0x80;
                    var pkt = new PacketDrawObject(
                        ch.Uid.Value, ch.BodyId, ch.X, ch.Y, ch.Z,
                        (byte)ch.Direction, ch.Hue, flags, 0x01,
                        equip.ToArray());
                    result.Add(pkt.Build().Span.ToArray());
                }
                return result;
            };
            if (_config.StateRecordingEnabled)
            {
                try
                {
                    var stateDbPath = Path.Combine(ResolvePath(basePath, _config.WorldSaveDir), "state_recording.db");
                    _stateRecorder = new SphereNet.Server.Recording.StateRecorder(
                        stateDbPath, _loggerFactory.CreateLogger("StateRecorder"),
                        _config.StateRecordPlayersOnly,
                        _config.StateRecordMoveScanMs,
                        _config.StateRecordSnapshotMs);
                    _stateRecorder.Initialize();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "StateRecorder init failed — state recording disabled");
                    _stateRecorder = null;
                }
            }

            if (_config.MacroEnabled)
                _macroEngine = new SphereNet.Server.Macro.MacroEngine(_config.MacroMaxSteps, _config.MacroMaxLoopMinutes);

            _npcAI.OnNpcLookAtChar = (npc, target) =>
                _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCLookAtChar,
                    new TriggerArgs { CharSrc = target, N1 = target.Uid.Value > int.MaxValue ? 0 : (int)target.Uid.Value }) == TriggerResult.True;
            _npcAI.OnNpcActFight = (npc, target) =>
                _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCActFight,
                    new TriggerArgs { CharSrc = target, N1 = target.Uid.Value > int.MaxValue ? 0 : (int)target.Uid.Value }) == TriggerResult.True;
            _npcAI.OnNpcActWander = npc =>
                _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCActWander,
                    new TriggerArgs { CharSrc = npc }) == TriggerResult.True;
            _npcAI.OnNpcActFollow = (npc, target) =>
                _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCActFollow,
                    new TriggerArgs { CharSrc = target, O1 = target }) == TriggerResult.True;
            _npcAI.OnNpcActCast = (npc, target, spell) =>
                _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCActCast,
                    new TriggerArgs { CharSrc = target, O1 = target, N1 = (int)spell }) == TriggerResult.True;
            _npcAI.OnNpcLookAtItem = (npc, item) =>
                _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCLookAtItem,
                    new TriggerArgs { CharSrc = npc, O1 = item, N1 = (int)item.Uid.Value }) == TriggerResult.True;

            _npcAI.OnNpcSay = (npc, text) =>
            {
                NpcSpeak(npc, text);
            };
            _npcAI.OnGuardLightningStrike = target =>
            {
                BroadcastLightningStrike(target);
            };
            _npcAI.OnNpcTeleport = npc =>
            {
                BroadcastCharacterAppear(npc);
            };
            _npcAI.OnNpcAttack = (attacker, target, damage) =>
            {
                byte attackerDir = (byte)((byte)attacker.Direction & 0x07);
                byte attackerFlags = 0;
                if (attacker.IsInWarMode) attackerFlags |= 0x40;
                if (attacker.IsDead) attackerFlags |= 0x02;
                if (attacker.BodyId == 0x0191 || attacker.BodyId == 0x0193) attackerFlags |= 0x02;
                if (attacker.IsStatFlag(StatFlag.Freeze)) attackerFlags |= 0x01;

                ForEachClientInRange(attacker.Position, 18, 0, (observerCh, observerClient) =>
                {
                    byte noto = GameClient.ComputeNotoriety(_world, observerCh, attacker);
                    observerClient.Send(new PacketMobileMoving(
                        attacker.Uid.Value, attacker.BodyId,
                        attacker.X, attacker.Y, attacker.Z, attackerDir,
                        attacker.Hue, attackerFlags, noto));
                });

                ushort swingAnim = GameClient.GetNpcSwingAction(attacker);
                GameClient.BroadcastAnimation(attacker, swingAnim, NewAnimationGesture.Attack, 18,
                    BroadcastNearby, ForEachClientInRange);

                var weapon = attacker.GetEquippedItem(Layer.OneHanded) ?? attacker.GetEquippedItem(Layer.TwoHanded);
                ushort swingSound = GameClient.GetSwingSoundPublic(weapon);
                BroadcastNearby(attacker.Position, 18,
                    new PacketSound(swingSound, attacker.X, attacker.Y, attacker.Z), 0);

                if (damage <= 0)
                {
                    BroadcastNearby(target.Position, 18,
                        new PacketSound(0x0234, target.X, target.Y, target.Z), 0);
                    _triggerDispatcher?.FireCharTrigger(attacker, CharTrigger.HitMiss,
                        new TriggerArgs { CharSrc = attacker, O1 = target });
                    return;
                }

                ushort getHitAction = target.IsMounted
                    ? MapAnimToMounted((ushort)AnimationType.GetHit)
                    : (ushort)AnimationType.GetHit;
                BroadcastNearby(target.Position, 18, new PacketAnimation(target.Uid.Value, getHitAction), 0);

                if (damage > 0)
                {
                    _spellEngine?.TryInterruptFromDamage(target, damage);
                    if (target.HasActiveSkillPending())
                    {
                        int abortedSkill = target.ClearActiveSkillPending();
                        if (abortedSkill >= 0)
                            Character.ActiveSkillAborted?.Invoke(target, abortedSkill);
                    }
                }

                BroadcastNearby(target.Position, 18,
                    new PacketDamage(target.Uid.Value, (ushort)Math.Min(damage, ushort.MaxValue)), 0);
                BroadcastNearby(target.Position, 18,
                    new PacketUpdateHealth(target.Uid.Value, target.MaxHits, target.Hits), 0);

                _triggerDispatcher?.FireCharTrigger(attacker, CharTrigger.Hit,
                    new TriggerArgs { CharSrc = attacker, O1 = target, N1 = damage });
                _triggerDispatcher?.FireCharTrigger(target, CharTrigger.GetHit,
                    new TriggerArgs { CharSrc = attacker, N1 = damage });

                if (weapon != null)
                    _triggerDispatcher?.FireItemTrigger(weapon, ItemTrigger.Hit,
                        new TriggerArgs { CharSrc = attacker, ItemSrc = weapon, O1 = target, N1 = damage });
                var shield = target.GetEquippedItem(Layer.TwoHanded);
                if (shield != null)
                    _triggerDispatcher?.FireItemTrigger(shield, ItemTrigger.GetHit,
                        new TriggerArgs { CharSrc = attacker, ItemSrc = shield, N1 = damage });
            };
            _npcAI.OnNpcKill = (killer, victim) =>
            {
                // Reflect / self-inflicted death (reactive armor etc.): don't
                // attribute the kill to the victim itself.
                var actualKiller = ReferenceEquals(killer, victim) ? null : killer;
                var effectiveKiller = actualKiller != null ? ResolveEffectiveOffender(actualKiller) : null;

                if (effectiveKiller != null)
                    _triggerDispatcher?.FireCharTrigger(effectiveKiller, CharTrigger.Kill,
                        new TriggerArgs { CharSrc = effectiveKiller, O1 = victim });
                _triggerDispatcher?.FireCharTrigger(victim, CharTrigger.Death,
                    new TriggerArgs { CharSrc = effectiveKiller });

                var victimPos = victim.Position;
                byte victimDir = (byte)((byte)victim.Direction & 0x07);
                var corpse = _deathEngine.ProcessDeath(victim, effectiveKiller);
                killer.FightTarget = Serial.Invalid;

                if (corpse != null)
                {
                    uint corpseWireSerial = corpse.Uid.Value;
                    if (corpse.Amount > 1)
                        corpseWireSerial |= 0x80000000u;

                    if (victim.IsPlayer)
                    {
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                            victimDir);
                        BroadcastNearby(victimPos, 18, corpsePacket, 0);

                        // Player corpse: send contents + equip map for paperdoll corpse rendering.
                        foreach (var corpseItem in corpse.Contents)
                        {
                            var containerItem = new PacketContainerItem(
                                corpseItem.Uid.Value,
                                corpseItem.DispIdFull,
                                0,
                                corpseItem.Amount,
                                corpseItem.X,
                                corpseItem.Y,
                                corpse.Uid.Value,
                                corpseItem.Hue,
                                useGridIndex: true);
                            BroadcastNearby(victimPos, 18, containerItem, 0);
                        }

                        var corpseEquipEntries = new List<(byte Layer, uint ItemSerial)>();
                        var usedLayers = new HashSet<byte>();
                        foreach (var item in corpse.Contents)
                        {
                            byte layer = (byte)item.EquipLayer;
                            if (layer == (byte)Layer.None || layer == (byte)Layer.Face || layer == (byte)Layer.Pack)
                                continue;
                            if (!usedLayers.Add(layer))
                                continue;
                            corpseEquipEntries.Add((layer, item.Uid.Value));
                        }

                        var corpseEquip = new PacketCorpseEquipment(corpse.Uid.Value, corpseEquipEntries);
                        BroadcastNearby(victimPos, 18, corpseEquip, 0);

                        // 0xAF is NOT broadcast here — OnCharacterDeath below
                        // runs a per-observer dispatch that sends 0xAF to plain
                        // players and 0x1D + 0x78 ghost mobile to staff. A
                        // blanket BroadcastNearby would hit staff with 0xAF
                        // BEFORE 0x1D+0x78, causing ClassicUO to remap the
                        // serial (0x80000000|serial) so the follow-up 0x1D
                        // becomes a no-op and the alive body lingers under the
                        // remapped key alongside the new ghost mobile.
                        // Mirrors the PvP (TrySwingAt) path which also defers
                        // 0xAF to OnCharacterDeath's per-observer dispatch.
                    }
                    else
                    {
                        // NPC corpse — keep the same packet order the PvP path
                        // uses for non-player victims so the client sees:
                        //   1) corpse world item
                        //   2) death animation
                        //   3) delete dead mobile
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                            victimDir);
                        BroadcastNearby(victimPos, 18, corpsePacket, 0);

                        var dirToKiller = victim.Position.GetDirectionTo(killer.Position);
                        uint npcFallDir = (uint)dirToKiller <= 3 ? 1u : 0u;
                        var deathAnim = new PacketDeathAnimation(victim.Uid.Value, corpse.Uid.Value, npcFallDir);
                        BroadcastNearby(victimPos, 18, deathAnim, 0);

                        var removeMobile = new PacketDeleteObject(victim.Uid.Value);
                        BroadcastNearby(victimPos, 18, removeMobile, 0);
                    }
                }

                foreach (var c in _clients.Values)
                {
                    if (c.Character == victim)
                    {
                        c.OnCharacterDeath();
                        break;
                    }
                }
            };
            _npcAI.OnHealerAction = (healer, target, isResurrect) =>
            {
                ushort healAnim = healer.IsMounted ? MapAnimToMounted(16) : (ushort)16;
                var anim = new PacketAnimation(healer.Uid.Value, healAnim, 4, 1, false, false, 0);
                BroadcastNearby(healer.Position, 18, anim, 0);
                var sound = new PacketSound(isResurrect ? (ushort)0x0214 : (ushort)0x01F2,
                    healer.X, healer.Y, healer.Z);
                BroadcastNearby(healer.Position, 18, sound, 0);

                if (isResurrect && target.IsDead)
                {
                    if (_clientsByCharUid.TryGetValue(target.Uid, out var victimClient))
                        victimClient.OnResurrect();
                    else
                        target.Resurrect();
                }
            };
            _npcAI.OnHealerCure = (healer, target) =>
            {
                ushort healAnim = healer.IsMounted ? MapAnimToMounted(16) : (ushort)16;
                var anim = new PacketAnimation(healer.Uid.Value, healAnim, 4, 1, false, false, 0);
                BroadcastNearby(healer.Position, 18, anim, 0);
                var sound = new PacketSound(0x01E0, healer.X, healer.Y, healer.Z);
                BroadcastNearby(healer.Position, 18, sound, 0);
            };
            _npcAI.OnVendorRestock = vendor =>
            {
                _triggerDispatcher?.FireCharTrigger(vendor, CharTrigger.NPCRestock,
                    new TriggerArgs { CharSrc = vendor });
            };
            _npcAI.OnWitnessCrime = (witness, criminal) =>
            {
                _npcAI.AlertGuardsInRange(witness.Position, criminal);
            };
            _npcAI.OnWakeNpc = WakeNpc;
            _npcAI.OnNpcSound = (npc, type) =>
            {
                var charDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
                if (charDef == null) return;
                ushort soundId = type switch
                {
                    CreatureSoundType.Idle => charDef.SoundIdle,
                    CreatureSoundType.Notice => charDef.SoundNotice,
                    CreatureSoundType.Hit => charDef.SoundHit,
                    CreatureSoundType.GetHit => charDef.SoundGetHit,
                    CreatureSoundType.Die => charDef.SoundDie,
                    _ => 0
                };
                if (soundId == 0) return;
                var snd = new PacketSound(soundId, npc.X, npc.Y, npc.Z);
                BroadcastNearby(npc.Position, 18, snd, 0);
            };
            _npcAI.OnNpcBreath = (npc, target, damage) =>
            {
                // Fire breath effect: moving fireball from NPC to target
                var fx = new PacketEffect(0, npc.Uid.Value, target.Uid.Value, 0x36D4,
                    npc.X, npc.Y, (short)(npc.Z + 10),
                    target.X, target.Y, (short)(target.Z + 10),
                    7, 10, true, true);
                BroadcastNearby(npc.Position, 18, fx, 0);
                var breathSound = new PacketSound(0x0227, npc.X, npc.Y, npc.Z);
                BroadcastNearby(npc.Position, 18, breathSound, 0);

                target.Hits -= (short)Math.Min(damage, target.Hits);
                _spellEngine?.TryInterruptFromDamage(target, damage);
                if (target.HasActiveSkillPending())
                {
                    int abortedSkill = target.ClearActiveSkillPending();
                    if (abortedSkill >= 0)
                        Character.ActiveSkillAborted?.Invoke(target, abortedSkill);
                }
                if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
                {
                    target.FightTarget = npc.Uid;
                    target.NextNpcActionTime = 0;
                    WakeNpc(target);
                }
                var dmgPkt = new PacketDamage(target.Uid.Value, (ushort)Math.Min(damage, ushort.MaxValue));
                BroadcastNearby(target.Position, 18, dmgPkt, 0);
                var healthPkt = new PacketUpdateHealth(target.Uid.Value, target.MaxHits, target.Hits);
                BroadcastNearby(target.Position, 18, healthPkt, 0);

                if (target.Hits <= 0 && !target.IsDead)
                    _npcAI.OnNpcKill?.Invoke(npc, target);
            };
            _npcAI.OnNpcThrow = (npc, target, damage) =>
            {
                ushort throwGfx = 0x0F51;
                if (npc.TryGetTag("THROWOBJ", out string? objStr) && !string.IsNullOrWhiteSpace(objStr))
                {
                    var cleanObj = objStr.Trim();
                    if (cleanObj.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        cleanObj = cleanObj[2..];
                    if (ushort.TryParse(cleanObj, System.Globalization.NumberStyles.HexNumber, null, out ushort gfx) && gfx > 0)
                        throwGfx = gfx;
                }
                var fx = new PacketEffect(0, npc.Uid.Value, target.Uid.Value, throwGfx,
                    npc.X, npc.Y, (short)(npc.Z + 10),
                    target.X, target.Y, (short)(target.Z + 10),
                    10, 5, true, false);
                BroadcastNearby(npc.Position, 18, fx, 0);

                target.Hits -= (short)Math.Min(damage, target.Hits);
                _spellEngine?.TryInterruptFromDamage(target, damage);
                if (target.HasActiveSkillPending())
                {
                    int abortedSkill = target.ClearActiveSkillPending();
                    if (abortedSkill >= 0)
                        Character.ActiveSkillAborted?.Invoke(target, abortedSkill);
                }
                if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
                {
                    target.FightTarget = npc.Uid;
                    target.NextNpcActionTime = 0;
                    WakeNpc(target);
                }
                var dmgPkt = new PacketDamage(target.Uid.Value, (ushort)Math.Min(damage, ushort.MaxValue));
                BroadcastNearby(target.Position, 18, dmgPkt, 0);
                var healthPkt = new PacketUpdateHealth(target.Uid.Value, target.MaxHits, target.Hits);
                BroadcastNearby(target.Position, 18, healthPkt, 0);

                if (target.Hits <= 0 && !target.IsDead)
                    _npcAI.OnNpcKill?.Invoke(npc, target);
            };
            _npcAI.OnNpcCastSpell = (npc, target, spell) =>
            {
                int castMs = _spellEngine.CastStart(npc, spell, target.Uid, target.Position);
                if (castMs > 0)
                    npc.SetCastTimerEnd(Environment.TickCount64 + castMs);
            };
            _npcAI.OnNpcTickSpellCast = npc => _spellEngine.TickCastTimer(npc);
            var gatheringEngine = new GatheringEngine(_world, _triggerDispatcher);
            _skillHandlers = new SkillHandlers(_world, gatheringEngine);
            _craftingEngine = new CraftingEngine(_world);
            // NOTE: LoadRecipesFromDefs is called AFTER defLoader.LoadAll() populates
            // DefinitionLoader.AllItemDefs — see post-definition-load block below.
            _weatherEngine = new WeatherEngine(_world);
            _weatherEngine.Configure(
                _config.SeasonMode,
                (SeasonType)Math.Clamp(_config.SeasonDefault, (byte)SeasonType.Spring, (byte)SeasonType.Desolation),
                checked(_config.SeasonChangeIntervalMinutes * 60 * 1000));
            _weatherEngine.OnWeatherChanged = (regionName, type, intensity, temp) =>
            {
                var pkt = new PacketWeather((byte)type, intensity, temp);
                foreach (var c in _clients.Values)
                {
                    if (!c.IsPlaying || c.Character == null) continue;
                    var r = _world.FindRegion(c.Character.Position);
                    if (r != null && r.Name == regionName)
                        c.Send(pkt);
                }
            };
            VendorEngine.World = _world;
            _world.ObjectCreated += OnWorldObjectCreated;
            _world.ObjectDeleting += OnWorldObjectDeleting;
            _world.CharacterMoved += OnCharacterMoved;
            _world.CharacterPlaced += BroadcastCharacterAppear;
            _accounts.AccountCreated += account => _systemHooks.DispatchAccount("create", account);
            _accounts.AccountLogin += account => _systemHooks.DispatchAccount("login", account);
            _accounts.AccountDeleted += account => _systemHooks.DispatchAccount("delete", account);
            _accounts.AccountPasswordChanged += account => _systemHooks.DispatchAccount("pwchange", account);
            _accounts.AccountBlocked += account => _systemHooks.DispatchAccount("block", account);
            _accounts.AccountUnblocked += account => _systemHooks.DispatchAccount("unblock", account);
            _accounts.AccountsChanged += SaveAccountsToDisk;

            // Wire config values to engines
            SkillEngine.SkillSumMaxOverride = _config.MaxBaseSkill > 0 ? _config.MaxBaseSkill : 7000;
            _world.MapData = _mapData;

            // Wire combat weapon damage lookup from ItemDef definitions
            CombatEngine.WeaponDefLookup = (baseId) =>
            {
                var link = _resources.GetResource(ResType.ItemDef, baseId);
                if (link == null) return null;
                using var sf = link.OpenAtStoredPosition();
                if (sf == null) return null;
                var sections = sf.ReadAllSections();
                int damMin = 0, damMax = 0;
                foreach (var sec in sections)
                {
                    foreach (var key in sec.Keys)
                    {
                        if (key.Key.Equals("DAM", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = key.Arg.Split(',');
                            int.TryParse(parts[0].Trim(), out damMin);
                            if (parts.Length > 1) int.TryParse(parts[1].Trim(), out damMax);
                            else damMax = damMin;
                        }
                    }
                }
                return damMax > 0 ? (damMin, damMax) : null;
            };

            CombatEngine.NpcDamageDefLookup = (defIndex) =>
            {
                var charDef = DefinitionLoader.GetCharDef(defIndex);
                if (charDef == null || (charDef.AttackMin == 0 && charDef.AttackMax == 0))
                    return null;
                return (charDef.AttackMin, Math.Max(charDef.AttackMin, charDef.AttackMax));
            };

            // Item durability from config
            CombatEngine.DurabilityEnabled = _config.ItemDurabilityEnabled;
            CombatEngine.DurabilityLossChance = _config.ItemDurabilityLossChance;
            CombatEngine.DurabilityLossMin = _config.ItemDurabilityLossMin;
            CombatEngine.DurabilityLossMax = _config.ItemDurabilityLossMax;
            CombatEngine.BreakOnZeroHits = _config.ItemBreakOnZeroHits;
            CombatEngine.DefaultHits = _config.ItemDefaultHits;

            CombatEngine.OnItemDamaged = (item, loss) =>
            {
                if (_triggerDispatcher == null) return false;
                var args = new TriggerArgs { ItemSrc = item, N1 = loss };
                var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.Damage, args);
                return result == TriggerResult.True;
            };

            CombatEngine.OnItemBroken = (item) =>
            {
                var owner = item.ContainedIn.IsValid ? _world.FindChar(item.ContainedIn) : null;
                if (owner == null)
                {
                    var equipOwner = _world.GetAllObjects()
                        .OfType<SphereNet.Game.Objects.Characters.Character>()
                        .FirstOrDefault(c => c.GetEquippedItem(item.EquipLayer) == item);
                    owner = equipOwner;
                }

                if (owner != null && _clientsByCharUid.TryGetValue(owner.Uid, out var ownerClient))
                    ownerClient.SysMessage($"Your {item.GetName()} has been destroyed!");

                if (item.EquipLayer != SphereNet.Core.Enums.Layer.None)
                    owner?.Unequip(item.EquipLayer);
                var parent = item.ContainedIn.IsValid ? _world.FindObject(item.ContainedIn) as SphereNet.Game.Objects.Items.Item : null;
                parent?.RemoveItem(item);
                _world.RemoveItem(item);
                item.Delete();
            };

            CombatEngine.OnHitParry = (defender, attacker) =>
            {
                _triggerDispatcher?.FireCharTrigger(defender, CharTrigger.HitParry,
                    new TriggerArgs { CharSrc = attacker, O1 = attacker });
            };

            // Housing — load multi definitions from multi.mul
            var multiRegistry = new SphereNet.Game.Housing.MultiRegistry();
            if (_mapData != null)
            {
                int multiCount = multiRegistry.LoadFromMapData(_mapData);
                _log.LogInformation("Loaded {Count} multi definitions from multi.mul", multiCount);
            }
            _housingEngine = new HousingEngine(_world, multiRegistry)
            {
                MaxHousesPerPlayer = _config.MaxHousesPlayer,
                MaxHousesPerAccount = _config.MaxHousesAccount
            };
            _housingEngine.DeserializeFromWorld();
            if (_housingEngine.HouseCount > 0)
                _log.LogInformation("Restored {Count} houses from world save", _housingEngine.HouseCount);

            // Ships
            _shipEngine = new SphereNet.Game.Ships.ShipEngine(_world, multiRegistry, _mapData);
            _shipEngine.OnTillerSpeak = (ship, text) =>
            {
                // Source-X CItemShip::Speak uses CObjBase::Speak with COLOR_TEXT_DEF.
                // Broadcast as overhead unicode text from the tillerman item so
                // every client near the multi sees the line, mirroring upstream.
                var origin = ship.MultiItem.Position;
                var pkt = new PacketSpeechUnicodeOut(
                    ship.MultiItem.Uid.Value,
                    ship.MultiItem.DispIdFull,
                    0x06, // ASCII speech
                    0x0481, // grey hue
                    3, // small font
                    "ENU",
                    ship.MultiItem.Name ?? "Tillerman",
                    text);
                BroadcastNearby(origin, 18, pkt, 0);
            };
            _shipEngine.OnShipMoved = ship =>
            {
                var mi = ship.MultiItem;
                var pkt = new PacketBoatSmoothMove(
                    mi.Uid.Value,
                    (byte)ship.SpeedMode,
                    (byte)((byte)ship.DirMove & 0x07),
                    (byte)((byte)ship.DirFace & 0x07),
                    mi.X, mi.Y, (ushort)(mi.Z < 0 ? 0 : mi.Z));
                BroadcastNearby(mi.Position, 18, pkt, 0);
            };
            _shipEngine.DeserializeFromWorld();
            if (_shipEngine.ShipCount > 0)
                _log.LogInformation("Restored {Count} ships from world save", _shipEngine.ShipCount);

            SphereNet.Game.Objects.Characters.Character.ResolveHouseUidsByOwner = ownerUid =>
                _housingEngine?.GetHousesByOwner(ownerUid)
                    .Select(house => house.MultiItem.Uid)
                    .ToArray()
                ?? [];
            SphereNet.Game.Objects.Characters.Character.ResolveShipUidsByOwner = ownerUid =>
                _shipEngine?.AllShips
                    .Where(ship => ship.Owner == ownerUid)
                    .Select(ship => ship.MultiItem.Uid)
                    .ToArray()
                ?? [];

            // Wire Item static delegates for ship resolution
            SphereNet.Game.Objects.Items.Item.ResolveShip = uid => _shipEngine.GetShip(uid);
            SphereNet.Game.Objects.Items.Item.ResolveHouse = uid => _housingEngine?.GetHouse(uid);
            // MULTICREATE verb -> HousingEngine runtime registration
            SphereNet.Game.Objects.Items.Item.OnHouseRegister =
                item => _housingEngine?.RegisterExistingMulti(item);
            // Runtime TYPE=t_spawn_char/item → initialize spawn component
            SphereNet.Game.Objects.Items.Item.OnSpawnTypeChanged = item =>
                item.InitializeSpawnComponent(_world, _resources);

            // Char UID -> GameClient index, used by BroadcastNearby /
            // SendPacketToChar to skip the full _clients.Values scan.
            SphereNet.Game.Clients.GameClient.OnCharacterOnline =
                (ch, client) => _clientsByCharUid[ch.Uid] = client;
            SphereNet.Game.Clients.GameClient.OnCharacterOffline =
                ch => { _clientsByCharUid.Remove(ch.Uid); _macroEngine?.OnCharDisconnect(ch.Uid.Value); };
            SphereNet.Game.Clients.GameClient.OnWakeNpc = WakeNpc;
            SphereNet.Game.Clients.GameClient.BotSpawnLocationProvider = acctName =>
            {
                if (_botEngine == null || !SphereNet.Game.Diagnostics.BotClient.IsBotAccountName(acctName))
                    return null;
                var (x, y, z) = _botEngine.GetRandomSpawnLocation(new Random(acctName.GetHashCode()));
                return new Point3D(x, y, z, 0);
            };

            // Corpse decay -> drop contents to the ground. Invoked by the
            // per-item decay timer in Item.OnTick; replaces the old per-tick
            // full-world scan in DeathEngine.ProcessDecay.
            SphereNet.Game.Objects.Items.Item.OnCorpseDecay = corpse =>
            {
                bool isPlayerCorpse = false;
                if (corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
                    uint.TryParse(ownerStr, out uint ownerUid))
                {
                    var owner = _world.FindChar(new Serial(ownerUid));
                    if (owner != null && owner.IsPlayer)
                        isPlayerCorpse = true;
                }

                foreach (var child in corpse.Contents.ToArray())
                {
                    corpse.RemoveItem(child);
                    if (isPlayerCorpse)
                    {
                        _world.PlaceItem(child, corpse.Position);
                        if (child.DecayTime <= 0)
                            child.DecayTime = Environment.TickCount64 + GameWorld.DefaultDecayTimeMs;
                    }
                    else
                    {
                        child.Delete();
                    }
                }
            };
            SphereNet.Game.Objects.Items.Item.OnTimerExpired = item =>
            {
                return _triggerDispatcher?.FireItemTrigger(item,
                    ItemTrigger.Timer,
                    new TriggerArgs { ItemSrc = item });
            };
            _world.TimerFExpired = (obj, entry) =>
            {
                if (_triggerRunner == null)
                    return;
                var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(obj)
                {
                    ArgString = entry.Args
                };
                _triggerRunner.TryRunFunction(entry.FunctionName, obj, null, trigArgs, out _);
            };
            SphereNet.Game.Objects.Items.Item.ResolveShipEngine = () => _shipEngine;
            SphereNet.Game.Objects.Items.Item.ResolveWorld = () => _world;
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => _world;
            SphereNet.Game.Objects.ObjBase.OnNameChangeWarning = msg =>
                _log.LogWarning("[NAME_CHANGE] {Details}", msg);

            // Route Character-emitted diagnostic lines (vendor restock,
            // trigger verb dispatch, etc.) into the main logger so they
            // appear next to the trig_runner / npc_spawn traces we already
            // rely on while debugging service NPC behaviour.
            SphereNet.Game.Objects.Characters.Character.Diagnostic = msg =>
                _log.LogDebug("{Details}", msg);
            SphereNet.Game.Definitions.TemplateEngine.Diagnostic = msg =>
                _log.LogDebug("{Details}", msg);

            // Guild member properties
            SphereNet.Game.Objects.Characters.Character.ResolveGuildManager = _ => _guildManager;

            // Party properties & commands
            SphereNet.Game.Objects.Characters.Character.ResolvePartyFinder = uid => _partyManager.FindParty(uid);
            SphereNet.Game.Objects.Characters.Character.ResolvePartyManager = () => _partyManager;

            // Character lookup by UID — used for ACCOUNT.CHAR.N.NAME chain and
            // other admin-dialog ref resolution paths.
            SphereNet.Game.Objects.Characters.Character.ResolveCharByUid = uid =>
                _world?.FindChar(uid);

            // Packet delivery back into the character's owning client, for
            // script verbs like ADDBUFF/REMOVEBUFF/SYSMESSAGELOC/ARROWQUEST.
            // No-op when the character has no connected client.
            SphereNet.Game.Objects.Characters.Character.SendPacketToOwner = (target, packet) =>
            {
                if (TryGetClientFor(target, out var c))
                {
                    if (packet is SphereNet.Network.Packets.Outgoing.PacketBuffIcon &&
                        !c.NetState.SupportsBuffIcon)
                        return;
                    c.Send(packet);
                }
            };

            SphereNet.Game.Objects.Characters.Character.SendOwnerMessage = (target, msg) =>
            {
                if (_clientsByCharUid.TryGetValue(target.Uid, out var gc))
                    gc.SysMessage(msg);
            };

            SphereNet.Game.Objects.Characters.Character.NotoSaveUpdate = ch =>
            {
                if (ch == null || _world == null) return;
                ForEachClientInRange(ch.Position, 18, 0, (observerCh, observerClient) =>
                {
                    byte noto = GameClient.ComputeNotoriety(_world, observerCh, ch);
                    byte dir = (byte)((byte)ch.Direction & 0x07);
                    byte flags = 0;
                    if (ch.IsInWarMode) flags |= 0x40;
                    if (ch.IsDead) flags |= 0x02;
                    if (ch.BodyId == 0x0191 || ch.BodyId == 0x0193) flags |= 0x02;
                    if (ch.IsStatFlag(StatFlag.Freeze)) flags |= 0x01;
                    observerClient.Send(new PacketMobileMoving(
                        ch.Uid.Value, ch.BodyId,
                        ch.X, ch.Y, ch.Z, dir,
                        ch.Hue, flags, noto));
                });
            };

            SphereNet.Game.Objects.Characters.Character.ActiveSkillAborted = (ch, skillId) =>
            {
                _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.SkillAbort,
                    new TriggerArgs { CharSrc = ch, N1 = skillId });
                if (_clientsByCharUid.TryGetValue(ch.Uid, out var gc))
                    gc.SysMessage("You stop what you were doing.");
            };

            SphereNet.Game.Objects.Characters.Character.OnStepStealth = ch =>
                _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.StepStealth,
                    new TriggerArgs { CharSrc = ch });

            // Account resolution from character UID
            SphereNet.Game.Objects.Characters.Character.ResolveAccountForChar = uid =>
            {
                var ch = _world.FindChar(uid);
                if (ch == null) return null;
                if (ch.TryGetTag("ACCOUNT", out string? acctName) && !string.IsNullOrEmpty(acctName))
                    return _accounts.FindAccount(acctName);
                foreach (var acct in _accounts.GetAllAccounts())
                {
                    for (int i = 0; i < 7; i++)
                    {
                        if (acct.GetCharSlot(i) == uid)
                            return acct;
                    }
                }
                return null;
            };
            SphereNet.Game.Objects.Items.Item.ResolveGuild = uid => _guildManager.GetGuild(uid);
            SphereNet.Game.Objects.Items.Item.ResolveGuildCharacter = uid => _world.FindChar(uid);
            SphereNet.Game.Objects.Items.Item.ExecuteGuildMemberCommand = (_, memberUid, command) =>
            {
                var ch = _world.FindChar(memberUid);
                return ch != null && _commands.TryExecute(ch, command) == CommandResult.Executed;
            };
            SphereNet.Game.Objects.Items.Item.ExecuteGuildRelationCommand = (_, stoneUid, command) =>
            {
                var guild = _guildManager.GetGuild(stoneUid);
                var masterUid = guild?.GetMaster()?.CharUid ?? Serial.Invalid;
                var ch = masterUid.IsValid ? _world.FindChar(masterUid) : null;
                return ch != null && _commands.TryExecute(ch, command) == CommandResult.Executed;
            };


            // --- Admin dialog verb hooks (DISCONNECT/KICK/RESENDTOOLTIP/...).
            // Each delegate routes a CChar verb back through the right
            // engine. They are nullable for tests that spin up Character
            // without a network/admin context.
            SphereNet.Game.Objects.Characters.Character.DisconnectClient = (target, ban) =>
            {
                if (TryGetClientFor(target, out var c))
                {
                    if (ban)
                    {
                        // Ban semantics: pull the account via the same
                        // resolver Sphere uses for ACCOUNT.* lookups.
                        var acct = SphereNet.Game.Objects.Characters.Character
                            .ResolveAccountForChar?.Invoke(target.Uid);
                        if (acct != null)
                            acct.IsBanned = true;
                    }
                    c.NetState.MarkClosing();
                }
            };

            SphereNet.Game.Objects.Characters.Character.ResendTooltipForAll = target =>
            {
                // No dedicated AOS tooltip-revision packet in the codebase
                // yet. Mark the character's stat flags dirty so the view
                // tick re-emits the status block on the next pass; that is
                // close enough for the admin dialog use case (refresh after
                // toggling Invul/Incognito/etc).
                target.MarkDirty(SphereNet.Core.Enums.DirtyFlag.StatFlags);
            };

            SphereNet.Game.Objects.Characters.Character.OpenInfoDialog = target =>
            {
                // Show inspect dialog on the GM watching the target. The
                // target here is who we want to inspect, not the requester:
                // walk every active client and dispatch the inspect dialog
                // for the target. Caller side (admin dialog) typically
                // chains <Src.info> from the GM's own session, so the GM
                // is the active speech-source — matching ShowInspectDialog
                // expectations.
                if (TryGetClientFor(target, out var c))
                    c.ShowInspectDialog(target.Uid.Value);
            };

            SphereNet.Game.Objects.Characters.Character.OnAppearanceChanged = ch =>
            {
                const int range = 18;
                foreach (var c in _clients.Values)
                {
                    if (!c.IsPlaying || c.Character == null) continue;
                    if (c.Character == ch)
                    {
                        c.SendSelfRedraw();
                        continue;
                    }
                    if (c.Character.MapIndex == ch.MapIndex &&
                        c.Character.Position.GetDistanceTo(ch.Position) <= range)
                    {
                        c.NotifyCharacterAppear(ch);
                    }
                }
            };

            SphereNet.Game.Objects.Characters.Character.BeginTeleTarget = gm =>
            {
                if (TryGetClientFor(gm, out var c))
                    c.BeginTeleportTarget();
            };

            SphereNet.Game.Objects.Characters.Character.SummonCageAround = gm =>
            {
                if (TryGetClientFor(gm, out var c))
                    c.BeginSummonCageTarget();
            };

            SphereNet.Game.Objects.Characters.Character.ResolveClientInfo = ch =>
            {
                if (!TryGetClientFor(ch, out var c))
                    return (0, SphereNet.Game.Objects.Characters.Character.ClientType.ClassicWindows);

                var ct = c.NetState.ParsedClientType switch
                {
                    1 => SphereNet.Game.Objects.Characters.Character.ClientType.Classic3D,
                    2 => SphereNet.Game.Objects.Characters.Character.ClientType.KingdomReborn,
                    3 => SphereNet.Game.Objects.Characters.Character.ClientType.Enhanced,
                    _ => SphereNet.Game.Objects.Characters.Character.ClientType.ClassicWindows,
                };
                return ((int)c.NetState.ClientVersionNumber, ct);
            };

            SphereNet.Game.Objects.Characters.Character.FollowUid = (gm, uid) =>
            {
                // No persistent follow engine yet — Source-X CChar follow
                // skill ticks aren't implemented. Best-effort fallback:
                // teleport the GM next to the target so the dialog row
                // ("Follow this player") still has a useful side effect.
                var targetSerial = new SphereNet.Core.Types.Serial(uid);
                var target = _world?.FindChar(targetSerial);
                if (target == null) return;
                gm.MoveTo(target.Position);
            };

            // --- Observer-visible verbs (ANIM/SOUND/EFFECT/BOW/SALUTE/BARK)
            // and per-owner UI verbs (DCLICK/PACK/BANK) ---
            SphereNet.Game.Objects.Characters.Character.BroadcastNearby = (origin, range, pkt, exclude) =>
            {
                BroadcastNearby(origin, range, pkt, exclude);
            };
            SphereNet.Game.Objects.Characters.Character.OpenPaperdollForOwner = ch =>
            {
                if (TryGetClientFor(ch, out var c))
                    c.SendPaperdoll(ch);
            };
            SphereNet.Game.Objects.Characters.Character.OpenBackpackForOwner = ch =>
            {
                if (TryGetClientFor(ch, out var c))
                {
                    var pack = ch.Backpack;
                    if (pack != null)
                        c.NetState.Send(new SphereNet.Network.Packets.Outgoing.PacketOpenContainer(
                            pack.Uid.Value, 0x003C, c.NetState.IsClientPost7090));
                }
            };
            SphereNet.Game.Objects.Characters.Character.OpenBankboxForOwner = ch =>
            {
                if (TryGetClientFor(ch, out var c))
                    c.OpenBankBox();
            };

            // Mounts
            _mountEngine = new SphereNet.Game.Mounts.MountEngine(_world);

            // Stress-test harness (.stress / .stressreport / .stressclean)
            _stressEngine = new SphereNet.Game.Diagnostics.StressTestEngine(_world, _loggerFactory);
            _commands.OnStressGenerateRequested += (items, npcs) => _stressEngine.QueueGenerate(items, npcs);
            _commands.OnStressReportRequested   += () => _stressEngine.LogReport();
            _commands.OnStressCleanupRequested  += () => _stressEngine.QueueCleanup();

            // Bot stress test engine (.bot / .botmenu)
            _botEngine = new SphereNet.Game.Diagnostics.BotEngine(_loggerFactory.CreateLogger<SphereNet.Game.Diagnostics.BotEngine>());
            // Soak placement (--botcity/--botcluster): the server positions incoming
            // bot logins (BotSpawnLocationProvider -> this engine), so clustering an
            // out-of-process fleet is configured here, not on the runner.
            if (!string.IsNullOrEmpty(_botSpawnCity))
            {
                var sc = _botSpawnCity.ToUpperInvariant() switch
                {
                    "BRITAIN" => SphereNet.Game.Diagnostics.BotSpawnCity.Britain,
                    "TRINSIC" => SphereNet.Game.Diagnostics.BotSpawnCity.Trinsic,
                    "MOONGLOW" => SphereNet.Game.Diagnostics.BotSpawnCity.Moonglow,
                    "YEW" => SphereNet.Game.Diagnostics.BotSpawnCity.Yew,
                    "MINOC" => SphereNet.Game.Diagnostics.BotSpawnCity.Minoc,
                    "VESPER" => SphereNet.Game.Diagnostics.BotSpawnCity.Vesper,
                    "SKARA" => SphereNet.Game.Diagnostics.BotSpawnCity.Skara,
                    "JHELOM" => SphereNet.Game.Diagnostics.BotSpawnCity.Jhelom,
                    _ => SphereNet.Game.Diagnostics.BotSpawnCity.All,
                };
                _botEngine.SetSpawnCity(sc);
            }
            if (_botSpawnCluster)
                _botEngine.SetClusterSpawn(true);
            _commands.OnBotCommandRequested += HandleBotCommand;
            _commands.OnBotMenuRequested += ShowBotManagerDialog;
            _commands.OnSectorListRequested += ShowSectorListDialog;

            _commands.OnRecordDialogRequested += ShowRecordingDialog;
            _commands.OnStateRecordRequested += HandleStateRecordCommand;
            _commands.OnMacroRequested += HandleMacroCommand;
            _commands.OnSaveCommand += RequestSaveOnMainLoop;
            _commands.OnSaveFormatChangeRequested += RequestSaveFormatChangeOnMainLoop;
            _commands.OnScriptDebugToggleRequested += on =>
            {
                exprParser.DebugUnresolved = on;
                _log.LogInformation("Script debug logging: {State}", on ? "ON" : "OFF");
            };
    }

    private static void LoadDefinitionsAndRegions()
    {
            // Load spell/item/char definitions from scripts
            var defSw = Stopwatch.StartNew();
            // Wire diagnostic BEFORE the loader runs — script-load tracing
            // (template body inspection, etc.) only fires during LoadAll.
            DefinitionLoader.Diagnostic = msg => _log.LogDebug("{Details}", msg);
            var defLoader = new DefinitionLoader(_resources, _spellRegistry);
            defLoader.LoadAll();
            defSw.Stop();
            _log.LogInformation("Definitions loaded in {Ms}ms", defSw.ElapsedMilliseconds);

            // Load AREADEF definitions as regions from scripts
            LoadRegionDefs();

            // Load ROOMDEF definitions from scripts
            LoadRoomDefs();

            // Load craft recipes — must come AFTER defLoader.LoadAll() so AllItemDefs is populated
            int recipeCount = _craftingEngine.LoadRecipesFromDefs(_resources);
            if (recipeCount > 0)
                _log.LogInformation("Loaded {Count} craft recipes from SKILLMAKE definitions", recipeCount);
    }

    private static bool StartNetwork()
    {
            // --- 8. Network ---
            _log.LogInformation("Starting network...");
            int maxClients = _config.ClientMax > 0 ? _config.ClientMax : 256;
            _network = new NetworkManager(maxClients, _loggerFactory);
            _network.CryptConfig = _cryptConfig;
            _network.UseCrypt = _config.UseCrypt;
            _network.UseNoCrypt = _config.UseNoCrypt;
            _network.DefaultClientEra = _config.ClientEra;
            _network.DebugPackets = _config.DebugPackets;
            _network.DebugPacketOpcodeFilter = ParseDebugPacketOpcodes(_config.DebugPacketOpcodes);
            _network.MaxPacketsPerTick = _config.MaxPacketsPerTick;
            _network.FloodDetectionCount = _config.FloodDetectionCount;
            _network.FloodDetectionWindowMs = _config.FloodDetectionWindowMs;
            _network.ClientMaxIP = _config.ClientMaxIP;
            _network.PacketScriptHook = HandlePacketScriptHook;
            _log.LogInformation("Crypto keys loaded: {Count}, UseCrypt={UC}, UseNoCrypt={UNC}",
                _cryptConfig.Keys.Count, _config.UseCrypt, _config.UseNoCrypt);
            _network.SetHandlers(
                loginRequest: OnLoginRequest,
                gameLogin: OnGameLogin,
                charSelect: OnCharSelect,
                moveRequest: OnMoveRequest,
                movementBatch: OnMovementBatch,
                speech: OnSpeech,
                attackRequest: OnAttackRequest,
                warMode: OnWarMode,
                doubleClick: OnDoubleClick,
                singleClick: OnSingleClick,
                itemPickup: OnItemPickup,
                itemDrop: OnItemDrop,
                itemEquip: OnItemEquip,
                statusRequest: OnStatusRequest,
                targetResponse: OnTargetResponse,
                gumpResponse: OnGumpResponse,
                clientVersion: OnClientVersion,
                aosTooltip: OnAOSTooltip,
                textCommand: OnTextCommand,
                extendedCommand: OnExtendedCommand,
                resyncRequest: OnResyncRequest,
                logoutRequest: OnLogoutRequest,
                helpRequest: OnHelpRequest,
                serverSelect: OnServerSelect,
                charCreate: OnCharCreate,
                viewRange: OnViewRange,
                vendorBuy: OnVendorBuy,
                vendorSell: OnVendorSell,
                secureTrade: OnSecureTrade,
                rename: OnRename,
                profileRequest: OnProfileRequest,
                // Phase 1
                deathMenu: OnDeathMenu,
                charDelete: OnCharDelete,
                dyeResponse: OnDyeResponse,
                promptResponse: OnPromptResponse,
                menuChoice: OnMenuChoice,
                // Phase 2
                bookPage: OnBookPage,
                bookHeader: OnBookHeader,
                bulletinBoardRequestList: OnBulletinBoardRequestList,
                bulletinBoardRequestMessage: OnBulletinBoardRequestMessage,
                bulletinBoardPost: OnBulletinBoardPost,
                bulletinBoardDelete: OnBulletinBoardDelete,
                mapDetail: OnMapDetail,
                mapPinEdit: OnMapPinEdit,
                // Phase 3
                gumpTextEntry: OnGumpTextEntry,
                allNamesRequest: OnAllNamesRequest
            );

            _network.OnConnectionClosed += OnConnectionClosed;
            _network.OnUnknownPacket += OnUnknownPacket;
            _network.OnPacketQuotaExceeded += OnPacketQuotaExceeded;

            if (!_network.Start("0.0.0.0", _config.ServPort))
            {
                _log.LogError("Failed to start network listener. Exiting.");
            return false;
            }

            _serverStartTime = DateTime.UtcNow;
    #if WINFORMS
            _consoleForm?.SetServerStartTime(_serverStartTime);
    #endif
            _log.LogInformation("SphereNet ready. Listening on port {Port}.", _config.ServPort);
            _systemHooks.DispatchServer("start", _serverHookContext, _config.ServName);
        return true;
    }
}
