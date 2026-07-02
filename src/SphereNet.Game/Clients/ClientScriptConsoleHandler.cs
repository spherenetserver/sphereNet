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

/// <summary>
/// Script-verb handler extracted from the GameClient.ScriptConsole partial
/// (decomposition phase 3 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Hosts the script-command surface (TryExecuteScriptCommand verbs: MESSAGE,
/// SAYUA, INPDLG, TRYSRC, TARGETF, MENU, DIALOG, SERV.*, FILE.*, DB.*/LDB.*,
/// SENDPACKET), script-variable resolution and the FOR* object queries.
/// Method bodies moved verbatim; the private context shims below enumerate
/// exactly what this handler needs from GameClient. The ITextConsole
/// implementation (SysMessage/GetName/GetPrivLevel) stays on GameClient -
/// scripts receive the GameClient as their console identity.
/// </summary>
public sealed class ClientScriptConsoleHandler
{
    private readonly IClientContext _client;

    internal ClientScriptConsoleHandler(IClientContext client)
    {
        _client = client;
    }

    // --- context shims (the GameClient surface this handler depends on) ---
    private Character? _character => _client.Character;
    private GameWorld _world => _client.World;
    private NetState _netState => _client.NetState;
    private Account? _account => _client.Account;
    private CommandHandler? _commands => _client.Cmds;
    private TriggerDispatcher? _triggerDispatcher => _client.Triggers;
    private ILogger _logger => _client.Log;
    private ScriptFileHandle? _scriptFile => _client.ScriptFile;
    private ScriptDbAdapter? _scriptDb => _client.ScriptDb;
    private ScriptDbAdapter? _scriptLdb => _client.ScriptLdb;
    private ScriptDbAdapter? _scriptMdb => _client.ScriptMdb;
    private string _scriptDatabaseRoot => _client.ScriptDatabaseRoot;
    private ClientTargetState Targets => _client.Targets;
    private ClientGumpRegistry Gumps => _client.Gumps;
    private ClientDialogHandler Dialogs => _client.Dialogs;
    private int _dialogDepth;
    private ushort _pendingMenuId { get => _client.PendingMenuId; set => _client.PendingMenuId = value; }
    private string _pendingMenuDefname { get => _client.PendingMenuDefname; set => _client.PendingMenuDefname = value; }
    private List<MenuOptionEntry>? _pendingMenuOptions { get => _client.PendingMenuOptions; set => _client.PendingMenuOptions = value; }
    private string? _pendingDialogCloseFunction
    {
        get => _client.PendingDialogCloseFunction;
        set => _client.PendingDialogCloseFunction = value;
    }
    private string _pendingDialogArgs
    {
        get => _client.PendingDialogArgs;
        set => _client.PendingDialogArgs = value;
    }
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint>? BroadcastNearby => _client.BroadcastNearby;
    private void SysMessage(string text) => _client.SysMessage(text);
    private void Send(SphereNet.Network.Packets.PacketWriter packet) => _client.Send(packet);
    private void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null) => _client.SendGump(gump, callback);
    private void OpenVendorBuy(Character vendor) => _client.OpenVendorBuy(vendor);
    private void OpenBankBox() => _client.OpenBankBox();
    private bool CloseScriptDialog(string dialogId) => _client.CloseScriptDialog(dialogId);
    private bool IsScriptDialogOpen(string dialogId) => _client.IsScriptDialogOpen(dialogId);
    private bool OpenNamedDialog(string dialogId, int requestedPage = 0, ObjBase? subject = null) => _client.OpenNamedDialog(dialogId, requestedPage, subject);
    private void ClearPendingTargetState() => _client.ClearPendingTargetState();
    private void Resync() => _client.Resync();
    private void BroadcastDrawObject(Character ch) => _client.BroadcastDrawObject(ch);
    private void SendInputPromptGump(IScriptObj target, string propName, int maxLength) => _client.SendInputPromptGump(target, propName, maxLength);
    private void BeginXVerbTarget(string verb, string args) => _client.BeginXVerbTarget(verb, args);
    private bool TryFindMenuSection(string menuDefname, out SphereNet.Scripting.Parsing.ScriptSection menuSection) => _client.TryFindMenuSection(menuDefname, out menuSection);
    private static bool IsPlainDefToken(string token) => GameClient.IsPlainDefToken(token);

    /// <summary>Unimplemented SERV.* verbs seen this server run — used to
    /// warn-log only the first occurrence of each.</summary>
    private static readonly HashSet<string> s_unknownServVerbs = [];

    /// <summary>Vendor for the script BUY verb: the current dialog subject
    /// when it's a vendor NPC, otherwise the nearest vendor in range.</summary>
    private Character? ResolveVendorContext()
    {
        if (_character == null)
            return null;
        if (Dialogs.DialogSubjectUid.IsValid &&
            _world.FindChar(Dialogs.DialogSubjectUid) is { } subject &&
            !subject.IsPlayer && subject.NpcBrain == NpcBrainType.Vendor)
            return subject;

        Character? nearest = null;
        int bestDist = int.MaxValue;
        foreach (var ch in _world.GetCharsInRange(_character.Position, 8))
        {
            if (ch.IsPlayer || ch.IsDeleted || ch.NpcBrain != NpcBrainType.Vendor)
                continue;
            int dist = _character.Position.GetDistanceTo(ch.Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = ch;
            }
        }
        return nearest;
    }

    public bool TryExecuteScriptCommand(IScriptObj target, string key, string args, ITriggerArgs? triggerArgs)
    {
        if (_character == null) return false;

        string cmd = key.Trim();
        string upper = cmd.ToUpperInvariant();

        if (upper == "OBJ")
        {
            if (triggerArgs?.Object1 is Character objCh)
                _character.SetTag("OBJ", $"0{objCh.Uid.Value:X}");
            else if (triggerArgs?.Object1 is Item objItem)
                _character.SetTag("OBJ", $"0{objItem.Uid.Value:X}");
            return true;
        }

        // Source-X SET meta-verb: "Src.set <verb> [args]" pops a target
        // cursor and re-dispatches the verb against the picked object.
        // Sphere admin dialogs lean on this for "set dupe", "set
        // remove", "set xinfo" rows on the player tweak panel.
        if (upper == "SET" || upper == "SETUID")
        {
            string raw = args?.Trim() ?? "";
            if (raw.Length == 0) return true;
            int sp = raw.IndexOfAny(new[] { ' ', '\t' });
            string verb = sp > 0 ? raw[..sp] : raw;
            string verbArgs = sp > 0 ? raw[(sp + 1)..].TrimStart() : "";
            BeginXVerbTarget(verb, verbArgs);
            return true;
        }

        // Sphere MESSAGE command: overhead text on the target object.
        // Syntax: message @<hue>[,<type>,<font>] <text>
        //   e.g.  message @0481,1,1 [Nimloth]
        //   e.g.  message @080a [Invis]
        if (upper == "MESSAGE")
        {
            string raw = args.Trim();
            ushort hue = 0x03B2;
            byte speechType = 0; // normal overhead speech
            ushort font = 3;
            string text = raw;

            if (raw.StartsWith('@'))
            {
                int spaceIdx = raw.IndexOf(' ');
                string colorSpec = spaceIdx > 0 ? raw[1..spaceIdx] : raw[1..];
                text = spaceIdx > 0 ? raw[(spaceIdx + 1)..].Trim() : "";

                var colorParts = colorSpec.Split(',');
                if (colorParts.Length >= 1)
                {
                    string huePart = colorParts[0].Trim();
                    if (huePart.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                        ushort.TryParse(huePart.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort hx))
                    {
                        hue = hx;
                    }
                    else if (ushort.TryParse(huePart, System.Globalization.NumberStyles.HexNumber, null, out ushort hHex))
                    {
                        hue = hHex;
                    }
                    else if (ushort.TryParse(huePart, out ushort hDec))
                    {
                        hue = hDec;
                    }
                }
                if (colorParts.Length >= 2 && byte.TryParse(colorParts[1], out byte t))
                    speechType = t;
                if (colorParts.Length >= 3 && ushort.TryParse(colorParts[2], out ushort f))
                    font = f;
            }

            // Sphere compatibility: MESSAGE should appear overhead on target.
            // Many script packs use type=1 or type=6 here, but UO clients can render
            // those as system/label text instead of overhead speech.
            if (speechType is 1 or 6)
                speechType = 0;

            if (text.Length > 0)
            {
                uint serial = _character.Uid.Value;
                ushort bodyId = _character.BodyId;
                Point3D origin = _character.Position;
                if (target is Character ch)
                {
                    serial = ch.Uid.Value;
                    bodyId = ch.BodyId;
                    origin = ch.Position;
                }
                else if (target is Item item)
                {
                    serial = item.Uid.Value;
                    bodyId = 0;
                    origin = item.Position;
                }
                var packet = new PacketSpeechUnicodeOut(serial, bodyId, speechType, hue, font,
                    "TRK", target.GetName(), text);
                _netState.Send(packet);
                BroadcastNearby?.Invoke(origin, 18, packet, _character.Uid.Value);
            }
            return true;
        }

        // SAYUA — overhead speech with hue/type/font/lang
        // Format: sayua <hue>,<type>,<font>,<lang> <text>
        if (upper == "SAYUA")
        {
            string raw = args.Trim();
            int firstSpace = raw.IndexOf(' ');
            ushort hue = 0x03B2;
            byte speechType = 0;
            ushort font = 3;
            string text = raw;

            if (firstSpace > 0)
            {
                string paramsPart = raw[..firstSpace];
                text = raw[(firstSpace + 1)..].TrimStart();
                string[] parms = paramsPart.Split(',');
                if (parms.Length > 0 && ushort.TryParse(parms[0], out ushort h)) hue = h;
                if (parms.Length > 1 && byte.TryParse(parms[1], out byte t)) speechType = t;
                if (parms.Length > 2 && ushort.TryParse(parms[2], out ushort f)) font = f;
            }

            if (text.Length > 0)
            {
                uint serial = _character.Uid.Value;
                ushort bodyId = _character.BodyId;
                Point3D origin = _character.Position;
                if (target is Character ch)
                {
                    serial = ch.Uid.Value;
                    bodyId = ch.BodyId;
                    origin = ch.Position;
                }
                else if (target is Item item)
                {
                    serial = item.Uid.Value;
                    bodyId = 0;
                    origin = item.Position;
                }
                var packet = new PacketSpeechUnicodeOut(serial, bodyId, speechType, hue, font,
                    "TRK", target.GetName(), text);
                _netState.Send(packet);
                BroadcastNearby?.Invoke(origin, 18, packet, _character.Uid.Value);
            }
            return true;
        }

        // INPDLG <prop> <maxLength> — open a Source-X style text-entry
        // gump on this client. The reply (0xAC) writes the user-typed
        // value into <prop> on the script verb's target object.
        // Source-X: CObjBase.cpp:OV_INPDLG → CClient::addGumpInputVal.
        if (upper == "INPDLG")
        {
            string raw = args.Trim();
            if (raw.Length == 0)
                return true;

            string propName;
            int maxLen = 1;
            int sp = raw.IndexOf(' ');
            if (sp > 0)
            {
                propName = raw[..sp].Trim();
                if (!int.TryParse(raw[(sp + 1)..].Trim(), out maxLen) || maxLen <= 0)
                    maxLen = 1;
            }
            else
            {
                propName = raw;
            }

            SendInputPromptGump(target, propName, maxLen);
            return true;
        }

        if (upper == "TRYSRC")
        {
            // Source-X compatibility: execute the provided verb line against SRC,
            // but never fail the caller when the verb is missing.
            string payload = args.Trim();
            if (payload.Length == 0)
                return true;

            if (payload[0] is '.' or '/')
                payload = payload[1..].TrimStart();
            // Proper TRYSRC semantics:
            //   TRYSRC <srcRef> <verb...>
            // where <srcRef> can be UID/REF/etc. Examples from scripts:
            //   TRYSRC <UID> DIALOGCLOSE d_spawn
            //   TRYSRC <REF2> EFFECT 0,i_fx_fireball,10,16,0,044,4
            // If the first token resolves to an object reference, execute
            // the remaining command line against that object. Otherwise,
            // keep the legacy fallback and run the whole payload as a GM
            // command line.
            int firstSpace = payload.IndexOf(' ');
            if (firstSpace > 0)
            {
                string srcRefToken = payload[..firstSpace].Trim();
                string rest = payload[(firstSpace + 1)..].Trim();
                if (rest.Length > 0 && TryFindObjectByScriptRef(srcRefToken, out var srcRefObj))
                {
                    int cmdSpace = rest.IndexOf(' ');
                    string subCmd = cmdSpace > 0 ? rest[..cmdSpace] : rest;
                    string subArg = cmdSpace > 0 ? rest[(cmdSpace + 1)..].Trim() : "";
                    if (subCmd.Length > 0)
                    {
                        if (srcRefObj.TrySetProperty(subCmd, subArg))
                            return true;
                        if (srcRefObj.TryExecuteCommand(subCmd, subArg, _client))
                            return true;
                        _ = TryExecuteScriptCommand(srcRefObj, subCmd, subArg, triggerArgs);
                    }
                    return true;
                }
            }

            if (_commands != null)
            {
                _ = _commands.TryExecute(_character, payload);
                return true;
            }

            string fallbackCmd = payload;
            int fallbackSpace = fallbackCmd.IndexOf(' ');
            string cmd2 = fallbackSpace > 0 ? fallbackCmd[..fallbackSpace] : fallbackCmd;
            string arg2 = fallbackSpace > 0 ? fallbackCmd[(fallbackSpace + 1)..].Trim() : "";
            IScriptObj srcObj = triggerArgs?.Source ?? target;
            if (cmd2.Length > 0)
            {
                if (srcObj.TrySetProperty(cmd2, arg2))
                    return true;
                if (srcObj.TryExecuteCommand(cmd2, arg2, _client))
                    return true;
                _ = TryExecuteScriptCommand(srcObj, cmd2, arg2, triggerArgs);
            }
            return true;
        }

        if (upper is "TARGETF" or "TARGETFG")
        {
            string[] parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return true;
            if (Targets.CursorActive)
                return true;
            ClearPendingTargetState();
            Targets.Function = parts[0];
            Targets.FunctionArgs = parts.Length > 1 ? parts[1].Trim() : "";
            Targets.AllowGround = upper == "TARGETFG";
            Targets.ItemUid = target is Item ti ? ti.Uid : Serial.Invalid;
            Targets.CursorActive = true;
            byte tType = (byte)(upper == "TARGETFG" ? 1 : 0);
            _netState.Send(new PacketTarget(tType, (uint)Random.Shared.Next(1, int.MaxValue)));
            return true;
        }

        if (upper is "TARGET" or "TARGETG")
        {
            if (Targets.CursorActive)
                return true;
            ClearPendingTargetState();
            Targets.AllowGround = upper == "TARGETG";
            Targets.CursorActive = true;
            byte tType = (byte)(upper == "TARGETG" ? 1 : 0);
            _netState.Send(new PacketTarget(tType, (uint)Random.Shared.Next(1, int.MaxValue)));
            return true;
        }

        if (upper == "SKILLMENU")
        {
            // Reference CV_SKILLMENU: open a [SKILLMENU name] selection menu.
            return OpenSkillMenu(args.Trim());
        }

        if (upper == "SUMMON")
        {
            // Reference CV_SUMMON: summon the given chardef as a temporary
            // summoned pet of the caster (used by sm_summon entries).
            SummonFromMenu(args.Trim());
            return true;
        }

        if (upper == "MAKEITEM")
        {
            // Reference SKILLMENU MAKEITEM: craft the itemdef through the
            // crafting engine recipe loaded from its SKILLMAKE/RESOURCES.
            MakeItemFromMenu(args.Trim());
            return true;
        }

        if (upper == "MENU")
        {
            string menuDefname = args.Trim();
            if (string.IsNullOrWhiteSpace(menuDefname))
            {
                _logger.LogWarning("[menu] MENU command with no argument");
                return true;
            }

            if (!TryFindMenuSection(menuDefname, out var menuSection))
            {
                _logger.LogWarning("[menu] Section [MENU {Defname}] not found", menuDefname);
                return true;
            }

            // Parse the MENU section:
            //   First key = title/question
            //   ON=0 text          → text-based item (modelId=0, hue=0)
            //   ON=baseid text     → item-based
            //   ON=baseid @hue, text → item-based with hue
            //   Lines after ON until next ON = script to execute

            var keys = menuSection.Keys;
            if (keys.Count == 0)
            {
                _logger.LogWarning("[menu] Empty MENU section {Defname}", menuDefname);
                return true;
            }

            string question = keys[0].Arg.Length > 0 ? $"{keys[0].Key} {keys[0].Arg}" : keys[0].Key;
            var options = new List<MenuOptionEntry>();
            MenuOptionEntry? current = null;

            for (int i = 1; i < keys.Count; i++)
            {
                var k = keys[i];
                if (k.Key.StartsWith("ON", StringComparison.OrdinalIgnoreCase) && k.Key.Length == 2)
                {
                    // Flush previous option
                    if (current != null) options.Add(current);

                    // Parse: ON=baseid text  or  ON=baseid @hue, text  or  ON=0 text
                    string onArg = k.Arg.Trim();
                    ushort modelId = 0;
                    ushort hue = 0;
                    string text = "";

                    int firstSpace = onArg.IndexOf(' ');
                    if (firstSpace < 0)
                    {
                        // ON=baseid with no text
                        _ = ushort.TryParse(onArg, System.Globalization.NumberStyles.HexNumber, null, out modelId);
                    }
                    else
                    {
                        string idPart = onArg[..firstSpace].Trim();
                        string rest = onArg[(firstSpace + 1)..].Trim();

                        if (idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || idPart.StartsWith("0", StringComparison.OrdinalIgnoreCase))
                            _ = ushort.TryParse(idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? idPart[2..] : idPart, System.Globalization.NumberStyles.HexNumber, null, out modelId);
                        else
                            _ = ushort.TryParse(idPart, out modelId);

                        // Check for @hue prefix: @hue, text  or  @hue text
                        if (rest.StartsWith('@'))
                        {
                            int comma = rest.IndexOf(',');
                            int space = rest.IndexOf(' ');
                            int sep = comma >= 0 ? comma : space;
                            if (sep > 1)
                            {
                                string huePart = rest[1..sep];
                                _ = ushort.TryParse(huePart, System.Globalization.NumberStyles.HexNumber, null, out hue);
                                text = rest[(sep + 1)..].TrimStart(' ', ',');
                            }
                            else
                            {
                                text = rest;
                            }
                        }
                        else
                        {
                            text = rest;
                        }
                    }

                    current = new MenuOptionEntry(modelId, hue, text, []);
                }
                else if (current != null)
                {
                    // Script line belonging to current ON block
                    current.Script.Add(k);
                }
            }
            if (current != null) options.Add(current);

            if (options.Count == 0)
            {
                _logger.LogWarning("[menu] MENU {Defname} has no ON entries", menuDefname);
                return true;
            }

            // Store pending state
            _pendingMenuId = (ushort)(Math.Abs(menuDefname.GetHashCode()) & 0xFFFF);
            _pendingMenuDefname = menuDefname;
            _pendingMenuOptions = options;

            // Build and send 0x7C packet
            var items = new List<MenuItemEntry>(options.Count);
            foreach (var opt in options)
                items.Add(new MenuItemEntry(opt.ModelId, opt.Hue, opt.Text));

            _netState.Send(new PacketMenuDisplay(_character.Uid.Value, _pendingMenuId, question, items));
            return true;
        }

        if (upper == "DIALOGCLOSE" || upper.StartsWith("DIALOGCLOSE ", StringComparison.Ordinal))
        {
            // Close the named open script dialog (0xBF 0x04) using the
            // server-side open-dialog registry. Bare DIALOGCLOSE (no name)
            // stays a no-op for legacy script compatibility.
            string dlgName = upper == "DIALOGCLOSE"
                ? args.Trim()
                : (cmd["DIALOGCLOSE ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}")).Trim();
            if (dlgName.Length > 0)
                CloseScriptDialog(dlgName);
            return true;
        }

        // SDIALOG = "send dialog", a Sphere alias for DIALOG used by some
        // shards' script packs. Accept both so imported scripts don't
        // need to be rewritten.
        if (upper == "DIALOG" || upper == "SDIALOG")
        {
            if (_dialogDepth >= 4) return true;
            _dialogDepth++;
            try { return HandleDialogCommand(args, target as ObjBase); } finally { _dialogDepth--; }
        }

        if (upper == "GO" && target is Character goChar)
        {
            if (TryParsePoint(args, goChar.Position, out var dst))
            {
                _world.MoveCharacter(goChar, dst);
                if (goChar == _character)
                {
                    Resync();
                    BroadcastDrawObject(_character);
                }
            }
            return true;
        }

        if (upper == "GONAME" && target is Character goNameChar)
        {
            string targetName = args.Trim();
            if (targetName.Length > 0)
            {
                var dst = _world.GetAllObjects()
                    .OfType<Character>()
                    .FirstOrDefault(c => c.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
                if (dst != null)
                {
                    _world.MoveCharacter(goNameChar, dst.Position);
                    if (goNameChar == _character)
                        Resync();
                }
            }
            return true;
        }

        if (upper == "SERV.NEWITEM")
        {
            string defName = args.Trim();
            if (_commands?.Resources == null || defName.Length == 0)
                return true;
            var rid = _commands.Resources.ResolveDefName(defName);
            if (!rid.IsValid) return true;

            var item = _world.CreateItem();
            item.BaseId = (ushort)rid.Index;
            item.Name = defName;
            Targets.ScriptNewItem = item;
            return true;
        }

        if (upper.StartsWith("NEW.", StringComparison.Ordinal))
        {
            if (Targets.ScriptNewItem == null) return true;
            string sub = cmd[4..].ToUpperInvariant();
            switch (sub)
            {
                case "EQUIP":
                    _character.Backpack ??= _world.CreateItem();
                    _character.Backpack.Name = "Backpack";
                    _character.Equip(_character.Backpack, Layer.Pack);
                    _character.Backpack.AddItem(Targets.ScriptNewItem);
                    Targets.ScriptNewItem = null;
                    return true;
                case "CONT":
                {
                    var trimmed = args.Trim();
                    if (trimmed.Length > 0 && trimmed != "-1")
                    {
                        uint cval = ObjBase.ParseHexOrDecUInt(trimmed);
                        var cont = _world.FindObject(new Serial(cval)) as Item;
                        if (cont != null) { cont.AddItem(Targets.ScriptNewItem); Targets.ScriptNewItem = null; return true; }
                    }
                    _character.Backpack ??= _world.CreateItem();
                    _character.Backpack.AddItem(Targets.ScriptNewItem);
                    Targets.ScriptNewItem = null;
                    return true;
                }
                default:
                    Targets.ScriptNewItem.TrySetProperty(sub, args);
                    return true;
            }
        }

        if (upper == "SERV.ALLCLIENTS" || upper.StartsWith("SERV.ALLCLIENTS ", StringComparison.Ordinal))
        {
            string payload = args.Trim();
            if (upper.StartsWith("SERV.ALLCLIENTS ", StringComparison.Ordinal))
                payload = cmd["SERV.ALLCLIENTS ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}");

            if (payload.StartsWith("SOUND", StringComparison.OrdinalIgnoreCase))
            {
                string[] ps = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (ps.Length >= 2 && ushort.TryParse(ps[1], out ushort snd))
                {
                    var pkt = new PacketSound(snd, _character.X, _character.Y, _character.Z);
                    BroadcastNearby?.Invoke(_character.Position, 9999, pkt, 0);
                }
            }
            else if (payload.Length > 0)
            {
                // Source-X parity: SERV.ALLCLIENTS <function> runs the function once
                // for each online player character as target, with SRC as current char.
                int firstSpace = payload.IndexOf(' ');
                string funcName = firstSpace > 0 ? payload[..firstSpace].Trim() : payload.Trim();
                string funcArgs = firstSpace > 0 ? payload[(firstSpace + 1)..].Trim() : "";

                var runner = _triggerDispatcher?.Runner;
                if (runner != null && funcName.Length > 0)
                {
                    foreach (var player in _world.GetAllObjects().OfType<Character>())
                    {
                        if (!player.IsPlayer || !player.IsOnline)
                            continue;

                        var callArgs = new ExecTriggerArgs(_character, 0, 0, funcArgs)
                        {
                            Object1 = player,
                            Object2 = _character
                        };

                        _ = runner.TryRunFunction(funcName, player, _client, callArgs, out _);
                    }
                }
                else
                {
                    string msg = payload.StartsWith("SYSMESSAGE", StringComparison.OrdinalIgnoreCase)
                        ? payload["SYSMESSAGE".Length..].Trim()
                        : payload;
                    SysMessage(msg);
                }
            }
            else
            {
                string msg = payload.StartsWith("SYSMESSAGE", StringComparison.OrdinalIgnoreCase)
                    ? payload["SYSMESSAGE".Length..].Trim()
                    : payload;
                SysMessage(msg);
            }
            return true;
        }

        if (upper == "SERV.LOG" || upper.StartsWith("SERV.LOG ", StringComparison.Ordinal))
        {
            string msg = upper == "SERV.LOG" ? args : cmd["SERV.LOG ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}");
            _logger.LogInformation("[SCRIPT] {Message}", msg.Trim());
            return true;
        }

        if (upper == "BANKSELF")
        {
            OpenBankBox();
            return true;
        }

        if (upper == "SMELT" || upper.StartsWith("SMELT ", StringComparison.Ordinal))
        {
            // Source-X CIV_SMELT: smelt this ore with SRC as the smith. Arg =
            // forge uid; without one the nearest forge in reach is used.
            if (target is not Item ore || _character == null) return true;
            string forgeArg = upper == "SMELT"
                ? args.Trim()
                : (cmd["SMELT ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}")).Trim();
            Serial forgeUid = Serial.Invalid;
            if (forgeArg.Length > 0)
            {
                string uidStr = forgeArg;
                if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) uidStr = uidStr[2..];
                else if (uidStr.StartsWith('0') && uidStr.Length > 1) uidStr = uidStr[1..];
                if (uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint fu))
                    forgeUid = new Serial(fu);
            }
            if (!forgeUid.IsValid)
            {
                foreach (var near in _world.GetItemsInRange(_character.Position, 3))
                {
                    if (near.ItemType == ItemType.Forge) { forgeUid = near.Uid; break; }
                }
            }
            _client.ItemUse.SmeltFromScript(ore, forgeUid);
            return true;
        }

        if (upper == "CARVECORPSE")
        {
            // Source-X CIV_CARVECORPSE: SRC carves this corpse (meat/hides/
            // feathers per the dead creature's body).
            if (target is Item corpse && corpse.ItemType == ItemType.Corpse && _character != null)
                _client.DeathEng?.CarveCorpse(_character, corpse);
            return true;
        }

        if (upper == "OPENTRADEWINDOW" || upper.StartsWith("OPENTRADEWINDOW ", StringComparison.Ordinal))
        {
            // Source-X CV_OPENTRADEWINDOW: open a secure trade with the char
            // named by the arg uid (or the current target object).
            string tradeArg = upper == "OPENTRADEWINDOW"
                ? args.Trim()
                : (cmd["OPENTRADEWINDOW ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}")).Trim();
            Character? partner = target as Character;
            if (tradeArg.Length > 0)
            {
                string uidStr = tradeArg;
                if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) uidStr = uidStr[2..];
                else if (uidStr.StartsWith('0') && uidStr.Length > 1) uidStr = uidStr[1..];
                if (uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint tradeUid))
                    partner = _world.FindChar(new Serial(tradeUid));
            }
            if (partner != null && partner != _character)
                _client.InitiateTrade(partner);
            return true;
        }

        if (upper == "OPENPAPERDOLL" || upper.StartsWith("OPENPAPERDOLL ", StringComparison.Ordinal))
        {
            // Source-X CV_OPENPAPERDOLL: open the paperdoll — the target
            // object's when it is a char, else the client's own.
            var pdTarget = target as Character ?? _character;
            if (pdTarget != null)
                _client.SendPaperdoll(pdTarget);
            return true;
        }

        if (upper == "CAST" || upper.StartsWith("CAST ", StringComparison.Ordinal))
        {
            // Source-X CV_CAST: begin casting a spell on this client — by
            // number or spell defname (s_ prefix optional).
            string spellArg = upper == "CAST"
                ? args.Trim()
                : (cmd["CAST ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}")).Trim();
            if (spellArg.Length == 0) return true;
            SphereNet.Core.Enums.SpellType castSpell;
            if (int.TryParse(spellArg, out int spellNum) && spellNum > 0)
                castSpell = (SphereNet.Core.Enums.SpellType)spellNum;
            else if (!Enum.TryParse(spellArg.Replace(" ", "").Replace("s_", "",
                         StringComparison.OrdinalIgnoreCase), ignoreCase: true, out castSpell))
                return true;
            _client.HandleCastSpell(castSpell, 0);
            return true;
        }

        if (upper == "WEBLINK" || upper.StartsWith("WEBLINK ", StringComparison.Ordinal))
        {
            // Source-X CV_WEBLINK — open a browser on the client (0xA5).
            string url = upper == "WEBLINK"
                ? args.Trim()
                : (cmd["WEBLINK ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}")).Trim();
            if (url.Length > 0)
                Send(new PacketWebLink(url));
            return true;
        }

        if (upper == "BUY")
        {
            // Source-X CV_BUY: open the vendor buy list. Vendor context is the
            // current dialog subject when it's an NPC vendor, otherwise the
            // nearest vendor NPC in interaction range.
            var vendor = ResolveVendorContext();
            if (vendor != null)
                OpenVendorBuy(vendor);
            return true;
        }

        if (upper == "BYE")
        {
            // Source-X CV_BYE: end the NPC interaction — close any open script
            // dialogs and drop the dialog subject so follow-up reads don't
            // resolve against the NPC anymore.
            foreach (var openDlg in Gumps.OpenScriptDialogs.Keys.ToArray())
                CloseScriptDialog(openDlg);
            Dialogs.DialogSubjectUid = Serial.Invalid;
            return true;
        }

        if (upper.StartsWith("SERV.", StringComparison.Ordinal))
        {
            // Unimplemented service verbs must not crash scripts (Sphere keeps
            // running), but the gap shouldn't be invisible either: warn once
            // per verb per server run, debug-log every hit, and tell GM-level
            // callers directly so script authors see it in-game.
            bool firstHit;
            lock (s_unknownServVerbs)
                firstHit = s_unknownServVerbs.Add(upper);
            if (firstHit)
                _logger.LogWarning("Script SERV verb not implemented: {Verb} {Args} (further uses logged at debug)", key, args);
            else
                _logger.LogDebug("Script SERV verb not implemented: {Verb} {Args}", key, args);
            if (_character != null && _character.PrivLevel >= PrivLevel.GM)
                SysMessage($"Script: unimplemented verb '{key}' ignored.");
            return true;
        }

        if (upper.StartsWith("FILE.", StringComparison.Ordinal))
        {
            if (_scriptFile == null)
            {
                _logger.LogWarning("FILE commands not enabled (OF_FileCommands not set in OptionFlags).");
                return true;
            }

            string fileVerb = upper.Length > 5 ? upper[5..] : "";
            switch (fileVerb)
            {
                case "OPEN":
                    _scriptFile.Open(args);
                    return true;
                case "CLOSE":
                    _scriptFile.Close();
                    return true;
                case "WRITE":
                    _scriptFile.Write(args);
                    return true;
                case "WRITELINE":
                    _scriptFile.WriteLine(args);
                    return true;
                case "WRITECHR":
                    if (int.TryParse(args, out int chrVal))
                        _scriptFile.WriteChr(chrVal);
                    return true;
                case "FLUSH":
                    _scriptFile.Flush();
                    return true;
                case "DELETEFILE":
                    ScriptFileHandle.DeleteFile(_scriptFile.FilePath != "" ? Path.GetDirectoryName(_scriptFile.FilePath) ?? "" : "", args);
                    return true;
                case "MODE.APPEND":
                    _scriptFile.ModeAppend = args != "0";
                    return true;
                case "MODE.CREATE":
                    _scriptFile.ModeCreate = args != "0";
                    return true;
                case "MODE.READFLAG":
                    _scriptFile.ModeRead = args != "0";
                    return true;
                case "MODE.WRITEFLAG":
                    _scriptFile.ModeWrite = args != "0";
                    return true;
                case "MODE.SETDEFAULT":
                    _scriptFile.SetModeDefault();
                    return true;
            }
            return true;
        }

        if (upper.StartsWith("DB.", StringComparison.Ordinal))
        {
            if (_scriptDb == null)
            {
                _logger.LogWarning("DB adapter is not configured for script runtime.");
                return true;
            }

            string dbVerb = upper.Length > 3 ? upper[3..] : "";
            switch (dbVerb)
            {
                case "CONNECT":
                {
                    bool ok;
                    string err;
                    string trimmed = args.Trim();
                    string[] dbArgs = trimmed.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (dbArgs.Length == 2)
                        ok = _scriptDb.Connect(dbArgs[0], dbArgs[1], out err);
                    else if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.Contains('='))
                        ok = _scriptDb.Connect(trimmed, out err);
                    else
                        ok = _scriptDb.ConnectDefault(out err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_connect_fail", err));
                    return true;
                }
                case "CLOSE":
                {
                    string name = args.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        _scriptDb.Close();
                    else if (name.Equals("*", StringComparison.Ordinal))
                        _scriptDb.CloseAll();
                    else
                        _scriptDb.Close(name);
                    return true;
                }
                case "SELECT":
                {
                    string name = args.Trim();
                    if (!_scriptDb.Select(name, out string err))
                        SysMessage(err);
                    return true;
                }
                case "QUERY":
                {
                    bool ok = _scriptDb.Query(args, out int rows, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_query_fail", err));
                    else
                        _logger.LogDebug("DB.QUERY returned {Rows} rows", rows);
                    return true;
                }
                case "EXECUTE":
                {
                    bool ok = _scriptDb.Execute(args, out int affected, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_execute_fail", err));
                    else
                        _logger.LogDebug("DB.EXECUTE affected {Rows} rows", affected);
                    return true;
                }
            }
            return true;
        }

        // Source-X MDB.* — the secondary MySQL reference object; same verb
        // surface as DB.* on its own connection.
        if (upper.StartsWith("MDB.", StringComparison.Ordinal))
        {
            if (_scriptMdb == null)
            {
                _logger.LogWarning("MDB adapter is not configured for script runtime.");
                return true;
            }

            string mdbVerb = upper.Length > 4 ? upper[4..] : "";
            switch (mdbVerb)
            {
                case "CONNECT":
                {
                    bool ok;
                    string err;
                    string trimmed = args.Trim();
                    string[] dbArgs = trimmed.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (dbArgs.Length == 2)
                        ok = _scriptMdb.Connect(dbArgs[0], dbArgs[1], out err);
                    else if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.Contains('='))
                        ok = _scriptMdb.Connect(trimmed, out err);
                    else
                        ok = _scriptMdb.ConnectDefault(out err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_connect_fail", err));
                    return true;
                }
                case "CLOSE":
                    _scriptMdb.Close();
                    return true;
                case "QUERY":
                {
                    if (!_scriptMdb.Query(args, out int rows, out string err))
                        SysMessage(ServerMessages.GetFormatted("db_query_fail", err));
                    else
                        _logger.LogDebug("MDB.QUERY returned {Rows} rows", rows);
                    return true;
                }
                case "EXECUTE":
                {
                    if (!_scriptMdb.Execute(args, out int affected, out string err))
                        SysMessage(ServerMessages.GetFormatted("db_execute_fail", err));
                    else
                        _logger.LogDebug("MDB.EXECUTE affected {Rows} rows", affected);
                    return true;
                }
            }
            return true;
        }

        if (upper.StartsWith("LDB.", StringComparison.Ordinal))
        {
            if (_scriptLdb == null)
            {
                _logger.LogWarning("SQLite (LDB) adapter is not configured for script runtime.");
                return true;
            }

            string ldbVerb = upper.Length > 4 ? upper[4..] : "";
            switch (ldbVerb)
            {
                case "CONNECT":
                {
                    string fileName = args.Trim();
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        SysMessage("LDB.CONNECT requires a filename.");
                        return true;
                    }
                    if (!_scriptLdb.ConnectFile(fileName, _scriptDatabaseRoot, out string err))
                        SysMessage(ServerMessages.GetFormatted("db_connect_fail", err));
                    return true;
                }
                case "CLOSE":
                {
                    _scriptLdb.Close();
                    return true;
                }
                case "QUERY":
                {
                    bool ok = _scriptLdb.Query(args, out int rows, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_query_fail", err));
                    else
                        _logger.LogDebug("LDB.QUERY returned {Rows} rows", rows);
                    return true;
                }
                case "EXECUTE":
                {
                    bool ok = _scriptLdb.Execute(args, out int affected, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_execute_fail", err));
                    else
                        _logger.LogDebug("LDB.EXECUTE affected {Rows} rows", affected);
                    return true;
                }
            }
            return true;
        }

        if (upper.Equals("SENDPACKET", StringComparison.Ordinal))
        {
            if (TryParseScriptPacket(args, out byte[] packet, out string err))
            {
                _netState.SendRaw(packet);
            }
            else
            {
                _logger.LogWarning("[script_packet] SENDPACKET rejected: {Error}; input='{Input}'", err, args);
            }
            return true;
        }

        return false;
    }

    /// <summary>Open a [SKILLMENU name] section as a 0x7C selection menu.
    /// Entries: first non-ON line is the title; each ON=&lt;itemdef&gt; [text]
    /// shows the itemdef's art ("&lt;name&gt;" or empty text = itemdef name);
    /// TEST=SKILL value gates the entry on the character's skills; remaining
    /// lines run as script verbs on selection.</summary>
    internal bool OpenSkillMenu(string menuName)
    {
        if (_character == null || string.IsNullOrWhiteSpace(menuName))
            return false;
        var resources = _commands?.Resources ?? DefinitionLoader.StaticResources;
        if (resources == null)
            return false;

        var rid = resources.ResolveDefName(menuName.Trim());
        if (!rid.IsValid || rid.Type != Core.Enums.ResType.SkillMenu)
            return false;
        var keys = resources.GetResource(rid)?.StoredKeys;
        if (keys == null || keys.Count == 0)
            return false;

        string title = menuName;
        bool titleSeen = false;
        var options = new List<MenuOptionEntry>();
        MenuOptionEntry? current = null;
        bool skipping = false;

        foreach (var k in keys)
        {
            bool isOn = k.Key.Equals("ON", StringComparison.OrdinalIgnoreCase);
            if (!isOn && current == null && !skipping && !titleSeen)
            {
                title = string.IsNullOrEmpty(k.Arg) ? k.Key : $"{k.Key} {k.Arg}";
                titleSeen = true;
                continue;
            }

            if (isOn)
            {
                if (current != null && !skipping)
                    options.Add(current);
                skipping = false;

                string onArg = k.Arg.Trim();
                int sp = onArg.IndexOfAny([' ', '\t']);
                string itemRef = sp < 0 ? onArg : onArg[..sp];
                string text = sp < 0 ? "" : onArg[(sp + 1)..].Trim();

                ushort modelId = 0;
                string itemName = itemRef;
                var irid = resources.ResolveDefName(itemRef);
                if (irid.IsValid)
                {
                    var idef = DefinitionLoader.GetItemDef(irid.Index);
                    modelId = idef?.DispIndex ?? (ushort)irid.Index;
                    if (!string.IsNullOrEmpty(idef?.Name))
                        itemName = idef.Name;
                }
                if (string.IsNullOrEmpty(text) || text.Equals("<name>", StringComparison.OrdinalIgnoreCase))
                    text = itemName;

                current = new MenuOptionEntry(modelId, 0, text, []);
                continue;
            }

            if (current == null)
                continue;

            if (k.Key.Equals("TEST", StringComparison.OrdinalIgnoreCase))
            {
                if (!PassesSkillMenuTest(k.Arg))
                {
                    current = null;
                    skipping = true;
                }
                continue;
            }
            if (k.Key.Equals("TESTIF", StringComparison.OrdinalIgnoreCase))
                continue; // expression tests not evaluated; entry stays visible

            current.Script.Add(k);
        }
        if (current != null && !skipping)
            options.Add(current);

        if (options.Count == 0)
        {
            SysMessage("You are not able to use any of those options.");
            return true;
        }

        _pendingMenuId = (ushort)(Math.Abs(menuName.GetHashCode()) & 0xFFFF);
        _pendingMenuDefname = menuName;
        _pendingMenuOptions = options;

        var items = new List<MenuItemEntry>(options.Count);
        foreach (var opt in options)
            items.Add(new MenuItemEntry(opt.ModelId, opt.Hue, opt.Text));
        _netState.Send(new PacketMenuDisplay(_character.Uid.Value, _pendingMenuId, title, items));
        return true;
    }

    /// <summary>TEST= line of a skill menu entry: comma-separated
    /// "SKILLNAME value" pairs (legacy fixed-point values, "75.0" = 750);
    /// non-skill tokens are ignored.</summary>
    private bool PassesSkillMenuTest(string arg)
    {
        if (_character == null || string.IsNullOrWhiteSpace(arg))
            return true;
        foreach (var part in arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int sp = part.LastIndexOf(' ');
            if (sp <= 0)
                continue;
            string name = part[..sp].Trim();
            if (!Enum.TryParse<SkillType>(name, true, out var skill))
                continue;
            int required = ValueCurve.ParseSphereNumber(part[(sp + 1)..].Trim());
            if (_character.GetSkill(skill) < required)
                return false;
        }
        return true;
    }

    private void SummonFromMenu(string defname)
    {
        if (_character == null || string.IsNullOrWhiteSpace(defname))
            return;
        var resources = _commands?.Resources ?? DefinitionLoader.StaticResources;

        var creature = _world.CreateCharacter();
        creature.IsPlayer = false;
        if (!Definitions.CharDefHelper.TryApplyDefName(creature, defname.Trim(), resources, refresh: false))
        {
            creature.Delete();
            SysMessage("Nothing answers the summons.");
            return;
        }

        creature.TryAssignOwnership(_character, _character, summoned: true, enforceFollowerCap: true);

        // Duration follows the Summon spell's curve when defined (engine
        // summon parity); fall back to two minutes.
        int durationTenths = _client.Spells?.GetSpellDef(SpellType.SummonCreature)
            ?.GetDuration(_character.GetSkill(SkillType.Magery)) ?? 0;
        if (durationTenths <= 0)
            durationTenths = 1200;
        creature.SetTag("SUMMON_DURATION", durationTenths.ToString());
        creature.SetTag("SUMMON_EXPIRE_TICK",
            (Environment.TickCount64 + durationTenths * 100L).ToString());

        var pos = new Point3D((short)(_character.X + 1), _character.Y, _character.Z, _character.MapIndex);
        _world.PlaceCharacter(creature, pos);
        _client.BroadcastCharacterAppear?.Invoke(creature);
    }

    private void MakeItemFromMenu(string defname)
    {
        if (_character == null || string.IsNullOrWhiteSpace(defname))
            return;
        var resources = _commands?.Resources ?? DefinitionLoader.StaticResources;
        if (resources == null)
            return;

        var irid = resources.ResolveDefName(defname.Trim());
        if (!irid.IsValid)
            return;
        var idef = DefinitionLoader.GetItemDef(irid.Index);
        ushort dispId = idef?.DispIndex ?? (ushort)irid.Index;

        var craftE = _client.CraftE;
        var recipe = craftE?.TryGetRecipe(dispId);
        if (craftE == null || recipe == null)
        {
            SysMessage("You don't know how to make that.");
            return;
        }
        if (!craftE.CanCraft(_character, recipe))
        {
            SysMessage("You lack the skill, materials or work site for that.");
            return;
        }

        _client.BeginPendingCraft(recipe, recipe.PrimarySkill, reopenGump: false);
    }

    private bool HandleDialogCommand(string args, ObjBase? subject = null)
    {
        if (_character == null) return false;

        string raw = args.Trim();
        string dialogId = "script_dialog";
        string closeSpec = "";
        int requestedPage = 1;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            int sep = raw.IndexOfAny([' ', ',']);
            if (sep < 0) { dialogId = raw; }
            else { dialogId = raw[..sep]; closeSpec = raw[(sep + 1)..].TrimStart(' ', ','); }
        }

        dialogId = dialogId.Trim().Trim(',', ';');
        if (string.IsNullOrWhiteSpace(dialogId)) dialogId = "script_dialog";

        if (!string.IsNullOrWhiteSpace(closeSpec))
        {
            string[] dialogTokens = closeSpec.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (dialogTokens.Length > 0 && int.TryParse(dialogTokens[0], out int parsedPage))
                requestedPage = parsedPage;
        }

        if (OpenNamedDialog(dialogId, requestedPage, subject)) return true;

        string closeFn = "";
        if (!string.IsNullOrWhiteSpace(closeSpec))
        {
            string[] tokens = closeSpec.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length > 0)
            {
                if (tokens[0].Equals("DIALOGCLOSE", StringComparison.OrdinalIgnoreCase))
                    closeFn = tokens.Length > 1 ? tokens[1] : "";
                else
                    closeFn = tokens[0];
            }
        }

        _pendingDialogCloseFunction = string.IsNullOrWhiteSpace(closeFn)
            ? $"f_dialogclose_{dialogId}"
            : closeFn.Trim().Trim(',', ';');
        _pendingDialogArgs = dialogId;
        string title = $"Dialog {dialogId}";

        uint gumpId = (uint)Math.Abs(dialogId.GetHashCode());
        var gump = new GumpBuilder(_character.Uid.Value, gumpId, 360, 180);
        gump.AddResizePic(0, 0, 5054, 360, 180);
        gump.AddText(20, 20, 0, title);
        gump.AddText(20, 60, 0, $"[{dialogId}]");
        gump.AddButton(140, 130, 4005, 4007, 1);
        SendGump(gump);
        return true;
    }

    private static bool TryParseScriptPacket(string args, out byte[] packet, out string error)
    {
        packet = [];
        error = "";
        var tokens = args.Split([' ', '\t', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            error = "empty packet";
            return false;
        }

        var bytes = new List<byte>(tokens.Length + 8);
        foreach (string raw in tokens)
        {
            if (!TryParsePacketToken(raw, bytes, out error))
                return false;
            if (bytes.Count > 256)
            {
                error = "packet exceeds 256-byte script limit";
                return false;
            }
        }

        packet = bytes.ToArray();
        return packet.Length > 0;
    }

    private static bool TryParsePacketToken(string token, List<byte> bytes, out string error)
    {
        error = "";
        string t = token.Trim();
        if (t.Length == 0)
            return true;

        int colon = t.IndexOf(':');
        string kind = "";
        if (colon > 0)
        {
            kind = t[..colon].ToUpperInvariant();
            t = t[(colon + 1)..];
        }
        else if (t.Length > 1 && (t[0] == 'B' || t[0] == 'W' || t[0] == 'D') &&
                 (char.IsDigit(t[1]) || t[1] == 'x' || t[1] == 'X'))
        {
            kind = t[0] switch
            {
                'B' => "BYTE",
                'W' => "WORD",
                'D' => "DWORD",
                _ => ""
            };
            t = t[1..];
        }

        if (!TryParsePacketNumber(t, out uint value))
        {
            error = $"invalid token '{token}'";
            return false;
        }

        switch (kind)
        {
            case "":
            case "BYTE":
                if (value > byte.MaxValue)
                {
                    error = $"byte token out of range '{token}'";
                    return false;
                }
                bytes.Add((byte)value);
                return true;
            case "WORD":
                if (value > ushort.MaxValue)
                {
                    error = $"word token out of range '{token}'";
                    return false;
                }
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)value);
                return true;
            case "DWORD":
                bytes.Add((byte)(value >> 24));
                bytes.Add((byte)(value >> 16));
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)value);
                return true;
            default:
                error = $"unknown token type '{kind}'";
                return false;
        }
    }

    private static bool TryParsePacketNumber(string token, out uint value)
    {
        value = 0;
        string t = token.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value);
        if (t.Length > 1 && t[0] == '0' && t.All(c => Uri.IsHexDigit(c)))
            return uint.TryParse(t.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out value);
        if (t.Any(c => char.IsLetter(c)))
            return uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out value);
        return uint.TryParse(t, out value);
    }

    /// <summary>Map our internal <see cref="SphereNet.Core.Enums.ResType"/> to the
    /// Source-X RES_* numeric code (as defined in [DEFNAME] sphere_defs), so that
    /// <c>&lt;RESOURCETYPE x&gt;</c> compares equal to script constants like
    /// <c>&lt;def.res_chardef&gt;</c> (=6) and <c>&lt;def.res_itemdef&gt;</c> (=14).</summary>
    private static int SourceXResValue(SphereNet.Core.Enums.ResType type) => type switch
    {
        SphereNet.Core.Enums.ResType.Account => 1,
        SphereNet.Core.Enums.ResType.Area => 3,
        SphereNet.Core.Enums.ResType.Book => 5,
        SphereNet.Core.Enums.ResType.CharDef => 6,
        SphereNet.Core.Enums.ResType.Comment => 7,
        SphereNet.Core.Enums.ResType.DefName => 8,
        SphereNet.Core.Enums.ResType.Dialog => 9,
        SphereNet.Core.Enums.ResType.Events => 10,
        SphereNet.Core.Enums.ResType.Function => 12,
        SphereNet.Core.Enums.ResType.GamePage => 13,
        SphereNet.Core.Enums.ResType.ItemDef => 14,
        SphereNet.Core.Enums.ResType.Menu => 17,
        SphereNet.Core.Enums.ResType.Names => 19,
        SphereNet.Core.Enums.ResType.NewBie => 20,
        SphereNet.Core.Enums.ResType.Obscene => 22,
        SphereNet.Core.Enums.ResType.PlevelCfg => 23,
        SphereNet.Core.Enums.ResType.RegionResource => 24,
        SphereNet.Core.Enums.ResType.RegionType => 25,
        SphereNet.Core.Enums.ResType.ResourceList => 27,
        SphereNet.Core.Enums.ResType.RoomDef => 29,
        SphereNet.Core.Enums.ResType.Scroll => 31,
        SphereNet.Core.Enums.ResType.Sector => 32,
        SphereNet.Core.Enums.ResType.SkillDef => 34,
        SphereNet.Core.Enums.ResType.SkillClass => 35,
        SphereNet.Core.Enums.ResType.SkillMenu => 36,
        SphereNet.Core.Enums.ResType.Spawn => 37,
        SphereNet.Core.Enums.ResType.Speech => 38,
        SphereNet.Core.Enums.ResType.SpellDef => 39,
        SphereNet.Core.Enums.ResType.Sphere => 40,
        SphereNet.Core.Enums.ResType.ServerConfig => 40,
        SphereNet.Core.Enums.ResType.Template => 46,
        SphereNet.Core.Enums.ResType.Tip => 48,
        SphereNet.Core.Enums.ResType.TypeDef => 49,
        SphereNet.Core.Enums.ResType.WebPage => 52,
        SphereNet.Core.Enums.ResType.WorldChar => 54,
        SphereNet.Core.Enums.ResType.WorldItem => 55,
        SphereNet.Core.Enums.ResType.WorldScript => 57,
        _ => 0, // Unknown / MultiDef / Stone — no Source-X RES_ comparison value
    };

    public bool TryResolveScriptVariable(string varName, IScriptObj target, ITriggerArgs? triggerArgs, out string value)
    {
        value = "";
        if (_character == null) return false;

        // Common Sphere runtime constants used by admin/dialog scripts.
        // GETREFTYPE — match Source-X [DEFNAME ref_types] bit layout so
        // <GetRefType> == <Def.TRef_Char> works straight from script.
        if (varName.Equals("GETREFTYPE", StringComparison.OrdinalIgnoreCase))
        {
            if (target is SphereNet.Game.Objects.Items.Item)
                value = "0" + 0x080000.ToString("X");
            else if (target is SphereNet.Game.Objects.Characters.Character)
                value = "0" + 0x040000.ToString("X");
            else
                value = "0" + 0x010000.ToString("X");
            return true;
        }

        // Generic DEF.X / DEF0.X lookup — covers everything in a [DEFNAME ...]
        // section (admin_hidehighpriv, admin_flag_1, tcolor_orange, …). Admin
        // dialogs hit these for virtually every label; without this every
        // <Def.X> fell back to unresolved = empty string, leaving the gump
        // full of gaps.
        if (varName.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("DEF0.", StringComparison.OrdinalIgnoreCase))
        {
            int dot = varName.IndexOf('.');
            string defKey = varName[(dot + 1)..];
            bool asNumeric = varName[..dot].Equals("DEF0", StringComparison.OrdinalIgnoreCase);

            if (_commands?.Resources != null)
            {
                // String defs (admin_flag_X = "Invulnerability", etc.)
                if (_commands.Resources.TryGetDefValue(defKey, out string defVal))
                {
                    value = defVal;
                    return true;
                }
                // Numeric defs (Admin_Hidehighpriv 1) — stored as ResourceId index.
                var rid = _commands.Resources.ResolveDefName(defKey);
                if (rid.IsValid)
                {
                    value = asNumeric
                        ? rid.Index.ToString()
                        : $"0{rid.Index:X}"; // <Def.X> legacy-hex form
                    return true;
                }
            }
            value = "0";
            return true; // answered as "0" rather than unresolved — matches Sphere behaviour
        }
        // RESOURCETYPE / RESOURCEINDEX (Source-X): resolve a defname to its
        // resource TYPE code and resource INDEX. Worldgen spawners compare
        // <RESOURCETYPE <entry>> against <def.res_chardef> / <def.res_itemdef>,
        // so the type MUST use the Source-X RES_* numbering (CharDef=6,
        // ItemDef=14, Spawn=37, …) — NOT our internal ResType enum order.
        if (varName.StartsWith("RESOURCETYPE ", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("RESOURCEINDEX ", StringComparison.OrdinalIgnoreCase))
        {
            int sp = varName.IndexOf(' ');
            bool wantIndex = varName[..sp].Equals("RESOURCEINDEX", StringComparison.OrdinalIgnoreCase);
            string arg = varName[(sp + 1)..].Trim();
            var rid = _commands?.Resources?.ResolveDefName(arg)
                      ?? SphereNet.Core.Types.ResourceId.Invalid;
            if (!rid.IsValid)
            {
                value = "0"; // RES_UNKNOWN / no index
                return true;
            }
            value = wantIndex
                ? rid.Index.ToString()
                : SourceXResValue(rid.Type).ToString();
            return true;
        }
        if (varName.StartsWith("ISDIALOGOPEN.", StringComparison.OrdinalIgnoreCase))
        {
            value = IsScriptDialogOpen(varName["ISDIALOGOPEN.".Length..].Trim()) ? "1" : "0";
            return true;
        }

        if (varName.StartsWith("FILE.", StringComparison.OrdinalIgnoreCase))
        {
            if (_scriptFile == null)
            {
                value = "0";
                return true;
            }

            string fileProp = varName[5..].ToUpperInvariant();
            switch (fileProp)
            {
                case "OPEN":
                {
                    // FILE.OPEN as read property — returns "1" if file is open
                    value = _scriptFile.IsOpen ? "1" : "0";
                    return true;
                }
                case "INUSE":
                    value = _scriptFile.IsOpen ? "1" : "0";
                    return true;
                case "ISEOF":
                    value = _scriptFile.IsEof ? "1" : "0";
                    return true;
                case "FILEPATH":
                    value = _scriptFile.FilePath;
                    return true;
                case "POSITION":
                    value = _scriptFile.Position.ToString();
                    return true;
                case "LENGTH":
                    value = _scriptFile.Length.ToString();
                    return true;
                case "READCHAR":
                    value = _scriptFile.ReadChar();
                    return true;
                case "READBYTE":
                    value = _scriptFile.ReadByte();
                    return true;
                case "MODE.APPEND":
                    value = _scriptFile.ModeAppend ? "1" : "0";
                    return true;
                case "MODE.CREATE":
                    value = _scriptFile.ModeCreate ? "1" : "0";
                    return true;
                case "MODE.READFLAG":
                    value = _scriptFile.ModeRead ? "1" : "0";
                    return true;
                case "MODE.WRITEFLAG":
                    value = _scriptFile.ModeWrite ? "1" : "0";
                    return true;
                default:
                    // FILE.READLINE n, FILE.SEEK pos, FILE.FILELINES path, FILE.FILEEXIST path
                    if (fileProp.StartsWith("READLINE", StringComparison.Ordinal))
                    {
                        string lineArg = fileProp.Length > 8 ? fileProp[8..].Trim() : "";
                        if (string.IsNullOrEmpty(lineArg) && varName.Length > 13)
                            lineArg = varName[13..].Trim();
                        int lineNum = 0;
                        if (!string.IsNullOrEmpty(lineArg))
                            int.TryParse(lineArg, out lineNum);
                        value = _scriptFile.ReadLine(lineNum);
                        return true;
                    }
                    if (fileProp.StartsWith("SEEK", StringComparison.Ordinal))
                    {
                        string seekArg = fileProp.Length > 4 ? fileProp[4..].Trim() : "";
                        if (string.IsNullOrEmpty(seekArg) && varName.Length > 9)
                            seekArg = varName[9..].Trim();
                        _scriptFile.Seek(seekArg);
                        value = _scriptFile.Position.ToString();
                        return true;
                    }
                    if (fileProp.StartsWith("FILELINES", StringComparison.Ordinal))
                    {
                        string flArg = fileProp.Length > 9 ? fileProp[9..].Trim() : "";
                        if (string.IsNullOrEmpty(flArg) && varName.Length > 14)
                            flArg = varName[14..].Trim();
                        value = ScriptFileHandle.GetFileLines(
                            Path.GetDirectoryName(_scriptFile.FilePath) ?? "", flArg).ToString();
                        return true;
                    }
                    if (fileProp.StartsWith("FILEEXIST", StringComparison.Ordinal))
                    {
                        string feArg = fileProp.Length > 9 ? fileProp[9..].Trim() : "";
                        if (string.IsNullOrEmpty(feArg) && varName.Length > 14)
                            feArg = varName[14..].Trim();
                        value = ScriptFileHandle.FileExists(
                            Path.GetDirectoryName(_scriptFile.FilePath) ?? "", feArg) ? "1" : "0";
                        return true;
                    }
                    break;
            }
            value = "0";
            return true;
        }

        if (varName.Equals("DB.CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            value = _scriptDb?.IsConnected == true ? "1" : "0";
            return true;
        }
        if (varName.StartsWith("DB.CONNECTED.", StringComparison.OrdinalIgnoreCase) && _scriptDb != null)
        {
            string connName = varName[13..];
            value = _scriptDb.IsConnected_Named(connName) ? "1" : "0";
            return true;
        }
        if (varName.Equals("DB.ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            value = _scriptDb?.ActiveSessionName ?? "";
            return true;
        }
        if (varName.StartsWith("DB.ESCAPEDATA.", StringComparison.OrdinalIgnoreCase) && _scriptDb != null)
        {
            string rawData = varName[14..];
            value = _scriptDb.EscapeData(rawData);
            return true;
        }
        if (_scriptDb != null && _scriptDb.TryResolveRowValue(varName, out string dbVal))
        {
            value = dbVal;
            return true;
        }
        if (varName.Equals("MDB.CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            value = _scriptMdb?.IsConnected == true ? "1" : "0";
            return true;
        }
        if (varName.StartsWith("MDB.ESCAPEDATA.", StringComparison.OrdinalIgnoreCase) && _scriptMdb != null)
        {
            value = _scriptMdb.EscapeData(varName[15..]);
            return true;
        }
        if (_scriptMdb != null && varName.StartsWith("MDB.ROW.", StringComparison.OrdinalIgnoreCase))
        {
            string mdbKey = "db.row." + varName[8..];
            if (_scriptMdb.TryResolveRowValue(mdbKey, out string mdbVal))
            {
                value = mdbVal;
                return true;
            }
        }
        if (varName.Equals("LDB.CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            value = _scriptLdb?.IsConnected == true ? "1" : "0";
            return true;
        }
        if (_scriptLdb != null && varName.StartsWith("LDB.ROW.", StringComparison.OrdinalIgnoreCase))
        {
            string ldbKey = "db.row." + varName[8..];
            if (_scriptLdb.TryResolveRowValue(ldbKey, out string ldbVal))
            {
                value = ldbVal;
                return true;
            }
        }
        if (varName.StartsWith("ACCOUNT.", StringComparison.OrdinalIgnoreCase))
        {
            if (_account != null && _account.TryGetProperty(varName["ACCOUNT.".Length..], out string acctVal))
            {
                value = acctVal;
                return true;
            }
            return false;
        }

        if (varName.Equals("TARGP", StringComparison.OrdinalIgnoreCase))
        {
            var p = Targets.LastScriptPoint ?? _character.Position;
            value = $"{p.X},{p.Y},{p.Z},{p.Map}";
            return true;
        }

        if (varName.StartsWith("CTAG0.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("CTAG.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("DCTAG0.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("DCTAG.", StringComparison.OrdinalIgnoreCase))
        {
            int dot = varName.IndexOf('.');
            if (dot > 0)
            {
                string tagName = varName[(dot + 1)..].Trim().Trim(',', ';');
                string? tagVal = _character.CTags.Get(tagName);
                if (tagVal != null)
                {
                    value = tagVal;
                    return true;
                }
            }
            return false;
        }

        int objDot = varName.IndexOf('.');
        if (objDot > 0)
        {
            string root = varName[..objDot].Trim();
            string prop = varName[(objDot + 1)..].Trim();
            if (_character.TryGetProperty($"TAG.{root}", out string objRef) && TryFindObjectByScriptRef(objRef, out var scopedObj))
            {
                if (scopedObj.TryGetProperty(prop, out string scopedVal))
                {
                    value = scopedVal;
                    return true;
                }
            }
        }

        if (varName.StartsWith("ARGO.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("ACT.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("LINK.", StringComparison.OrdinalIgnoreCase))
        {
            IScriptObj? obj = null;
            int dot = varName.IndexOf('.');
            string root = dot > 0 ? varName[..dot].ToUpperInvariant() : varName.ToUpperInvariant();
            string sub = dot > 0 ? varName[(dot + 1)..] : "";
            if (root == "ARGO") obj = triggerArgs?.Object1;
            else if (root is "ACT" or "LINK") obj = triggerArgs?.Object2;

            if (obj == null) return false;

            if (sub.StartsWith("ACCOUNT.", StringComparison.OrdinalIgnoreCase) && obj is Character chAcct)
            {
                var acct = Character.ResolveAccountForChar?.Invoke(chAcct.Uid);
                if (acct != null && acct.TryGetProperty(sub["ACCOUNT.".Length..], out string acctVal))
                {
                    value = acctVal;
                    return true;
                }
                return false;
            }
            if (obj.TryGetProperty(sub, out string propVal))
            {
                value = propVal;
                return true;
            }
        }

        // Resolve object-scoped locals like OBJ.ISPLAYER where OBJ contains a UID string.
        int localDot = varName.IndexOf('.');
        if (localDot > 0)
        {
            string localName = varName[..localDot];
            if (triggerArgs != null && target.TryGetProperty($"TAG.{localName}", out string tagVal) && TryFindObjectByScriptRef(tagVal, out var refObj))
            {
                if (refObj.TryGetProperty(varName[(localDot + 1)..], out string scopedVal))
                {
                    value = scopedVal;
                    return true;
                }
            }
        }

        // Bare defname constants for general script execution paths
        // (outside dialog render), e.g. <statf_insubstantial>.
        if (_commands?.Resources != null && IsPlainDefToken(varName))
        {
            var rid = _commands.Resources.ResolveDefName(varName);
            if (rid.IsValid)
            {
                value = rid.Index.ToString();
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<IScriptObj> QueryScriptObjects(string query, IScriptObj target, string args, ITriggerArgs? triggerArgs)
    {
        if (_character == null) return Array.Empty<IScriptObj>();

        if (query.Equals("FORPLAYERS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            return _world.GetAllObjects()
                .OfType<Character>()
                .Where(c => c.IsPlayer && c.MapIndex == _character.MapIndex &&
                            c.Position.GetDistanceTo(_character.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        if (query.Equals("FORINSTANCES", StringComparison.OrdinalIgnoreCase))
        {
            string def = args.Trim();
            if (def.Length == 0) return Array.Empty<IScriptObj>();

            int? itemBase = null;
            int? charBase = null;
            var rid = _commands?.Resources?.ResolveDefName(def) ?? ResourceId.Invalid;
            if (rid.IsValid)
            {
                if (rid.Type == Core.Enums.ResType.ItemDef) itemBase = rid.Index;
                else if (rid.Type == Core.Enums.ResType.CharDef) charBase = rid.Index;
            }
            else if (int.TryParse(def.Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out int parsed))
            {
                itemBase = parsed;
                charBase = parsed;
            }

            return _world.GetAllObjects()
                .Where(o =>
                    (o is Item it && itemBase.HasValue && it.BaseId == (ushort)itemBase.Value) ||
                    (o is Character ch && charBase.HasValue && ch.BaseId == (ushort)charBase.Value))
                .Cast<IScriptObj>()
                .ToList();
        }

        // FORCHARS — all characters (players + NPCs) within radius
        if (query.Equals("FORCHARS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            return _world.GetAllObjects()
                .OfType<Character>()
                .Where(c => !c.IsDeleted && c.MapIndex == map &&
                            center.GetDistanceTo(c.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        // FORCLIENTS — only online player characters within radius
        if (query.Equals("FORCLIENTS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            return _world.GetAllObjects()
                .OfType<Character>()
                .Where(c => c.IsPlayer && c.IsOnline && !c.IsDeleted &&
                            c.MapIndex == map && center.GetDistanceTo(c.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        // FORITEMS — all ground items within radius
        if (query.Equals("FORITEMS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            return _world.GetAllObjects()
                .OfType<Item>()
                .Where(it => !it.IsDeleted && it.IsOnGround &&
                             it.MapIndex == map && center.GetDistanceTo(it.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        // FOROBJS — all characters + items within radius
        if (query.Equals("FOROBJS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            var result = new List<IScriptObj>();
            foreach (var obj in _world.GetAllObjects())
            {
                if (obj.IsDeleted) continue;
                if (obj.MapIndex != map) continue;
                if (center.GetDistanceTo(obj.Position) > range) continue;
                if (obj is Item it && !it.IsOnGround) continue;
                result.Add(obj);
            }
            return result;
        }

        // FORCONT — all items inside a container (args: "uid [depth]")
        if (query.Equals("FORCONT", StringComparison.OrdinalIgnoreCase))
        {
            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Array.Empty<IScriptObj>();
            if (!TryFindObjectByScriptRef(parts[0], out var contObj) || contObj is not Item container)
                return Array.Empty<IScriptObj>();
            int depth = parts.Length > 1 && int.TryParse(parts[1], out int d) ? d : 0;
            var result = new List<IScriptObj>();
            CollectContainerItems(container, depth, result);
            return result;
        }

        // FORCONTID — items in current target's backpack matching a BASEID (args: "baseid [depth]")
        if (query.Equals("FORCONTID", StringComparison.OrdinalIgnoreCase))
        {
            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Array.Empty<IScriptObj>();
            string defName = parts[0];
            int depth = parts.Length > 1 && int.TryParse(parts[1], out int d) ? d : 0;
            ushort? targetBaseId = ResolveBaseId(defName);
            if (!targetBaseId.HasValue) return Array.Empty<IScriptObj>();

            // Iterate the target character's backpack, or the target item as container
            Item? container = target is Character ch ? ch.Backpack : target as Item;
            if (container == null) return Array.Empty<IScriptObj>();
            var result = new List<IScriptObj>();
            CollectContainerItems(container, depth, result, baseIdFilter: targetBaseId.Value);
            return result;
        }

        // FORCONTTYPE — items in current target's backpack matching a TYPE (args: "type [depth]")
        if (query.Equals("FORCONTTYPE", StringComparison.OrdinalIgnoreCase))
        {
            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Array.Empty<IScriptObj>();
            string typeName = parts[0];
            int depth = parts.Length > 1 && int.TryParse(parts[1], out int d) ? d : 0;
            int? typeFilter = ResolveItemType(typeName);
            if (!typeFilter.HasValue) return Array.Empty<IScriptObj>();

            Item? container = target is Character ch ? ch.Backpack : target as Item;
            if (container == null) return Array.Empty<IScriptObj>();
            var result = new List<IScriptObj>();
            CollectContainerItems(container, depth, result, typeFilter: typeFilter.Value);
            return result;
        }

        // FORCHARLAYER — items on a specific equipment layer of the target character
        if (query.Equals("FORCHARLAYER", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(args.Trim(), out int layerNum)) return Array.Empty<IScriptObj>();
            Character? ch = target as Character ?? _character;
            var item = ch.GetEquippedItem((Layer)layerNum);
            if (item == null) return Array.Empty<IScriptObj>();
            // Layer 30 (Special) can contain multiple memory items; single item for other layers
            return new List<IScriptObj> { item };
        }

        if (query.Equals("FORCHARMEMORYTYPE", StringComparison.OrdinalIgnoreCase))
        {
            Character? ch = target as Character ?? _character;
            if (ch == null)
                return Array.Empty<IScriptObj>();
            return ch.GetMemoryEntriesByType(args, _world);
        }

        return Array.Empty<IScriptObj>();
    }

    private void CollectContainerItems(Item container, int depth, List<IScriptObj> result,
        ushort? baseIdFilter = null, int? typeFilter = null)
    {
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            bool matches = true;
            if (baseIdFilter.HasValue && item.BaseId != baseIdFilter.Value) matches = false;
            if (typeFilter.HasValue && (int)item.ItemType != typeFilter.Value) matches = false;
            if (matches) result.Add(item);
            if (depth > 0 && item.ContentCount > 0)
                CollectContainerItems(item, depth - 1, result, baseIdFilter, typeFilter);
        }
    }

    private ushort? ResolveBaseId(string defName)
    {
        var rid = _commands?.Resources?.ResolveDefName(defName) ?? ResourceId.Invalid;
        if (rid.IsValid) return (ushort)rid.Index;
        if (ushort.TryParse(defName.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber, null, out ushort v))
            return v;
        return null;
    }

    private int? ResolveItemType(string typeName)
    {
        // Try as enum name (e.g. "t_spellbook" → strip "t_" prefix, parse as ItemType)
        string name = typeName.TrimStart();
        if (name.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
            name = name[2..];
        if (Enum.TryParse<Core.Enums.ItemType>(name, ignoreCase: true, out var itemType))
            return (int)itemType;
        // Try as numeric
        if (int.TryParse(typeName, out int num))
            return num;
        return null;
    }

    private bool TryFindObjectByScriptRef(string value, out IScriptObj obj)
    {
        obj = null!;
        string v = value.Trim();
        if (v.StartsWith("0", StringComparison.OrdinalIgnoreCase))
            v = v[1..];
        if (!uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return false;
        var found = _world.FindObject(new Serial(uid));
        if (found == null) return false;
        obj = found;
        return true;
    }

    private static bool TryParsePoint(string args, Point3D current, out Point3D point)
    {
        point = current;
        var parts = args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (!short.TryParse(parts[0], out short x) || !short.TryParse(parts[1], out short y))
            return false;
        sbyte z = parts.Length > 2 && sbyte.TryParse(parts[2], out sbyte tz) ? tz : current.Z;
        byte map = parts.Length > 3 && byte.TryParse(parts[3], out byte tm) ? tm : current.Map;
        point = new Point3D(x, y, z, map);
        return true;
    }
}
