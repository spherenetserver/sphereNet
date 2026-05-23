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
    private static string? ResolveServerProperty(string property)
    {
        string upper = property.ToUpperInvariant();
        return upper switch
        {
            // --- Read-only server stats ---
            "CLIENTS" => _network?.ActiveConnections.ToString() ?? "0",
            "ACCOUNTS" => _accounts?.Count.ToString() ?? "0",
            "CHARS" => _world?.TotalChars.ToString() ?? "0",
            "ITEMS" => _world?.TotalItems.ToString() ?? "0",
            "VERSION" => "SphereNet 1.0",
            "SERVNAME" or "NAME" => _config?.ServName ?? "SphereNet",

            // --- Time properties ---
            "TIME" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            "TIMEUP" => ((int)(DateTime.UtcNow - _serverStartTime).TotalSeconds).ToString(),
            "RTIME" => DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy"),
            "RTICKS" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            "TICKPERIOD" => "100",

            // --- Save ---
            "SAVECOUNT" => _saveCount.ToString(),

            // --- Memory ---
            "MEM" => (GC.GetTotalMemory(false) / 1024).ToString(),

            // --- Regeneration rates (tenths of a second in Sphere) ---
            "REGEN0" => (_config?.RegenHits ?? 40).ToString(),
            "REGEN1" => (_config?.RegenStam ?? 20).ToString(),
            "REGEN2" => (_config?.RegenMana ?? 30).ToString(),
            "REGEN3" => (_config?.RegenFood ?? 86400).ToString(),

            // --- Misc ---
            "HEARALL" => "0",
            "GMPAGES" => "0",
            "GUILDS" => "0",
            "SEASON" => ((int)(_weatherEngine?.CurrentSeason ?? SeasonType.Spring)).ToString(),
            "SEASONMODE" => (_weatherEngine?.CurrentSeasonMode ?? SeasonMode.Auto).ToString(),
            "FEATURET2A" => (_config?.FeatureT2A ?? 0).ToString(),
            // Chat system flags are script-visible even when chat is disabled.
            // Return 0 until a fuller chat subsystem is wired.
            "CHATFLAGS" => "0",

            // --- Reference lookups via SERV.xxx ---
            "LASTNEWITEM" => _world?.LastNewItem.Value.ToString() ?? "0",
            "LASTNEWCHAR" => _world?.LastNewChar.Value.ToString() ?? "0",

            // --- SERV.MAP* ---
            _ when upper.StartsWith("MAPLIST.") => ResolveMapListProperty(upper[8..]),

            // --- SERV.SKILL.n.KEY / SERV.SKILL.n.NAME — skill table lookup.
            // Admin dialogs iterate all 58 skills using <Serv.Skill.<idx>.Key>
            // to discover defnames at runtime.
            _ when upper.StartsWith("SKILL.") => ResolveServSkill(upper[6..]),

            // --- SERV.CHARDEF.<defname>.<prop> / SERV.ITEMDEF.<defname>.<prop>
            // Used by script dialogs like d_spawn to render names/jobs from
            // definition data without instantiating the object.
            _ when upper.StartsWith("CHARDEF.") => ResolveServCharDef(property[8..]),
            _ when upper.StartsWith("ITEMDEF.") => ResolveServItemDef(property[8..]),

            // --- ISEVENT.name — 1 if the named event script is loaded.
            // Used by admin dialogs to grey out delete buttons for missing events.
            _ when upper.StartsWith("ISEVENT.") => ResolveIsEvent(upper[8..]),

            // --- SERV.ACCOUNT.name or SERV.ACCOUNT.n ---
            _ when upper.StartsWith("ACCOUNT.") => ResolveServAccount(upper[8..]),

            // --- SERV.MAP.n.SECTOR.n.property ---
            _ when upper.StartsWith("MAP.") => ResolveServMapSector(upper[4..]),

            // --- SERV.MAP(x,y,z,m).property — Source-X function form
            // Used by d_admin houses/ships row to look up the
            // region name at a given world point:
            //   <Serv.Map(<REF1.P.X>,<REF1.P.Y>,0,<REF1.P.M>).Region.Name>
            _ when upper.StartsWith("MAP(") => ResolveServMapPoint(property[4..]),

            // --- RTIME.FORMAT / RTICKS.FORMAT / RTICKS.FROMTIME (standalone, forwarded here) ---
            _ when upper.StartsWith("RTIME.FORMAT") => ResolveRtimeFormat(upper),
            _ when upper.StartsWith("RTICKS.FORMAT") => ResolveRticksFormat(upper),
            _ when upper.StartsWith("RTICKS.FROMTIME") => ResolveRticksFromTime(upper),

            // --- SERV.LOOKUPSKILL <name> — reverse lookup skill id by name.
            // Returns -1 on miss to match Source-X behaviour.
            _ when upper.StartsWith("LOOKUPSKILL ") => ResolveLookupSkill(property[12..]),
            _ when upper.StartsWith("LOOKUPSKILL(") => ResolveLookupSkill(
                property.EndsWith(")") ? property[12..^1] : property[12..]),

            // --- Global variables: VAR.name / VAR0.name ---
            _ when upper.StartsWith("VAR0.") => _world?.GetGlobalVar0(property[5..]) ?? "0",
            _ when upper.StartsWith("VAR.") => _world?.GetGlobalVar(property[4..]) ?? "",

            // --- OBJ / OBJ.property — global object reference ---
            "OBJ" => _world?.ObjReference.Value != 0 ? $"0{_world!.ObjReference.Value:X}" : "0",
            _ when upper.StartsWith("OBJ.") => ResolveObjProperty(property[4..]),

            // --- NEW / NEW.property — last created object ---
            "NEW" => _world?.LastNewItem.Value != 0 ? $"0{_world!.LastNewItem.Value:X}" :
                      _world?.LastNewChar.Value != 0 ? $"0{_world!.LastNewChar.Value:X}" : "0",
            _ when upper.StartsWith("NEW.") => ResolveNewProperty(property[4..]),

            // --- UID.0xHEX.property — direct object access ---
            _ when upper.StartsWith("UID.") => ResolveUidProperty(property[4..]),

            // --- DEFMSG.name — default message lookup ---
            _ when upper.StartsWith("DEFMSG.") => ResolveDefMsg(property[7..]),

            // --- Commands (write operations, prefixed with _SET_/_CLEARVARS/_NEWDUPE) ---
            _ when upper.StartsWith("_SET_VAR.") => HandleSetGlobalVar(property[9..]),
            _ when upper.StartsWith("_SET_OBJ=") => HandleSetObj(property[9..]),
            _ when upper.StartsWith("_SET_OBJ.") => HandleSetObjProperty(property[9..]),
            _ when upper.StartsWith("_CLEARVARS=") => HandleClearVars(property[11..]),
            _ when upper.StartsWith("_NEWDUPE=") => HandleNewDupe(property[9..]),
            _ when upper.StartsWith("_SET_DEFMSG=") => HandleSetDefMsg(property[12..]),
            _ when upper.StartsWith("_SET_SEASON=") => HandleSetSeason(property[12..]),

            // REF object property access
            _ when upper.StartsWith("_REF_GET=") => HandleRefGet(property[9..]),
            _ when upper.StartsWith("_REF_EXEC=") => HandleRefExec(property[10..]),

            // serv.allclients <function> — invoke <function> once per
            // online player, with the caller as src. Protocol format:
            // _ALLCLIENTS=<srcUid>|<funcName>.
            _ when upper.StartsWith("_ALLCLIENTS=") => HandleAllClients(property[12..]),

            // serv.resync / serv.save / serv.shutdown — admin write
            // verbs reachable from dialog buttons (d_admin_function).
            // Sphere fires them as bare property reads on the server
            // resolver; we hijack and run the matching engine action.
            "RESYNC" => HandleServResync(),
            "SAVE" => HandleServSave(),
            "SHUTDOWN" => HandleServShutdown(""),
            _ when upper.StartsWith("SHUTDOWN ") => HandleServShutdown(property[9..]),

            // Region property access
            _ when upper.StartsWith("_REGION_GET=") => HandleRegionGet(property[12..]),

            // Room property access
            _ when upper.StartsWith("_ROOM_GET=") => HandleRoomGet(property[10..]),

            // Bare defname constants (e.g. statf_insubstantial) used by
            // script expressions without DEF./DEF0. prefix.
            _ => ResolveDefConstant(upper)
        };
    }

    /// <summary>Resolve <c>ISEVENT.name</c>. Returns "1" when a script event
    /// with this defname exists in the loaded resource set, else "0".</summary>
    private static string? ResolveIsEvent(string eventName)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(eventName)) return "0";
        var rid = _resources.ResolveDefName(eventName);
        if (!rid.IsValid)
            rid = _resources.ResolveDefName("e_" + eventName);
        return rid.IsValid ? "1" : "0";
    }

    /// <summary>Resolve <c>SERV.SKILL.n[.KEY|NAME]</c>. Returns the enum name
    /// (e.g. "Alchemy") which matches Source-X defname for that skill slot.</summary>
    private static string? ResolveServSkill(string sub)
    {
        int dot = sub.IndexOf('.');
        string idxStr = dot < 0 ? sub : sub[..dot];
        string field = dot < 0 ? "" : sub[(dot + 1)..].ToUpperInvariant();
        if (!int.TryParse(idxStr, out int idx)) return null;
        if (!Enum.IsDefined(typeof(SphereNet.Core.Enums.SkillType), (short)idx))
            return "";
        string skillName = ((SphereNet.Core.Enums.SkillType)idx).ToString();
        if (string.IsNullOrEmpty(field) || field == "KEY" || field == "NAME" || field == "DEFNAME")
            return skillName;
        return "";
    }

    private static string HandleSetSeason(string raw)
    {
        if (_weatherEngine == null)
            return "0";

        string trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "0";

        SeasonType season;
        if (byte.TryParse(trimmed, out byte seasonNum) &&
            Enum.IsDefined(typeof(SeasonType), (int)seasonNum))
        {
            season = (SeasonType)seasonNum;
        }
        else if (!Enum.TryParse(trimmed, ignoreCase: true, out season))
        {
            return "0";
        }

        bool changed = _weatherEngine.SetSeason(season, resetCycleTimer: true);
        if (changed)
            BroadcastSeasonChange(playSound: true);
        return ((int)_weatherEngine.CurrentSeason).ToString();
    }

    private static string? ResolveServCharDef(string sub)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(sub))
            return "";

        int dot = sub.IndexOf('.');
        if (dot <= 0)
            return "";

        string defName = sub[..dot].Trim();
        string field = sub[(dot + 1)..].Trim().ToUpperInvariant();
        var rid = _resources.ResolveDefName(defName);
        if (!rid.IsValid || rid.Type != SphereNet.Core.Enums.ResType.CharDef)
            return "";

        var def = SphereNet.Game.Definitions.DefinitionLoader.GetCharDef(rid.Index);
        if (def == null)
            return "";

        return field switch
        {
            "NAME" => def.Name ?? "",
            "JOB" => def.Job ?? "",
            "DEFNAME" => def.DefName ?? "",
            "ID" or "DISPID" => $"0{def.DispIndex:X}",
            "ICON" => def.Icon ?? "",
            "NPC" or "NPCBRAIN" => def.NpcBrain.ToString(),
            _ => ""
        };
    }

    private static string? ResolveServItemDef(string sub)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(sub))
            return "";

        int dot = sub.IndexOf('.');
        if (dot <= 0)
            return "";

        string defName = sub[..dot].Trim();
        string field = sub[(dot + 1)..].Trim().ToUpperInvariant();
        var rid = _resources.ResolveDefName(defName);
        if (!rid.IsValid || rid.Type != SphereNet.Core.Enums.ResType.ItemDef)
            return "";

        var def = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(rid.Index);
        if (def == null)
            return "";

        return field switch
        {
            "NAME" => def.Name ?? "",
            "DEFNAME" => def.DefName ?? "",
            "ID" or "DISPID" => $"0{def.DispIndex:X}",
            "TYPE" => def.Type.ToString(),
            _ => ""
        };
    }

    private static string? ResolveObjProperty(string subProp)
    {
        if (_world == null) return "0";
        var obj = _world.FindObject(_world.ObjReference);
        if (obj == null) return "0";
        return obj.TryGetProperty(subProp, out string val) ? val : "0";
    }

    private static string? ResolveNewProperty(string subProp)
    {
        if (_world == null) return "0";
        // Try last new item first, then last new char
        var obj = _world.FindObject(_world.LastNewItem) ?? _world.FindObject(_world.LastNewChar);
        if (obj == null) return "0";
        return obj.TryGetProperty(subProp, out string val) ? val : "0";
    }

    private static string? ResolveUidProperty(string uidAndProp)
    {
        if (_world == null) return null;
        // Format: 0xHEXVALUE.property or HEXVALUE.property
        int dot = uidAndProp.IndexOf('.');
        if (dot <= 0) return null;
        string uidStr = uidAndProp[..dot].Trim();
        string prop = uidAndProp[(dot + 1)..].Trim();
        // Strip leading 0 or 0x
        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return null;
        var obj = _world.FindObject(new SphereNet.Core.Types.Serial(uid));
        if (obj == null) return "0";
        return obj.TryGetProperty(prop, out string val) ? val : "0";
    }

    private static string? ResolveDefMsg(string msgName)
    {
        if (_resources != null && _resources.TryGetDefMessage(msgName, out string defMsg))
            return defMsg;
        return "";
    }

    private static string? ResolveDefConstant(string upperToken)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(upperToken))
            return null;
        // Only allow plain identifier-like tokens here so arithmetic and
        // command payloads don't accidentally route into defname lookups.
        for (int i = 0; i < upperToken.Length; i++)
        {
            char ch = upperToken[i];
            bool ok = (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch is '_' or '.';
            if (!ok) return null;
        }
        var rid = _resources.ResolveDefName(upperToken);
        if (rid.IsValid) return rid.Index.ToString();

        // Built-in STATF_* fallback when scripts reference status-flag
        // constants directly and the defname pack doesn't provide them.
        // Example: (<Flags> & statf_insubstantial)
        if (upperToken.StartsWith("STATF_", StringComparison.Ordinal))
        {
            string suffix = upperToken[6..];
            foreach (SphereNet.Core.Enums.StatFlag flag in Enum.GetValues(typeof(SphereNet.Core.Enums.StatFlag)))
            {
                string enumName = flag.ToString().ToUpperInvariant();
                if (enumName == suffix || enumName.Replace("_", "", StringComparison.Ordinal) == suffix)
                    return ((uint)flag).ToString();
            }
        }
        return null;
    }

    private static string? HandleSetGlobalVar(string assignment)
    {
        // Format: name=value
        int eq = assignment.IndexOf('=');
        if (eq <= 0) return "";
        string name = assignment[..eq].Trim();
        string value = assignment[(eq + 1)..].Trim();
        _world?.SetGlobalVar(name, value);
        return "";
    }

    private static string? HandleSetObj(string uidStr)
    {
        if (_world == null) return "";
        string v = uidStr.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            v = v[2..];
        else if (v.StartsWith("0", StringComparison.Ordinal) && v.Length > 1)
            v = v[1..];
        if (uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            _world.ObjReference = new SphereNet.Core.Types.Serial(uid);
        else
            _world.ObjReference = SphereNet.Core.Types.Serial.Invalid;
        return "";
    }

    private static string? HandleSetObjProperty(string propAssignment)
    {
        if (_world == null) return "";
        // Format: property=value
        int eq = propAssignment.IndexOf('=');
        if (eq <= 0) return "";
        string prop = propAssignment[..eq].Trim();
        string val = propAssignment[(eq + 1)..].Trim();
        var obj = _world.FindObject(_world.ObjReference);
        obj?.TrySetProperty(prop, val);
        return "";
    }

    private static string? HandleClearVars(string prefix)
    {
        _world?.ClearGlobalVars(string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim());
        return "";
    }

    private static string? HandleNewDupe(string uidStr)
    {
        if (_world == null) return "";
        string v = uidStr.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            v = v[2..];
        else if (v.StartsWith("0", StringComparison.Ordinal) && v.Length > 1)
            v = v[1..];
        if (!uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return "";
        var original = _world.FindObject(new SphereNet.Core.Types.Serial(uid));
        if (original is SphereNet.Game.Objects.Items.Item origItem)
        {
            var clone = _world.CreateItem();
            clone.BaseId = origItem.BaseId;
            clone.Name = origItem.Name;
            clone.Hue = origItem.Hue;
            clone.Amount = origItem.Amount;
            // Copy TAGs
            foreach (var kvp in origItem.Tags.GetAll())
                clone.Tags.Set(kvp.Key, kvp.Value);
        }
        else if (original is SphereNet.Game.Objects.Characters.Character origChar)
        {
            var clone = _world.CreateCharacter();
            clone.BaseId = origChar.BaseId;
            clone.Name = origChar.Name;
            clone.Hue = origChar.Hue;
            clone.Position = origChar.Position;
            foreach (var kvp in origChar.Tags.GetAll())
                clone.Tags.Set(kvp.Key, kvp.Value);
        }
        return "";
    }

    private static string? HandleSetDefMsg(string assignment)
    {
        // DEFMSG name=value — we just log it, no persistent message override in our impl yet
        return "";
    }

    /// <summary>Get property from object referenced by UID. Format: "uidHex|property"</summary>
    private static string? HandleRefGet(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "0";
        string uidStr = data[..pipe].Trim();
        string prop = data[(pipe + 1)..].Trim();
        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return "0";
        var obj = _world.FindObject(new SphereNet.Core.Types.Serial(uid));
        if (obj == null) return "0";
        return obj.TryGetProperty(prop, out string val) ? val : "0";
    }

    /// <summary>Execute command on object referenced by UID. Format: "uidHex|command|args"</summary>
    private static string? HandleRefExec(string data)
    {
        var parts = data.Split('|', 3);
        if (parts.Length < 2 || _world == null) return "";
        string uidStr = parts[0].Trim();
        string cmd = parts[1].Trim();
        string cmdArgs = parts.Length > 2 ? parts[2].Trim() : "";
        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return "";
        var obj = _world.FindObject(new SphereNet.Core.Types.Serial(uid));
        if (obj == null) return "";
        // Try set property first, then execute command with a minimal console
        if (cmdArgs.Length > 0 && obj.TrySetProperty(cmd, cmdArgs))
            return "";
        if (obj.TryExecuteCommand(cmd, cmdArgs, new RefExecConsole()))
            return "";

        // Client-scoped verbs (DIALOG, SDIALOG, MENU, INPDLG, GUMPDISPLAY, ...)
        // are not implemented on the object itself; they live on the
        // owning player's GameClient. Imported sphere admin scripts use
        //   UID.<player>.Dialog d_X
        // to push a dialog to a specific player. Route the verb to that
        // player's client so the gump actually goes out the wire.
        if (obj is Character ch)
        {
            var gc = FindGameClient(ch);
            if (gc != null)
                gc.TryExecuteScriptCommand(obj, cmd, cmdArgs, null);
        }
        return "";
    }

    /// <summary>Iterate all online players and invoke a script function
    /// on each. Format: "srcUid|funcName". Caller stays as src; each
    /// iterated player becomes the function's target. Sphere admin
    /// scripts use this pattern to tally online clients or push a
    /// system message to everyone.</summary>
    private static string? HandleAllClients(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "";
        string uidStr = data[..pipe].Trim();
        string payload = data[(pipe + 1)..].Trim();
        if (string.IsNullOrEmpty(payload)) return "";

        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint srcUid))
            return "";

        var srcObj = _world.FindObject(new SphereNet.Core.Types.Serial(srcUid));
        if (srcObj is not SphereNet.Game.Objects.Characters.Character srcChar)
            return "";

        // Source-X allows two payload shapes:
        //   serv.allclients f_count_players          → call function f_count_players
        //   serv.allclients sysmessage @0481 Booting → execute verb on every client
        // Detect verb form by splitting first token and asking the
        // target object whether it can execute it. Works for SYSMESSAGE,
        // MESSAGE, EVENTS, SHRINK, KICK, ... — the standard CChar verbs.
        int sp = payload.IndexOfAny(new[] { ' ', '\t' });
        string head = sp > 0 ? payload[..sp] : payload;
        string tail = sp > 0 ? payload[(sp + 1)..].TrimStart() : "";

        var snapshot = _clients.Values.Where(c => c.IsPlaying && c.Character != null).ToList();
        foreach (var client in snapshot)
        {
            var target = client.Character!;
            var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(srcChar)
            {
                Object1 = target,
                Object2 = srcChar,
            };

            bool dispatched = false;
            // Try as verb first — TryExecuteCommand returns false for
            // unknown verbs, falling through to function-call form.
            if (target.TryExecuteCommand(head, tail, client))
                dispatched = true;
            else if (client.TryExecuteScriptCommand(target, head, tail, trigArgs))
                dispatched = true;

            if (!dispatched && _triggerRunner != null)
                _triggerRunner.TryRunFunction(payload, target, client, trigArgs, out _);
        }
        return "";
    }

    /// <summary>Source-X <c>serv.resync</c> from a script: trigger the
    /// same hot-reload pipeline used by the telnet/console RESYNC verb.</summary>
    private static string? HandleServResync()
    {
        try { PerformScriptResync(); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Script-driven serv.resync failed");
        }
        return "";
    }

    /// <summary>Source-X <c>serv.save</c>: route to the standard world
    /// save path (<see cref="PerformSave"/>) so dialog-driven backups
    /// produce identical .scp output.</summary>
    private static string? HandleServSave()
    {
        try { PerformSave(); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Script-driven serv.save failed");
        }
        return "";
    }

    /// <summary>Source-X <c>serv.shutdown [seconds]</c>. Without an arg,
    /// shut down immediately; with a delay, schedule a single-shot
    /// timer. The scheduling is best-effort — the server tick loop is
    /// what actually exits when <c>_running</c> flips to false.</summary>
    private static string? HandleServShutdown(string args)
    {
        int delaySec = 0;
        if (!string.IsNullOrWhiteSpace(args))
            int.TryParse(args.Trim(), out delaySec);

        if (delaySec <= 0)
        {
            _running = false;
            return "";
        }

        // Detached delay; we don't await it because the resolver runs on
        // the script tick path and must return synchronously.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delaySec * 1000);
                _running = false;
            }
            catch { /* ignore */ }
        });
        return "";
    }

    /// <summary>Get property from region referenced by UID. Format: "uid|property"</summary>
    private static string? HandleRegionGet(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "0";
        string uidStr = data[..pipe].Trim();
        string prop = data[(pipe + 1)..].Trim();
        if (!uint.TryParse(uidStr, out uint regionUid))
            return "0";
        var region = _world.FindRegionByUid(regionUid);
        if (region == null) return "0";
        return region.TryGetProperty(prop, out string val) ? val : "0";
    }

    /// <summary>Get property from room referenced by UID. Format: "uid|property"</summary>
    private static string? HandleRoomGet(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "0";
        string uidStr = data[..pipe].Trim();
        string prop = data[(pipe + 1)..].Trim();
        if (!uint.TryParse(uidStr, out uint roomUid))
            return "0";
        var room = _world.FindRoomByUid(roomUid);
        if (room == null) return "0";
        return room.TryGetProperty(prop, out string val) ? val : "0";
    }

    private static string? ResolveRtimeFormat(string property)
    {
        // RTIME.FORMAT <format> — format current time
        // Property arrives as "RTIME.FORMAT <format>" or just "RTIME.FORMAT"
        int spaceIdx = property.IndexOf(' ');
        if (spaceIdx < 0)
            return DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy");
        string fmt = property[(spaceIdx + 1)..].Trim();
        try { return DateTime.Now.ToString(fmt); }
        catch { return DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy"); }
    }

    private static string? ResolveRticksFormat(string property)
    {
        // RTICKS.FORMAT <timestamp>,<format>
        var parts = property.Split(' ', 2);
        if (parts.Length < 2) return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var args = parts[1].Split(',', 2);
        if (args.Length < 2 || !long.TryParse(args[0].Trim(), out long ts))
            return "0";
        try
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
            return dt.ToString(args[1].Trim());
        }
        catch { return "0"; }
    }

    private static string? ResolveRticksFromTime(string property)
    {
        // RTICKS.FROMTIME <year>,<month>,<day>,<hour>,<min>,<sec>
        var parts = property.Split(' ', 2);
        if (parts.Length < 2) return "0";
        var args = parts[1].Split(',');
        if (args.Length < 3) return "0";
        try
        {
            int year = int.Parse(args[0].Trim());
            int month = int.Parse(args[1].Trim());
            int day = int.Parse(args[2].Trim());
            int hour = args.Length > 3 ? int.Parse(args[3].Trim()) : 0;
            int min = args.Length > 4 ? int.Parse(args[4].Trim()) : 0;
            int sec = args.Length > 5 ? int.Parse(args[5].Trim()) : 0;
            var dt = new DateTimeOffset(year, month, day, hour, min, sec, TimeSpan.Zero);
            return dt.ToUnixTimeSeconds().ToString();
        }
        catch { return "0"; }
    }

    /// <summary>Resolve <c>SERV.MAP(x,y,z,m).Region.Name</c> and similar
    /// Source-X function-form lookups. Splits the args inside parens on
    /// commas, looks up the region containing the point, then forwards
    /// the trailing <c>.Region.X</c> sub-property chain to the region
    /// object via <see cref="HandleRegionGet"/>.</summary>
    private static string? ResolveServMapPoint(string rest)
    {
        // rest is e.g. "1455,1612,0,0).Region.Name" — strip up to ')'.
        int close = rest.IndexOf(')');
        if (close < 0) return "0";
        string argList = rest[..close];
        string trailing = rest[(close + 1)..];
        if (trailing.StartsWith('.')) trailing = trailing[1..];

        var parts = argList.Split(',');
        if (parts.Length < 2) return "0";

        if (!int.TryParse(parts[0].Trim(), out int x)) return "0";
        if (!int.TryParse(parts[1].Trim(), out int y)) return "0";
        int z = parts.Length > 2 && int.TryParse(parts[2].Trim(), out int parsedZ) ? parsedZ : 0;
        int m = parts.Length > 3 && int.TryParse(parts[3].Trim(), out int parsedM) ? parsedM : 0;

        var pt = new SphereNet.Core.Types.Point3D((short)x, (short)y, (sbyte)z, (byte)m);
        if (_world == null) return "0";

        // Bare "MAP(x,y,z,m)" or trailing "P": just echo the point.
        if (string.IsNullOrEmpty(trailing) || trailing.Equals("P", StringComparison.OrdinalIgnoreCase))
            return pt.ToString();

        // REGION[.prop] — delegate to the existing HandleRegionGet path.
        if (trailing.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
        {
            var region = _world.FindRegion(pt);
            if (region == null) return "";
            string regionRest = trailing.Length > 6 && trailing[6] == '.' ? trailing[7..] : "";
            if (string.IsNullOrEmpty(regionRest)) return region.Name;
            return region.TryGetProperty(regionRest, out string rv) ? rv : "0";
        }

        // ROOM[.prop] — same idea but no per-point room lookup engine yet.
        return null;
    }

    private static string? ResolveServMapSector(string rest)
    {
        // MAP.0.SECTOR.n or MAP.0.SECTOR.n.property or MAP.0.ALLSECTORS
        int firstDot = rest.IndexOf('.');
        if (firstDot < 0) return null;

        string mapStr = rest[..firstDot];
        if (!int.TryParse(mapStr, out int mapNum)) return null;

        string sub = rest[(firstDot + 1)..];

        if (sub.StartsWith("SECTOR.", StringComparison.OrdinalIgnoreCase))
        {
            string sectorPart = sub[7..]; // after "SECTOR."
            int propDot = sectorPart.IndexOf('.');
            string sectorIdxStr = propDot >= 0 ? sectorPart[..propDot] : sectorPart;
            if (!int.TryParse(sectorIdxStr, out int sectorIdx)) return null;

            // Convert linear index to x,y (assuming 96 cols for map 0)
            int cols = 96;
            int sx = sectorIdx % cols;
            int sy = sectorIdx / cols;
            var sector = _world?.GetSector(mapNum, sx, sy);
            if (sector == null) return "0";

            if (propDot < 0) return sector.GetName(); // just "MAP.0.SECTOR.n" — return name

            string prop = sectorPart[(propDot + 1)..];
            if (sector.TryGetProperty(prop, out string val))
                return val;
            return "0";
        }

        return null;
    }

    private static string? ResolveMapListProperty(string rest)
    {
        // MAPLIST.0 → 1 (valid), MAPLIST.0.BOUND.X → max X, etc.
        int dotIdx = rest.IndexOf('.');
        string mapStr = dotIdx >= 0 ? rest[..dotIdx] : rest;
        if (!int.TryParse(mapStr, out int mapNum))
            return null;

        // Only map 0 (Felucca) supported currently
        if (mapNum != 0)
            return "0";

        if (dotIdx < 0)
            return "1"; // map exists

        string sub = rest[(dotIdx + 1)..];
        return sub switch
        {
            "BOUND.X" => "6144",
            "BOUND.Y" => "4096",
            "CENTER.X" => "3072",
            "CENTER.Y" => "2048",
            "SECTOR.SIZE" => "64",
            "SECTOR.COLS" => "96",
            "SECTOR.ROWS" => "64",
            "SECTOR.QTY" => "6144",
            _ => null
        };
    }

    /// <summary>Resolve <c>SERV.LOOKUPSKILL &lt;name&gt;</c>. Accepts either the
    /// enum name ("Alchemy") or the defname stored in a loaded SKILL block.
    /// Returns the numeric skill id, or "-1" if no match.</summary>
    private static string? ResolveLookupSkill(string name)
    {
        string trimmed = (name ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) return "-1";

        // Enum name match first (case-insensitive)
        if (Enum.TryParse<SphereNet.Core.Enums.SkillType>(trimmed, true, out var sk)
            && sk != (SphereNet.Core.Enums.SkillType)(-1))
        {
            return ((int)sk).ToString();
        }

        // Defname lookup via the resource holder: SKILLs register under
        // their defname ("Skill_Alchemy" or "Alchemy") so the resolver
        // can map script names back to the enum slot.
        if (_resources != null)
        {
            var rid = _resources.ResolveDefName(trimmed);
            if (rid.IsValid && rid.Type == SphereNet.Core.Enums.ResType.SkillDef)
                return rid.Index.ToString();
        }

        return "-1";
    }

    private static string? ResolveServAccount(string rest)
    {
        if (_accounts == null) return null;

        // SERV.ACCOUNT.name → account reference
        // For script property lookups we return the account name or "0" if not found
        var acct = _accounts.FindAccount(rest);
        if (acct != null)
            return acct.Name;

        // SERV.ACCOUNT.n (zero-based index) — not easily supported with dictionary, return "0"
        if (int.TryParse(rest, out _))
            return "0";

        return null;
    }


    /// <summary>Minimal ITextConsole for REF command execution.</summary>
    private sealed class RefExecConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public string GetName() => "SERVER";
        public void SysMessage(string text) { }
    }

    private sealed class ServerHookContext : IScriptObj
    {
        public string GetName() => "SERVER";

        public bool TryGetProperty(string key, out string value)
        {
            value = key.Equals("NAME", StringComparison.OrdinalIgnoreCase) ? "SERVER" : "";
            return key.Equals("NAME", StringComparison.OrdinalIgnoreCase);
        }

        public bool TryExecuteCommand(string key, string args, ITextConsole source) => false;

        public bool TrySetProperty(string key, string value) => false;

        public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args) => TriggerResult.Default;
    }
}
