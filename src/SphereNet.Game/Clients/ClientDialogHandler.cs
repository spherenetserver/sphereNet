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
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;
using SphereNet.Game.Messages;
using ScriptDbAdapter = SphereNet.Scripting.Execution.ScriptDbAdapter;

namespace SphereNet.Game.Clients;

/// <summary>
/// Dialog handler extracted from the GameClient.Dialogs partial
/// (decomposition phase 3 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Script [DIALOG] section rendering (layout expansion, coordinate cursors,
/// button handlers), the named-dialog dispatcher with native fallbacks, the
/// help menu and the INPDLG prompt state. Method bodies moved verbatim; the
/// private context shims below enumerate exactly what this handler needs
/// from GameClient.
/// </summary>
public sealed class ClientDialogHandler
{
    /// <summary>The gump LAYOUT vocabulary. Internal rather than private because a
    /// pack's [FUNCTION] may emit dialog rows - printing a table of houses, say -
    /// so a sweep over function bodies has to be able to tell a layout command from
    /// a verb, and the engine is where that list actually lives.</summary>
    internal static readonly HashSet<string> DialogRenderCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "BUTTON", "BUTTONTILEART", "CHECKBOX", "CHECKERTRANS", "CROPPEDTEXT",
        "DCROPPEDTEXT", "DHTMLGUMP", "DORIGIN", "DTEXT", "DTEXTENTRY",
        "DTEXTENTRYLIMITED", "GROUP", "GUMPPIC", "GUMPPICTILED",
        "HTMLGUMP", "ITEMPROPERTY", "NOCLOSE", "NODISPOSE", "NOMOVE", "PAGE",
        "PICINPIC", "RADIO", "RESIZEPIC", "TEXT", "TEXTENTRY",
        "TEXTENTRYLIMITED", "TILEPIC", "TILEPICHUE", "TOOLTIP", "XMFHTMLGUMP",
        "XMFHTMLGUMPCOLOR", "XMFHTMLTOK"
    };

    /// <summary>
    /// Source-X executes a DIALOG layout as a normal script with CDialogDef as
    /// the target. This adapter captures gump verbs while delegating ordinary
    /// reads, writes and verbs to the dialog subject. It lets the shared script
    /// interpreter handle CALL/functions/RETURN/SERV/DB/SRC and control flow.
    /// </summary>
    private sealed class DialogRenderTarget : IScriptObj
    {
        private readonly IScriptObj _subject;
        private readonly List<ScriptKey> _output;

        public DialogRenderTarget(IScriptObj subject, List<ScriptKey> output)
        {
            _subject = subject;
            _output = output;
        }

        public string GetName() => _subject.GetName();

        public bool PreferScriptFunction(string key) => !DialogRenderCommands.Contains(key);

        public bool TryGetProperty(string key, out string value)
        {
            string lookup = key.StartsWith("I.", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;
            return _subject.TryGetProperty(lookup, out value);
        }

        public bool TryExecuteCommand(string key, string args, ITextConsole source)
        {
            if (DialogRenderCommands.Contains(key))
            {
                _output.Add(new ScriptKey(key, args));
                return true;
            }
            return _subject.TryExecuteCommand(key, args, source);
        }

        public bool TrySetProperty(string key, string value) => _subject.TrySetProperty(key, value);

        public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args) =>
            _subject.OnTrigger(triggerType, source, args);
    }

    private readonly IClientContext _client;

    internal ClientDialogHandler(IClientContext client)
    {
        _client = client;
        RegisterNativeDialogFallbacks();
    }

    // --- context shims (the GameClient surface this handler depends on) ---
    private Character? _character => _client.Character;
    private GameWorld _world => _client.World;
    private TriggerDispatcher? _triggerDispatcher => _client.Triggers;
    private CommandHandler? _commands => _client.Cmds;
    private Mounts.MountEngine? _mountEngine => _client.MountE;
    private ILogger _logger => _client.Log;
    private ClientGumpRegistry Gumps => _client.Gumps;
    private void SysMessage(string text) => _client.SysMessage(text);
    private void Send(SphereNet.Network.Packets.PacketWriter packet) => _client.Send(packet);
    private void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null) => _client.SendGump(gump, callback);
    private void Resync() => _client.Resync();
    private void BroadcastDrawObject(Character ch) => _client.BroadcastDrawObject(ch);
    private bool TryResolveScriptVariable(string varName, IScriptObj target, ITriggerArgs? triggerArgs, out string value) => _client.TryResolveScriptVariable(varName, target, triggerArgs, out value);

    // Source-X dialog subject (CLIMODE_DIALOG pObj). When set, bare
    // property names inside the active script dialog resolve on this
    // object instead of the GM. Used by d_charprop1 / d_itemprop1 so
    // <BODY> / <STR> etc. reflect the inspected target. Cleared after
    // render; callbacks that act on the target stash its UID locally.
    private Serial _dialogSubjectUid = Serial.Invalid;

    /// <summary>The dialog's subject as an OBJECT. A spell effect a character wears is
    /// not a world object (it has no uid the world can look up), so a dialog opened
    /// on it - .edit on a spell effect - has to hold the object itself; looking it up
    /// by uid found nothing and the dialog read, and wrote, the GM instead.</summary>
    private ObjBase? _dialogSubjectObj;

    private ObjBase? ResolveDialogSubject()
    {
        if (_dialogSubjectObj != null && !_dialogSubjectObj.IsDeleted)
            return _dialogSubjectObj;
        return _dialogSubjectUid.IsValid ? _world.FindObject(_dialogSubjectUid) : null;
    }
    /// <summary>Cross-partial access to the dialog subject (ScriptConsole
    /// reads/clears it around script-driven dialog flows).</summary>
    internal Serial DialogSubjectUid
    {
        get => _dialogSubjectUid;
        set => _dialogSubjectUid = value;
    }
    /// <summary>Generic script-first → native fallback registry. When a
    /// named dialog (<c>d_xxx</c>) is requested via <c>SDIALOG</c> or a
    /// help/inspect entry point, the host first tries the script
    /// <c>[DIALOG d_xxx]</c> section through <see cref="TryShowScriptDialog(string, int)"/>;
    /// only when no script section is found does the registered native
    /// fallback render. New native gumps should plug in here instead of
    /// hard-coding their own render path.</summary>
    private readonly Dictionary<string, Action<int>> _nativeDialogFallbacks =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Pending Source-X <c>INPDLG</c> prompt state. Keyed by the
    /// <c>(targetSerial, context)</c> pair we encoded into the outgoing
    /// 0xAB packet; the matching 0xAC reply restores the property name
    /// to write the user-typed value into.
    /// </summary>
    /// The object is kept too: a memory or spell effect worn on a character has no
    /// world uid, so looking the reply's serial up again could never find it.
    internal readonly Dictionary<(uint Serial, ushort Context), (string Prop, IScriptObj Target)> PendingInputDlg = new();
    /// <summary>Monotonic counter for fresh INPDLG <c>context</c> ids
    /// (Source-X uses CLIMODE constants, but we just need uniqueness per
    /// open prompt).</summary>
    internal ushort NextInputDlgContext = 0x1000;

    /// <summary>Wire built-in <c>d_xxx</c> native gump fallbacks. Each entry
    /// is only used when the script-side <c>[DIALOG d_xxx]</c> section is
    /// missing — see <see cref="OpenNamedDialog"/>.</summary>
    private void RegisterNativeDialogFallbacks()
    {
        _nativeDialogFallbacks["d_helppage"] = page => ShowHelpPageDialog(page <= 0 ? 1 : page);
    }

    /// <summary>Generic script-first dialog dispatcher. Tries the script
    /// <c>[DIALOG dialogId]</c> section (Source-X parity), falling back to
    /// any registered native gump. Returns true when something was
    /// rendered. <paramref name="subject"/> binds the gump's CLIMODE_DIALOG
    /// pObj for property reads (used by edit / inspect).</summary>
    public bool OpenNamedDialog(string dialogId, int requestedPage = 0, ObjBase? subject = null, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(dialogId))
            return false;

        if (TryFindDialogSections(dialogId, out _))
            return TryShowScriptDialog(dialogId, requestedPage, subject, arguments);

        if (_nativeDialogFallbacks.TryGetValue(dialogId, out var nativeOpen))
        {
            nativeOpen(requestedPage);
            return true;
        }

        return false;
    }

    public bool IsScriptDialogOpen(string dialogId) =>
        Gumps.OpenScriptDialogs.ContainsKey(dialogId);

    /// <summary>Force-close an open script dialog (0xBF 0x04) and run its
    /// ON=&lt;button&gt; handler, as upstream does. Returns false when no such
    /// dialog is open.
    ///
    /// Closing does not stop at the packet: CClient::Dialog_Close feeds a gump
    /// response carrying the given button back through the receive path
    /// (CClientDialog.cpp:200-222), because a client from 4.0.4a on does not echo
    /// one of its own. That synthetic answer is what runs the dialog's ON=0
    /// block, and a script pack relies on it - d_admin's ON=0 is
    /// `CLEARCTAGS Dialog.Admin`, and `[FUNCTION admin]` opens with
    /// `DIALOGCLOSE d_admin` precisely to get that clear. Dropping the callback
    /// instead meant the tags were never cleared: every `.admin` appended its
    /// client list to the last one, so the same player turned up on page after
    /// page and the page count grew until the gump stopped coming up at all.</summary>
    public bool CloseScriptDialog(string dialogId, int buttonId = 0)
    {
        if (!Gumps.OpenScriptDialogs.TryGetValue(dialogId, out uint gumpId))
            return false;
        Send(new PacketCloseGump(gumpId, unchecked((uint)buttonId)));

        // Before 4.0.4a the client sends its own response to the close packet.
        uint version = _client.NetState.ClientVersionNumber;
        if (version != 0 && version < 40_004_000) return true;

        // The response path owns the rest of the teardown (it removes the gump
        // from the active set and consumes the callback), so hand over rather
        // than clearing first - HandleGumpResponse rejects a gump it cannot find.
        if (_character != null && Gumps.ActiveGumps.Contains(gumpId))
        {
            if (!Gumps.HasScript(gumpId)) Gumps.OpenScriptDialogs.Remove(dialogId);
            _client.HandleGumpResponse(Gumps.ScriptSerial(gumpId, _character.Uid.Value), gumpId, (uint)buttonId, [], []);
            return true;
        }

        Gumps.Callbacks.Remove(gumpId);
        Gumps.ActiveGumps.Remove(gumpId);
        Gumps.OpenScriptDialogs.Remove(dialogId);
        return true;
    }

    private void ShowHelpPageDialog(int requestedPage)
    {
        if (_character == null)
            return;

        int page = Math.Clamp(requestedPage, 1, 4);
        _character.SetTag("help_type", page.ToString());

        string[] menu = ["Genel", "Yardim", "Stuck", "Istatistik"];

        var gump = new GumpBuilder(_character.Uid.Value, (uint)Math.Abs("d_helppage".GetHashCode()), 500, 360);
        gump.AddResizePic(0, 0, 5054, 500, 360)
            .AddResizePic(15, 15, 2620, 130, 300)
            .AddResizePic(155, 15, 2620, 330, 300)
            .AddText(30, 25, 0x0481, "Help")
            .AddText(175, 25, 0x0481, "Bilgi");

        for (int i = 0; i < menu.Length; i++)
        {
            int idx = i + 1;
            int y = 65 + (i * 42);
            gump.AddButton(28, y, 4005, 4007, idx)
                .AddText(62, y + 2, idx == page ? (ushort)0x0021 : (ushort)0x0481, menu[i]);
        }

        string pageTitle = menu[page - 1];
        gump.AddText(175, 60, 0x0481, pageTitle);

        switch (page)
        {
            case 1:
                gump.AddHtmlGump(175, 90, 280, 160,
                    "Genel yardim menusu.<br><br>Detayli sistemler daha sonra script tarafindan doldurulabilir.",
                    true, true);
                break;
            case 2:
                gump.AddHtmlGump(175, 90, 280, 120,
                    "Sorunun varsa staff'a page atabilir veya mevcut page durumunu kontrol edebilirsin.",
                    true, true)
                    .AddButton(175, 235, 4005, 4007, 21)
                    .AddText(210, 237, 0x0481, "Page")
                    .AddButton(300, 235, 4005, 4007, 22)
                    .AddText(335, 237, 0x0481, "Page List");
                break;
            case 3:
                gump.AddHtmlGump(175, 90, 280, 120,
                    "Karakterin takildiysa uygun bir guvenli nokta secerek cikabilirsin.",
                    true, true)
                    .AddButton(175, 235, 4005, 4007, 30)
                    .AddText(210, 237, 0x0481, "Town")
                    .AddButton(300, 235, 4005, 4007, 31)
                    .AddText(335, 237, 0x0481, "Inn");
                break;
            case 4:
            {
                var stats = _world.GetStats();
                gump.AddHtmlGump(175, 90, 280, 160,
                    $"Online Oyuncu: {_world.GetAllObjects().OfType<Character>().Count(c => c.IsPlayer && c.IsOnline)}<br>" +
                    $"Yaratik Sayisi: {stats.Chars}<br>" +
                    $"Esya Sayisi: {stats.Items}<br>" +
                    $"Sektor Sayisi: {stats.Sectors}",
                    true, true);
                break;
            }
        }

        gump.AddButton(455, 22, 4017, 4019, 0);

        SendGump(gump, (buttonId, _, _) =>
        {
            if (_character == null)
                return;
            if (buttonId == 0)
                return;

            if (buttonId is >= 1 and <= 4)
            {
                ShowHelpPageDialog((int)buttonId);
                return;
            }

            if (buttonId is >= 30 and <= 31)
            {
                HandleHelpStuck(toInn: buttonId == 31);
                return;
            }

            if (buttonId == 21)
            {
                ShowHelpPageEntryDialog();
                return;
            }

            if (buttonId == 22)
            {
                ShowHelpPageListDialog();
            }
        });
    }

    /// <summary>Help-menu "I'm stuck": teleport to a safe town spot. Denied
    /// while jailed (would be a jail escape) or mid-fight (combat escape).</summary>
    private void HandleHelpStuck(bool toInn)
    {
        if (_character == null)
            return;
        if (_character.IsJailed)
        {
            SysMessage(ServerMessages.Get("msg_stuck_denied"));
            return;
        }
        if (_character.FightTarget.IsValid)
        {
            SysMessage(ServerMessages.Get("msg_stuck_denied"));
            return;
        }

        // Britain: bank plaza for "Town", the inn block for "Inn" (map 0).
        short x = toInn ? (short)1475 : (short)1495;
        short y = toInn ? (short)1612 : (short)1629;
        // Terrain query seeds the reference; the SEAT goes through the shared
        // standing resolver (audit design — character Z never from GetEffectiveZ).
        sbyte z = _world.MapData?.GetEffectiveZ(0, x, y) ?? (sbyte)10;
        var stuckStand = _world.Standing.ResolveStandingSurface(_character, 0, x, y, z,
            SphereNet.Game.Movement.WalkCheck.StandingPolicy.Settle);
        if (stuckStand.Found) z = stuckStand.Z;
        _world.MoveCharacter(_character, new Point3D(x, y, z, 0));
        Resync();
        SysMessage(ServerMessages.Get("msg_stuck_teleported"));
    }

    /// <summary>Help-menu "Page": prompt for a message, then queue it the way
    /// .PAGE and GMPAGE ADD do (CommandHandler.SubmitPage).</summary>
    private void ShowHelpPageEntryDialog()
    {
        if (_character == null)
            return;
        var gump = new GumpBuilder(_character.Uid.Value, _character.Uid.Value, 380, 180);
        gump.AddResizePic(0, 0, 5054, 380, 180);
        gump.AddText(20, 15, 0x0481, "Describe your problem for the staff:");
        gump.AddResizePic(15, 45, 3000, 350, 80);
        gump.AddTextEntryLimited(20, 50, 340, 70, 0, 1, "", 200);
        gump.AddButton(150, 140, 4023, 4025, 1); // OK
        gump.AddButton(200, 140, 4017, 4019, 0); // Cancel
        SendGump(gump, (buttonId, _, textEntries) =>
        {
            if (_character == null || buttonId != 1 || _commands == null)
                return;
            string text = textEntries.FirstOrDefault(t => t.Item1 == 1).Item2?.Trim() ?? "";
            if (text.Length == 0)
                return;
            // Queue it directly (Event_PromptResp_GMPage). Re-running the text as a
            // PAGE command let a pack's own [FUNCTION Page] - a queue viewer in the
            // reference distribution - take the call, so the page never queued.
            _commands.SubmitPage(_character, text);
        });
    }

    /// <summary>Help-menu "Page List": staff see every recent page, players
    /// only their own submissions.</summary>
    private void ShowHelpPageListDialog()
    {
        if (_character == null || _commands == null)
            return;
        bool staff = _character.PrivLevel >= PrivLevel.Counsel;
        var visible = _commands.RecentPages
            .Where(p => staff || p.From == _character.Uid)
            .TakeLast(10)
            .ToList();
        if (visible.Count == 0)
        {
            SysMessage(ServerMessages.Get("msg_pagelist_empty"));
            return;
        }
        var gump = new GumpBuilder(_character.Uid.Value, _character.Uid.Value, 420, 70 + visible.Count * 22);
        gump.AddResizePic(0, 0, 5054, 420, 70 + visible.Count * 22);
        gump.AddText(20, 12, 0x0481, staff ? "Recent pages" : "Your recent pages");
        int yy = 40;
        foreach (var p in visible)
        {
            string line = staff
                ? $"{p.Utc:HH:mm} {p.FromName}: {p.Message}"
                : $"{p.Utc:HH:mm} {p.Message}";
            gump.AddText(20, yy, 0, line.Length > 56 ? line[..56] : line);
            yy += 22;
        }
        gump.AddButton(380, 12, 4017, 4019, 0);
        SendGump(gump, (_, _, _) => { });
    }

    /// <summary>Open a script-defined dialog ([DIALOG &lt;name&gt;] sections)
    /// on this client. Returns false when the dialog name cannot be
    /// resolved — caller logs or sysmessages accordingly. Public so
    /// admin commands (".dialog") and script-command handlers share the
    /// same code path.</summary>
    public bool TryShowScriptDialog(string dialogId, int requestedPage)
        => TryShowScriptDialog(dialogId, requestedPage, subject: null);

    /// <summary>Open a script DIALOG section. When <paramref name="subject"/>
    /// is non-null, bare property reads inside the dialog resolve against
    /// that object first (Source-X CLIMODE_DIALOG pObj semantics) — needed
    /// by d_charprop1 / d_itemprop1 where the gump is bound to an inspected
    /// target instead of the GM.</summary>
    public bool TryShowScriptDialog(string dialogId, int requestedPage, ObjBase? subject, string? arguments = null)
    {
        if (_character == null || _commands?.Resources == null)
            return false;

        if (!TryFindDialogSections(dialogId, out var layoutSection))
            return false;

        var textLines = _commands.Resources.GetDialogTextLines(dialogId);

        var prevSubject = _dialogSubjectUid;
        var prevSubjectObj = _dialogSubjectObj;
        var parser = _triggerDispatcher?.Runner?.Interpreter.Expressions;
        var previousResponseResolver = parser?.DialogArgResolver;
        _dialogSubjectUid = subject?.Uid ?? Serial.Invalid;
        _dialogSubjectObj = subject;
        // A dialog opened by a button has fresh setup args. The caller's
        // response accessor must not override ARGN or leak its input fields.
        if (parser != null) parser.DialogArgResolver = null;
        try
        {
            return RenderScriptDialog(dialogId, requestedPage, layoutSection, subject?.Uid ?? Serial.Invalid, textLines, arguments);
        }
        finally
        {
            if (parser != null) parser.DialogArgResolver = previousResponseResolver;
            _dialogSubjectUid = prevSubject;
            _dialogSubjectObj = prevSubjectObj;
        }
    }

    private bool RenderScriptDialog(string dialogId, int requestedPage,
        SphereNet.Scripting.Parsing.ScriptSection layoutSection, Serial subjectUid,
        List<string>? textLines = null, string? arguments = null)
    {
        if (_character == null || layoutSection.Keys.Count == 0) return false;

        var dialogLocals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["__ARGS"] = arguments ?? ""
        };
        var subjectObj = _dialogSubjectObj;
        IScriptObj subject = (IScriptObj?)subjectObj ??
            (subjectUid.IsValid ? _world.FindObject(subjectUid) ?? _character : _character);
        var layoutArgs = new ExecTriggerArgs(_character, requestedPage, 0, arguments ?? "")
        {
            Object1 = subject,
            Object2 = _character
        };
        var layoutScope = new ScriptScope { TriggerName = $"DIALOG:{dialogId}" };
        var interpreter = _triggerDispatcher?.Runner?.Interpreter;
        string ExpandInitialText(string text) => interpreter != null
            ? interpreter.ExpandText(text, subject, _client, layoutArgs, layoutScope)
            : ResolveInlineExpressions(text, dialogLocals, requestedPage);
        // Source-X expands TEXT once, before position and layout side effects.
        // Copy per opening: cached resource lines must remain unexpanded.
        textLines = textLines?.Select(ExpandInitialText).ToList();

        int openingPage = unchecked((ushort)requestedPage);
        // Source-X CDialogDef remaps the requested page to client page 1.
        // Apply the same permutation to both page markers and navigation buttons.
        int RemapPage(int page) => openingPage == 0 || page == 0 || page > openingPage
            ? page : page == openingPage ? 1 : page + 1;

        // Sphere dialog first line is the screen position "x,y".
        // Source-X reads this via s.ReadKey() before processing controls —
        // a raw line. The Key/Arg split now treats ',' as a separator, so
        // read the verbatim line or "5,25" collapses to "5" and every
        // dialog anchors at 0,0.
        int dialogX = 0, dialogY = 0;
        if (layoutSection.Keys.Count > 0)
        {
            // Source-X evaluates the first-line "x,y" through the expression
            // engine, so a DEF-based start position (<DEF.dlgstartpos> = 30,30)
            // resolves instead of collapsing the dialog to 0,0.
            string firstLine = layoutSection.Keys[0].RawLine.Trim();
            if (firstLine.IndexOf('<') >= 0)
                firstLine = ExpandInitialText(firstLine);
            (dialogX, dialogY) = ReadDialogPosition(firstLine);
        }

        var resourceId = _commands!.Resources!.ResolveDefName(dialogId);
        var gump = new GumpBuilder(subjectUid.IsValid ? subjectUid.Value : _character.Uid.Value,
            ((uint)resourceId.Type << 24) | (uint)resourceId.Index)
        {
            ExplicitX = dialogX,
            ExplicitY = dialogY
        };
        if (textLines != null) gump.AddScriptTexts(textLines);
        int originX = 0, originY = 0;
        int cursorX = 0, cursorY = 0;
        // Separate "row tracker" for the `*N` operator. Sphere treats *N as a
        // fresh row step independent of the +/- cursor used for column work.
        int rowCursorX = 0, rowCursorY = 0;
        // Sphere/UO page semantics: content emitted before the first PAGE
        // marker belongs to page 0 (shared/common) and must render
        // immediately. Some imported dialogs (e.g. d_admin_player_tweak)
        // never declare an explicit PAGE 0 header, so starting hidden would
        // drop the entire layout and produce an almost-empty 0xDD packet.
        bool currentPageVisible = true;

        // Expand FOR / WHILE / IF blocks into a flat key sequence so the
        // render switch below can remain a linear walk. Each unrolled
        // copy of a loop body runs with the iterator's value substituted
        // into <local._for> / <local.n> / etc. before render commands see
        // the args — matching Sphere's runtime-expansion behaviour.
        var expandedKeys = ExecuteDialogLayout(layoutSection.Keys, dialogLocals, requestedPage, subject, layoutArgs, layoutScope, out bool cancelled);
        if (cancelled) return false;

        // Diagnostic: count of commands per page post-expansion. If page 4
        // (FLAGS) comes out empty while the others are populated, the
        // FOR/IF expansion isn't unrolling into output.
        {
            int currentP = 0;
            var perPage = new Dictionary<int, int>();
            foreach (var k in expandedKeys)
            {
                string ck = k.Key.Trim().ToUpperInvariant();
                if (ck == "PAGE" && int.TryParse(k.Arg.Trim(), out int np))
                {
                    currentP = np;
                    continue;
                }
                perPage[currentP] = perPage.GetValueOrDefault(currentP) + 1;
            }
            _logger.LogDebug("[dialog_expand] id={Id} keys={Total} pages={Pages}",
                dialogId, expandedKeys.Count,
                string.Join(", ", perPage.Select(kv => $"p{kv.Key}:{kv.Value}")));
        }

        foreach (var key in expandedKeys)
        {
            string cmd = key.Key.Trim().ToUpperInvariant();
            string args = key.Arg;
            switch (cmd)
            {
                case "NOMOVE":
                    gump.SetNoMove();
                    break;
                case "NOCLOSE":
                    gump.SetNoClose();
                    break;
                case "NODISPOSE":
                    gump.SetNoDispose();
                    break;
                case "PAGE":
                {
                    // UO page model is CLIENT-side: every page element
                    // lives in the same gump packet, the client switches
                    // visibility when a page-nav button fires. Emit a
                    // `{ page N }` marker and let every subsequent
                    // element render under that tag.
                    // Source-X does NOT reset m_iOriginX/m_iOriginY on
                    // PAGE — the DORIGIN baseline persists across pages
                    // so that PAGE 1 content can use +N offsets relative
                    // to the last DORIGIN set on PAGE 0.
                    int pageNo = unchecked((int)CreateDialogNumberParser().EvaluateSingle(args.TrimStart('.')));
                    if (pageNo > 0)
                        gump.SetPage(RemapPage(pageNo));
                    currentPageVisible = true;
                    break;
                }
                case "DORIGIN":
                {
                    // Controls already emit dialog-space coordinates; update
                    // their baseline without adding an extra offset at emit time.
                    originX = originY = 0;
                    ReadDialogOrigin(args, ref rowCursorX, ref rowCursorY);
                    cursorX = rowCursorX;
                    cursorY = rowCursorY;
                    break;
                }
                case "TEXT":
                case "HTMLGUMP":
                case "CROPPEDTEXT":
                case "TEXTENTRY":
                case "TEXTENTRYLIMITED":
                case "GROUP":
                case "ITEMPROPERTY":
                    gump.AddScriptControl(cmd, args);
                    break;
                case "RESIZE":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 5);
                    if (parts.Length >= 5)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddResizePic(x, y, ParseIntToken(parts[2]), ParseIntToken(parts[3]), ParseIntToken(parts[4]));
                    }
                    else if (cmd == "RESIZE" && parts.Length == 4)
                    {
                        // Sphere RESIZE shorthand: x,y,width,height (no gumpId)
                        // Uses default background gump 9200.
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddResizePic(x, y, 9200, ParseIntToken(parts[2]), ParseIntToken(parts[3]));
                    }
                    break;
                }
                case "RESIZEPIC":
                {
                    var p = ReadControlArguments(args, 5, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out _);
                    gump.AddResizePic(p[0], p[1], p[2], p[3], p[4]);
                    break;
                }
                case "GUMPPIC":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 3, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out string hue);
                    gump.AddGumpPic(p[0], p[1], p[2], hue);
                    break;
                }
                case "TOOLTIP":
                {
                    if (!currentPageVisible) break;
                    if (TryReadTooltip(args, out uint cliloc, out string tooltipArguments))
                        gump.AddTooltip(cliloc, tooltipArguments);
                    break;
                }
                case "GUMPPICTILED":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 5, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out _);
                    gump.AddGumpPicTiled(p[0], p[1], p[2], p[3], p[4]);
                    break;
                }
                case "BUTTON":
                {
                    if (!currentPageVisible) break;
                    var parts = ReadButtonArguments(args, 7, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY);
                    if (parts.Length >= 7)
                    {
                        int x = parts[0] + originX;
                        int y = parts[1] + originY;
                        gump.AddButton(
                            x, y,
                            parts[2], parts[3], parts[6], parts[4], RemapPage(parts[5]));
                    }
                    break;
                }
                case "BUTTONTILEART":
                {
                    if (!currentPageVisible) break;
                    var parts = ReadButtonArguments(args, 11, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY);
                    if (parts.Length >= 11)
                    {
                        int x = parts[0] + originX;
                        int y = parts[1] + originY;
                        gump.AddButtonTileArt(
                            x, y,
                            parts[2], parts[3], parts[6], parts[4], RemapPage(parts[5]),
                            parts[7], parts[8], parts[9], parts[10]);
                    }
                    break;
                }
                case "DHTMLGUMP":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 6, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out string html);
                    // Layout already expanded the text; preserve emitted HTML.
                    gump.AddHtmlGump(p[0], p[1], p[2], p[3], html, p[4], p[5]);
                    break;
                }
                case "DCROPPEDTEXT":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 5, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out string text);
                    if (text.StartsWith('.')) text = text[1..];
                    gump.AddCroppedText(p[0], p[1], p[2], p[3], p[4], text);
                    break;
                }
                case "DTEXT":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 3, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out string text);
                    if (text.StartsWith('.')) text = text[1..];
                    gump.AddText(p[0], p[1], p[2], text);
                    break;
                }
                case "CHECKERTRANS":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 4, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out _);
                    gump.AddCheckerTrans(p[0], p[1], p[2], p[3]);
                    break;
                }
                case "CHECKBOX":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 6, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out _);
                    gump.AddCheckbox(p[0], p[1], p[2], p[3], p[4], p[5]);
                    break;
                }
                case "RADIO":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 6, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out _);
                    gump.AddRadio(p[0], p[1], p[2], p[3], p[4], p[5]);
                    break;
                }
                case "DTEXTENTRY":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 6, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out string text);
                    gump.AddTextEntry(p[0], p[1], p[2], p[3], p[4], p[5], text);
                    break;
                }
                case "DTEXTENTRYLIMITED":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 7, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out string text);
                    gump.AddScriptTextEntryLimited(p[0], p[1], p[2], p[3], p[4], p[5], text, p[6]);
                    break;
                }
                case "TILEPIC":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 3, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out _);
                    gump.AddTilePic(p[0], p[1], p[2]);
                    break;
                }
                case "TILEPICHUE":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 3, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out string hue);
                    gump.AddTilePicHue(p[0], p[1], p[2], hue);
                    break;
                }
                case "XMFHTMLGUMP":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 7, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out _);
                    gump.AddXmfHtmlGump(p[0], p[1], p[2], p[3], p[4], p[5], p[6]);
                    break;
                }
                case "XMFHTMLGUMPCOLOR":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 7, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out string color, preserveRemainder: true);
                    gump.AddXmfHtmlGumpColor(p[0], p[1], p[2], p[3], p[4], p[5], p[6], color);
                    break;
                }
                case "XMFHTMLTOK":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 8, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out string textArguments);
                    gump.AddXmfHtmlTok(p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7], textArguments);
                    break;
                }
                case "PICINPIC":
                {
                    if (!currentPageVisible) break;
                    var p = ReadControlArguments(args, 7, ref cursorX, ref cursorY, ref rowCursorX, ref rowCursorY, out _);
                    gump.AddPicInPic(p[0], p[1], p[2], p[3], p[4], p[5], p[6]);
                    break;
                }
            }
        }

        Gumps.RegisterScript(dialogId, gump.GumpId, gump.Serial, (buttonId, switches, textEntries) =>
        {
            if (_character == null)
                return;
            var prevSubject = _dialogSubjectUid;
            var prevSubjectObj = _dialogSubjectObj;
            _dialogSubjectUid = subjectUid;
            _dialogSubjectObj = subjectObj;
            try
            {
                // Source-X ignores unmatched responses. Page buttons are handled
                // by the client; only the script may explicitly reopen a dialog.
                TryRunScriptDialogButton(dialogId, (int)buttonId, switches, textEntries);
            }
            finally
            {
                _dialogSubjectUid = prevSubject;
                _dialogSubjectObj = prevSubjectObj;
            }
        });

        SendGump(gump);

        return true;
    }

    /// <summary>Execute the script's <c>[Dialog d_xxx Button]</c> <c>ON=buttonId</c>
    /// handler. Wires the dialog response (buttonId, switches, text entries)
    /// into the expression parser so <c>&lt;ArgN&gt;</c>, <c>&lt;Argtxt[N]&gt;</c>
    /// and <c>&lt;Argchk[N]&gt;</c> resolve correctly during evaluation.</summary>
    private bool TryRunScriptDialogButton(string dialogId, int buttonId,
        uint[] switches, (ushort Id, string Text)[] textEntries)
    {
        if (_character == null) return false;
        if (_triggerDispatcher?.Runner == null || _commands?.Resources == null) return false;

        if (!TryFindDialogButtonSection(dialogId, out var buttonSection))
            return false;

        // Build a lookup for Argtxt[N] / Argchk[N].
        var textById = new Dictionary<ushort, string>();
        foreach (var te in textEntries)
            textById.TryAdd(te.Id, te.Text);
        var switchSet = new HashSet<uint>(switches);
        var indexParser = new ExpressionParser
        {
            VariableResolver = name => _commands.Resources.TryResolveDefNameValue(name, out long number)
                ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : _commands.Resources.TryGetDefValue(name, out string value) ? value : null
        };

        string? Resolve(string varExpr)
        {
            string upper = varExpr.ToUpperInvariant();
            if (upper.StartsWith("ARGCHK", StringComparison.Ordinal))
            {
                string operand = upper[6..].TrimStart('.');
                if (operand.Length == 0) return switches.Length.ToString();
                if (operand.StartsWith("ID", StringComparison.Ordinal))
                    return switches.Length > 0 && switches[0] != 0 ? switches[0].ToString() : "-1";
                uint id = unchecked((uint)indexParser.EvaluateSingle(operand));
                return switchSet.Contains(id) ? "1" : "0";
            }
            if (upper.StartsWith("ARGTXT", StringComparison.Ordinal))
            {
                string operand = upper[6..].TrimStart('.');
                if (operand.Length == 0) return textEntries.Length.ToString();
                uint id = unchecked((uint)indexParser.EvaluateSingle(operand));
                return id <= ushort.MaxValue && textById.TryGetValue((ushort)id, out var text) ? text : "";
            }
            // ARGN/ARGS/ARGV use the mutable trigger args, including writes
            // made by PREBUTTON and called functions. Only input fields above
            // belong to the original client response.
            return null;
        }

        var parser = _triggerDispatcher.Runner.Interpreter.Expressions;
        if (parser == null) return false;

        var prev = parser.DialogArgResolver;
        parser.DialogArgResolver = Resolve;
        try
        {
            // Source-X parity: dialog button target = dialog subject
            // (the inspected object), NOT the GM character. SRC = GM,
            // target = inspected object. This ensures TRYP/INPDLG/property
            // edits operate on the correct object.
            IScriptObj buttonTarget = _character;
            if (_dialogSubjectObj != null || _dialogSubjectUid.IsValid)
            {
                var subj = ResolveDialogSubject();
                if (subj == null || subj.IsDeleted)
                    return false;
                buttonTarget = subj;
            }

            var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(_character)
            {
                Number1 = unchecked((uint)buttonId),
                DialogResponseResolver = Resolve,
            };

            var posBefore = _character.Position;
            ushort bodyBefore = _character.BodyId;
            ushort hueBefore = _character.Hue.Value;
            var flagsBefore = _character.StatFlags;

            _commands.Resources.TryGetDialogPrebutton(dialogId, out var prebuttonSection);
            bool ran = _triggerDispatcher.Runner.TryRunDialogButton(
                buttonSection, buttonId, buttonTarget, _client, trigArgs, prebuttonSection);
            if (ran && _character != null)
            {
                bool moved = !_character.Position.Equals(posBefore);
                bool appearance =
                    _character.BodyId != bodyBefore ||
                    _character.Hue.Value != hueBefore ||
                    _character.StatFlags != flagsBefore;

                if (moved)
                {
                    _world.MoveCharacter(_character, _character.Position);
                    Resync();
                    _mountEngine?.EnsureMountedState(_character);
                    BroadcastDrawObject(_character);
                }
                else if (appearance)
                {
                    // No teleport, but body / hue / flag changed (e.g.
                    // statf_hidden via |=). Re-send DrawObject so the
                    // client reflects the new appearance without a
                    // full resync.
                    BroadcastDrawObject(_character);
                }
            }
            return ran;
        }
        finally
        {
            parser.DialogArgResolver = prev;
        }
    }

    /// <summary>Pre-expand FOR / WHILE / IF / LOCAL blocks in a dialog's
    /// key sequence. Dialog scripts mix render verbs (BUTTON, DTEXT, …)
    /// with control-flow verbs Sphere's interpreter otherwise handles at
    /// runtime. The outer parser walks the section linearly, so we flatten
    /// loops into their rendered copies and evaluate IFs up front.
    /// <paramref name="locals"/> is shared with the caller so LOCAL.x=
    /// assignments stay visible to later expression resolution.</summary>
    private List<SphereNet.Scripting.Parsing.ScriptKey> ExpandDialogScriptKeys(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input,
        Dictionary<string, string> locals,
        int dialogArgN1, out bool cancelled)
    {
        var output = new List<SphereNet.Scripting.Parsing.ScriptKey>(input.Count);
        cancelled = ExpandRange(input, 0, input.Count, output, locals, dialogArgN1) == 1;
        return output;
    }

    private List<ScriptKey> ExecuteDialogLayout(
        IReadOnlyList<ScriptKey> input,
        Dictionary<string, string> fallbackLocals,
        int dialogArgN1,
        IScriptObj subject, ExecTriggerArgs triggerArgs, ScriptScope scope, out bool cancelled)
    {
        // GumpSetup consumes the first raw line as position, even when it
        // resembles a command or contains just one coordinate.
        IReadOnlyList<ScriptKey> executable = input.Skip(1).ToArray();
        var interpreter = _triggerDispatcher?.Runner?.Interpreter;
        if (interpreter == null || _character == null)
            return ExpandDialogScriptKeys(executable, fallbackLocals, dialogArgN1, out cancelled);

        var output = new List<ScriptKey>(input.Count);
        var renderTarget = new DialogRenderTarget(subject, output);

        interpreter.Execute(executable, renderTarget, _client, triggerArgs, scope);
        // Source-X suppresses a gump only for the exact RETURN 1 value.
        cancelled = scope.IsReturning && scope.NumericReturnValue == 1;
        return output;
    }

    private const int MaxExpandedLines = 10000;

    private long? ExpandRange(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input, int start, int end,
        List<SphereNet.Scripting.Parsing.ScriptKey> output,
        Dictionary<string, string> locals,
        int dialogArgN1)
    {
        int i = start;
        while (i < end)
        {
            if (output.Count >= MaxExpandedLines)
                return null;
            var k = input[i];
            string cmd = k.Key.Trim().ToUpperInvariant();
            string args = k.Arg;

            if (cmd == "RETURN")
                return ParseLongToken(ResolveInlineExpressions($"<EVAL {args}>", locals, dialogArgN1));

            if (cmd == "IF")
            {
                int ifEnd = FindBlockEnd(input, i + 1, end, "IF", "ENDIF");
                if (ifEnd < 0) { i = end; break; }
                // Split the IF body into IF / ELIF / ELSE branches.
                var branches = SplitIfBranches(input, i + 1, ifEnd);
                string resolvedCond = ResolveInlineExpressions(args, locals, dialogArgN1);
                bool taken = EvaluateDialogCondition(resolvedCond);
                int chosenStart = -1, chosenEnd = -1;
                if (taken) { chosenStart = branches[0].Start; chosenEnd = branches[0].End; }
                else
                {
                    for (int b = 1; b < branches.Count && chosenStart < 0; b++)
                    {
                        var br = branches[b];
                        if (br.Keyword == "ELSE")
                        { chosenStart = br.Start; chosenEnd = br.End; break; }
                        if (br.Keyword == "ELIF" || br.Keyword == "ELSEIF")
                        {
                            string elifCond = ResolveInlineExpressions(br.Condition!, locals, dialogArgN1);
                            if (EvaluateDialogCondition(elifCond))
                            { chosenStart = br.Start; chosenEnd = br.End; break; }
                        }
                    }
                }
                if (chosenStart >= 0)
                {
                    var returned = ExpandRange(input, chosenStart, chosenEnd, output, locals, dialogArgN1);
                    if (returned.HasValue) return returned;
                }
                i = ifEnd + 1;
                continue;
            }

            if (cmd == "FORINSTANCES")
            {
                // FORINSTANCES <defname> — runs the body once per world
                // instance of the given item definition. The expansion pass
                // can't rebind the default object to each instance, but the
                // dominant dialog pattern is bare counting
                // ("FORINSTANCES i_x / LOCAL.n ++ / ENDFOR"), which this
                // covers exactly.
                int fiEnd = FindForBlockEnd(input, i + 1, end);
                if (fiEnd < 0) { i = end; break; }
                string defName = ResolveInlineExpressions(args, locals, dialogArgN1).Trim();
                int instCount = Math.Min(500, CountWorldItemInstances(defName));
                for (int it = 0; it < instCount; it++)
                {
                    var returned = ExpandRange(input, i + 1, fiEnd, output, locals, dialogArgN1);
                    if (returned.HasValue) return returned;
                }
                i = fiEnd + 1;
                continue;
            }

            if (cmd == "FOR")
            {
                // FOR N  / FOR START END / FOR VAR START END.
                int forEnd = FindForBlockEnd(input, i + 1, end);
                if (forEnd < 0) { i = end; break; }
                string resolved = ResolveInlineExpressions(args, locals, dialogArgN1);
                ParseForRange(resolved, out string? iterName, out long from, out long to);
                const long maxIter = 500;
                long count = Math.Min(maxIter, to - from + 1);
                string? savedFor = locals.TryGetValue("_FOR", out var sf) ? sf : null;
                string? savedNamed = (iterName != null && locals.TryGetValue(iterName, out var sv)) ? sv : null;
                for (long it = 0; it < count; it++)
                {
                    string cur = (from + it).ToString();
                    locals["_FOR"] = cur;
                    if (iterName != null)
                        locals[iterName] = cur;
                    var returned = ExpandRange(input, i + 1, forEnd, output, locals, dialogArgN1);
                    if (returned.HasValue) return returned;
                }
                if (savedFor != null) locals["_FOR"] = savedFor; else locals.Remove("_FOR");
                if (iterName != null)
                {
                    if (savedNamed != null) locals[iterName] = savedNamed;
                    else locals.Remove(iterName);
                }
                i = forEnd + 1;
                continue;
            }

            // Dialog scripts frequently seed runtime state in the layout body
            // before drawing controls:
            //   ARGS <def.npctype_<dctag0.spawn_type>_spawn>
            //   SRC.CTAG0.spawn_type 1
            // Keep those side-effect lines in the pre-expansion pass so
            // later <ARGV[...]> / <DARGV> and CTAG-dependent expressions
            // resolve correctly.
            if (cmd == "ARGS")
            {
                string resolvedArgs = string.IsNullOrEmpty(args)
                    ? ""
                    : ResolveInlineExpressions(args, locals, dialogArgN1);
                locals["__ARGS"] = resolvedArgs;
                i++;
                continue;
            }
            if ((cmd.StartsWith("SRC.CTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("SRC.CTAG.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("SRC.DCTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("SRC.DCTAG.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("CTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("CTAG.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("DCTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("DCTAG.", StringComparison.OrdinalIgnoreCase)) &&
                _character != null)
            {
                string prop = cmd.StartsWith("SRC.", StringComparison.OrdinalIgnoreCase) ? cmd[4..] : cmd;
                string resolvedVal = string.IsNullOrEmpty(args)
                    ? ""
                    : ResolveInlineExpressions(args, locals, dialogArgN1);
                _character.TrySetProperty(prop, resolvedVal);
                i++;
                continue;
            }

            if (cmd == "WHILE")
            {
                int whileEnd = FindBlockEnd(input, i + 1, end, "WHILE", "ENDWHILE");
                if (whileEnd < 0) { i = end; break; }
                const int maxIter = 500;
                int iter = 0;
                while (iter < maxIter)
                {
                    string resolved = ResolveInlineExpressions(args, locals, dialogArgN1);
                    if (!EvaluateDialogCondition(resolved)) break;
                    var returned = ExpandRange(input, i + 1, whileEnd, output, locals, dialogArgN1);
                    if (returned.HasValue) return returned;
                    iter++;
                }
                i = whileEnd + 1;
                continue;
            }

            // REFn = <uid> — scope-local object reference. Storage
            // lives in the same `locals` dict under the key "REFn" so
            // subsequent <REFn> / <REFn.property> lookups see it.
            // Scripts like the admin panel rely on this to point rows
            // at account / character objects:
            //   REF1=<SERV.ACCOUNT.<Eval <CTag0.Dialog.Admin.Index>+1>>
            //   <DEF.admin_flag_1>: <REF1.NAME>
            if (cmd.Length > 3 && cmd.StartsWith("REF", StringComparison.OrdinalIgnoreCase) &&
                char.IsDigit(cmd[3]) && !cmd.Contains('.'))
            {
                string resolved = string.IsNullOrEmpty(args) ? "" : ResolveInlineExpressions(args, locals, dialogArgN1);
                locals[cmd.ToUpperInvariant()] = resolved;
                i++;
                continue;
            }

            if (cmd.StartsWith("LOCAL.", StringComparison.OrdinalIgnoreCase))
            {
                // LOCAL.x = value  or  LOCAL.x += N
                string nameAndOp = cmd[6..]; // after "LOCAL."
                // Detect compound operator in args: "+= 1", "= 5", "1", etc.
                string varName = nameAndOp;
                string valueExpr = args;
                char opCh = ' ';
                if (valueExpr.Length > 0)
                {
                    var trimmed = valueExpr.TrimStart();
                    if (trimmed.StartsWith("+=", StringComparison.Ordinal)) { opCh = '+'; valueExpr = trimmed[2..]; }
                    else if (trimmed.StartsWith("-=", StringComparison.Ordinal)) { opCh = '-'; valueExpr = trimmed[2..]; }
                    else if (trimmed.StartsWith("=", StringComparison.Ordinal)) { opCh = '='; valueExpr = trimmed[1..]; }
                }
                string resolved = ResolveInlineExpressions(valueExpr.Trim(), locals, dialogArgN1);
                if (opCh is '+' or '-')
                {
                    long num = ParseLongToken(resolved);
                    long current = locals.TryGetValue(varName, out var cur) && long.TryParse(cur, out long pv) ? pv : 0;
                    locals[varName] = (opCh == '+' ? current + num : current - num).ToString();
                }
                else
                {
                    // Sphere LOCALs are strings: keep the resolved text verbatim
                    // so comma lists ("a_town1,a_town2,…") and .= concatenations
                    // survive instead of collapsing to a number. ++/--/.= lines
                    // arrive here already rewritten into <EVAL …>/<KEY>… form by
                    // ScriptKey.Parse, so plain assignment covers them too.
                    locals[varName] = resolved;
                }
                i++;
                continue;
            }

            // Render command — inline-resolve the args and emit. Local
            // scope flows into the args via ResolveInlineExpressions so
            // <local.x> / <eval …> inside BUTTON / DTEXT / RADIO /
            // RESIZEPIC coordinates come out as concrete numbers.
            string resolvedArg = string.IsNullOrEmpty(args)
                ? args
                : ResolveInlineExpressions(args, locals, dialogArgN1);
            output.Add(new SphereNet.Scripting.Parsing.ScriptKey(k.Key, resolvedArg));
            i++;
        }
        return null;
    }

    /// <summary>FOR-family block end: every FOR* loop keyword (FOR,
    /// FORINSTANCES, FORCHARS, FORITEMS, …) opens a block closed by the
    /// shared ENDFOR terminator, matching Sphere's loop grammar. Plain
    /// open/close matching on "FOR" would let a nested FORINSTANCES's
    /// ENDFOR close the outer FOR early.</summary>
    private static int FindForBlockEnd(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input, int start, int end)
    {
        int depth = 1;
        for (int i = start; i < end; i++)
        {
            string k = input[i].Key.Trim().ToUpperInvariant();
            if (k.StartsWith("FOR", StringComparison.Ordinal)) depth++;
            else if (k == "ENDFOR") { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    /// <summary>Count live world items spawned from the given item defname:
    /// matches either the resolved def index as the item's BaseId or the
    /// SCRIPTDEF tag that scripted ITEMDEFs stamp on their instances.</summary>
    private int CountWorldItemInstances(string defName)
    {
        if (string.IsNullOrEmpty(defName) || _commands?.Resources == null) return 0;
        var rid = _commands.Resources.ResolveDefName(defName);
        if (!rid.IsValid) return 0;
        int count = 0;
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is SphereNet.Game.Objects.Items.Item it && !it.IsDeleted &&
                (it.BaseId == rid.Index ||
                 (it.TryGetTag("SCRIPTDEF", out string? sd) && int.TryParse(sd, out int sdi) && sdi == rid.Index)))
                count++;
        }
        return count;
    }

    /// <summary>Find the matching end keyword, honouring nested blocks.
    /// Returns the absolute index of the end keyword, or -1 if unmatched.</summary>
    private static int FindBlockEnd(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input, int start, int end,
        string openKeyword, string endKeyword)
    {
        int depth = 1;
        for (int i = start; i < end; i++)
        {
            string k = input[i].Key.Trim().ToUpperInvariant();
            if (k == openKeyword) depth++;
            else if (k == endKeyword) { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    private readonly record struct IfBranch(string Keyword, int Start, int End, string? Condition);

    private static List<IfBranch> SplitIfBranches(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input, int start, int end)
    {
        // Branch boundaries at ELIF / ELSEIF / ELSE at depth 0 (relative
        // to the outer IF we're inside). Nested IFs count as depth.
        var list = new List<IfBranch>();
        int depth = 0;
        int segStart = start;
        string curKeyword = "IF";
        string? curCondition = null;
        for (int i = start; i < end; i++)
        {
            string k = input[i].Key.Trim().ToUpperInvariant();
            if (k == "IF") { depth++; continue; }
            if (k == "ENDIF") { depth--; continue; }
            if (depth != 0) continue;
            if (k == "ELSE" || k == "ELIF" || k == "ELSEIF")
            {
                list.Add(new IfBranch(curKeyword, segStart, i, curCondition));
                curKeyword = k;
                curCondition = (k != "ELSE") ? input[i].Arg : null;
                segStart = i + 1;
            }
        }
        list.Add(new IfBranch(curKeyword, segStart, end, curCondition));
        return list;
    }

    private static void ParseForRange(string expr, out string? iterName, out long from, out long to)
    {
        iterName = null;
        from = 0; to = 0;
        var parts = expr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            // FOR N — loops 0 to N-1 in Sphere.
            long.TryParse(parts[0], out long n);
            to = n - 1;
        }
        else if (parts.Length >= 2)
        {
            // FOR var start end
            if (!long.TryParse(parts[0], out from))
            {
                iterName = parts[0];
                if (parts.Length >= 3)
                {
                    long.TryParse(parts[1], out from);
                    long.TryParse(parts[2], out to);
                }
                return;
            }
            long.TryParse(parts[1], out to);
        }
    }

    private static long ParseLongToken(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out long hv))
            return hv;
        return long.TryParse(s, out long v) ? v : 0;
    }

    /// <summary>Cheap-and-cheerful truthiness for IF / WHILE. An expression
    /// is truthy if it evaluates to non-zero; strings compare as text.
    /// The Sphere parser accepts relational operators, which
    /// ExpressionParser already understands — we just evaluate through it.</summary>
    private bool EvaluateDialogCondition(string condition)
    {
        string c = condition.Trim();
        if (string.IsNullOrEmpty(c)) return false;
        if (c.Length >= 2 && c[0] == '(' && c[^1] == ')')
            c = c[1..^1].Trim();
        if (string.IsNullOrEmpty(c) || c == "0")
            return false;

        bool hasOperator = c.AsSpan().IndexOfAny("!+-*/%&|()<>=~^") >= 0;

        var parser = new ExpressionParser();
        long v = parser.Evaluate(c.AsSpan());
        if (v != 0)
            return true;
        if (hasOperator)
            return false;
        bool truthy = !long.TryParse(c, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _);
        return truthy;
    }

    /// <summary>Resolve &lt;local.x&gt; / &lt;dlocal.x&gt; / &lt;eval …&gt;
    /// / &lt;def0.…&gt; / &lt;src.…&gt; etc. in an argument string, using
    /// the dialog-local scope for LOCAL references. <paramref name="dialogArgN1"/>
    /// feeds &lt;argn1&gt; / &lt;argn&gt; so the Sphere "page &lt;argn1&gt;"
    /// pattern in dialog layouts resolves to the page the dialog was
    /// opened on (e.g. sdialog d_moongates &lt;eval &lt;src.p.m&gt;+1&gt;).</summary>
    private string ResolveInlineExpressions(string input, Dictionary<string, string> locals, int dialogArgN1 = 0)
    {
        if (string.IsNullOrEmpty(input) || input.IndexOf('<') < 0) return input;

        var servResolver = _triggerDispatcher?.Runner?.Interpreter?.ServerPropertyResolver;
        var runner = _triggerDispatcher?.Runner;
        var parser = new ExpressionParser
        {
            // Script [FUNCTION] calls inside dialog layouts —
            // <ARRAYCOUNT a,b,c>, <ARRAY list,idx>, <FormatMinutes n>, … —
            // execute through the trigger runner with the dialog's character
            // as the target, mirroring how the interpreter resolves them in
            // trigger bodies. Without this, FOR bounds like
            // "FOR s 1 <ARRAYCOUNT <LOCAL.list>>" never resolve and the loop
            // body is dropped from the rendered gump.
            FunctionResolver = expr =>
            {
                if (runner == null || _character == null) return null;
                string call = expr.Trim();
                if (call.Length == 0) return null;
                int sp = call.IndexOfAny([' ', '\t']);
                string fname = sp < 0 ? call : call[..sp];
                string fargs = sp < 0 ? "" : call[(sp + 1)..].Trim();
                return runner.TryEvaluateFunction(fname, fargs, _character, null, null, out string fval)
                    ? fval
                    : null;
            },
            VariableResolver = varName =>
            {
                string upper = varName.ToUpperInvariant();
                if (upper == "ARGN" || upper == "ARGN1")
                    return dialogArgN1.ToString();
                if (upper == "ARGS" || upper == "DARGS")
                    return locals.TryGetValue("__ARGS", out var av) ? av : "";
                if (upper == "DARGV")
                {
                    string rawArgs = locals.TryGetValue("__ARGS", out var av) ? av : "";
                    var toks = rawArgs.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    return toks.Length.ToString();
                }
                if (upper.StartsWith("ARGV", StringComparison.Ordinal))
                {
                    string rawArgs = locals.TryGetValue("__ARGS", out var av) ? av : "";
                    var toks = rawArgs.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    string suffix = upper.Length > 4 ? upper[4..] : "";
                    if (suffix.StartsWith("[", StringComparison.Ordinal) && suffix.EndsWith("]", StringComparison.Ordinal) && suffix.Length > 2)
                        suffix = suffix[1..^1];
                    if (int.TryParse(suffix, out int idx) && idx >= 0 && idx < toks.Length)
                        return toks[idx];
                    return "";
                }
                // Uninitialised LOCAL / DLOCAL return "0" per Sphere
                // convention — scripts read them as zero before the first
                // assignment (common pattern: "while <dlocal.n>" where the
                // counter is bumped inside the body).
                if (upper.StartsWith("LOCAL.", StringComparison.Ordinal))
                    return locals.TryGetValue(upper[6..], out var lv) ? lv : "0";
                if (upper.StartsWith("DLOCAL.", StringComparison.Ordinal))
                    return locals.TryGetValue(upper[7..], out var dlv) ? dlv : "0";

                // REFn and REFn.property — dialog-scoped object references.
                // REF1..REF999 are stored in the same locals dict keyed as
                // "REFN" (upper). `<REFn>` returns the stored reference
                // string (usually a UID); `<REFn.property>` looks up the
                // referenced object via the REF_GET protocol so its
                // properties flow into the rendered layout.
                if (upper.Length > 3 && upper.StartsWith("REF", StringComparison.Ordinal) &&
                    char.IsDigit(upper[3]))
                {
                    int dotIdx = upper.IndexOf('.');
                    string refKey = dotIdx > 0 ? upper[..dotIdx] : upper;
                    string? refVal = locals.TryGetValue(refKey, out var rv) ? rv : null;
                    if (dotIdx < 0) return refVal ?? "0";
                    if (string.IsNullOrEmpty(refVal) || refVal == "0") return "0";
                    string subProp = upper[(dotIdx + 1)..];
                    return servResolver?.Invoke($"_REF_GET={refVal}|{subProp}") ?? "0";
                }

                // CTAG0.X / CTAG.X / DCTAG0.X / DCTAG.X on the current character. Reads from
                // the client-session CTag map (Source-X CClient::m_TagDefs
                // parity), not the persistent TAG storage. Defaults to
                // "0" when unset — Sphere convention.
                if (upper.StartsWith("CTAG0.", StringComparison.Ordinal) || upper.StartsWith("CTAG.", StringComparison.Ordinal) ||
                    upper.StartsWith("DCTAG0.", StringComparison.Ordinal) || upper.StartsWith("DCTAG.", StringComparison.Ordinal))
                {
                    int dot = upper.IndexOf('.');
                    string tagKey = upper[(dot + 1)..];
                    string? tagVal = _character?.CTags.Get(tagKey);
                    if (string.IsNullOrEmpty(tagVal) && tagKey.Equals("ACCOUNTLANG", StringComparison.OrdinalIgnoreCase))
                    {
                        string fallbackLang = GetEffectiveAccountLang();
                        if (!string.IsNullOrEmpty(fallbackLang))
                            return fallbackLang;
                    }
                    return tagVal ?? "0";
                }
                if ((upper.StartsWith("DEF.", StringComparison.Ordinal) || upper.StartsWith("DEF0.", StringComparison.Ordinal)) &&
                    _commands?.Resources != null)
                {
                    string origKey = varName.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase)
                        ? varName[4..]
                        : varName[5..];
                    if (_commands.Resources.TryGetDefValue(origKey, out string defTextVal))
                    {
                        string stripped = StripSurroundingQuotes(defTextVal);
                        return stripped;
                    }
                    var defRid = _commands.Resources.ResolveDefName(origKey);
                    if (defRid.IsValid) return defRid.Index.ToString();
                    return "0";
                }
                if ((upper.StartsWith("SRC.", StringComparison.Ordinal) || upper.StartsWith("DSRC.", StringComparison.Ordinal)) && _character != null)
                {
                    int d = upper.IndexOf('.');
                    string sub = upper[(d + 1)..];
                    if (_character.TryGetProperty(sub, out string srcVal))
                    {
                        if ((sub.Equals("CTAG0.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase) ||
                             sub.Equals("CTAG.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrEmpty(srcVal) || srcVal == "0"))
                            return GetEffectiveAccountLang();
                        return srcVal;
                    }
                }
                if (upper.StartsWith("SERV.", StringComparison.Ordinal) && servResolver != null)
                    return servResolver(upper[5..]);

                // Dialog subject (CLIMODE_DIALOG pObj) wins over GM
                // properties for bare reads — <BODY>, <STR>, <NAME>
                // inside d_charprop1 refer to the inspected target,
                // not the GM. Fall back to GM when subject misses so
                // admin-style dialogs keep their existing behaviour.
                if (_dialogSubjectObj != null || _dialogSubjectUid.IsValid)
                {
                    var subj = ResolveDialogSubject();
                    if (subj != null)
                    {
                        // Sphere <I.*> alias = the subject itself.
                        //   <I.STR> → subject STR, <I.0> → skill 0 level.
                        // Character.TryGetProperty doesn't know the "I."
                        // prefix, so strip it here and delegate the rest.
                        string lookup = upper.StartsWith("I.", StringComparison.Ordinal)
                            ? upper[2..]
                            : upper;
                        // A bare number on Character resolves to that skill's
                        // current level — matches Source-X CChar::r_WriteVal
                        // on an integer key.
                        if (subj is Character subjCh && int.TryParse(lookup, out int skillIdx)
                            && skillIdx >= 0 && skillIdx < (int)SkillType.Qty)
                            return subjCh.GetSkill((SkillType)skillIdx).ToString();
                        if (subj.TryGetProperty(lookup, out string subjProp))
                            return subjProp;
                    }
                }
                if (_character != null && _character.TryGetProperty(upper, out string charProp))
                    return charProp;

                // Last-resort delegation: the same resolver the script
                // interpreter uses at runtime covers ACCOUNT.x,
                // ISEVENT.x, ISDIALOGOPEN.x, VAR0.x, GETREFTYPE, and
                // other dialog-common accessors.
                if (_character != null &&
                    TryResolveScriptVariable(upper, _character, null, out string fallback))
                    return fallback;

                // Bare defname constants used in script arithmetic/bit tests,
                // e.g. <statf_insubstantial>, <memory_ipet>. Function defnames
                // are excluded: a bare <somefunc> token must fall through to
                // the FunctionResolver and execute, not yield its resource
                // index.
                if (_commands?.Resources != null && IsPlainDefToken(upper))
                {
                    var rid = _commands.Resources.ResolveDefName(upper);
                    if (rid.IsValid && rid.Type != ResType.Function) return rid.Index.ToString();
                }

                return null;
            },
        };
        return parser.EvaluateStr(input);
    }

    // These three used to re-open and re-parse EVERY script file on EVERY gump
    // open / button click / menu request — a ~400ms main-loop stall per click
    // on a real pack, worst when the id does not exist (native-fallback ids
    // like d_helppage paid the full scan every time). ResourceHolder now
    // retains the sections at load; these are O(1) lookups.
    private bool TryFindDialogButtonSection(string dialogId, out SphereNet.Scripting.Parsing.ScriptSection buttonSection)
    {
        buttonSection = null!;
        return _commands?.Resources != null &&
            _commands.Resources.TryGetDialogButton(dialogId, out buttonSection);
    }

    private bool TryFindDialogSections(string dialogId, out SphereNet.Scripting.Parsing.ScriptSection layoutSection)
    {
        layoutSection = null!;
        return _commands?.Resources != null &&
            _commands.Resources.TryGetDialogLayout(dialogId, out layoutSection);
    }

    internal bool TryFindMenuSection(string menuDefname, out SphereNet.Scripting.Parsing.ScriptSection menuSection)
    {
        menuSection = null!;
        return _commands?.Resources != null &&
            _commands.Resources.TryGetMenuSection(menuDefname, out menuSection);
    }

    private string ResolveDialogHtml(string html, IScriptObj target)
    {
        // Delegate through the same resolver chain the interpreter uses.
        // SERV.*, RTIME, RTICKS, REFn.property, etc. all live on the server
        // property resolver — without routing dialog text through it we lost
        // the most common Sphere gump substitutions (<Serv.Servname>, …).
        var servResolver = _triggerDispatcher?.Runner?.Interpreter?.ServerPropertyResolver;
        var parser = new ExpressionParser
        {
            VariableResolver = varName =>
            {
                if (varName.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) &&
                    _commands?.Resources != null &&
                    _commands.Resources.TryGetDefValue(varName[4..], out string defVal))
                {
                    return defVal;
                }

                if (TryResolveScriptVariable(varName, target, null, out string runtimeVal))
                    return runtimeVal;

                // Source/target routing: Src.X resolves through the admin's
                // own character. Admin dialogs reference <Src.Version>,
                // <Src.Account>, <Src.CTag0.…>, etc.
                if (varName.StartsWith("SRC.", StringComparison.OrdinalIgnoreCase))
                {
                    string subProp = varName[4..];
                    if (_character != null && _character.TryGetProperty(subProp, out string srcProp))
                    {
                        if ((subProp.Equals("CTAG0.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase) ||
                             subProp.Equals("CTAG.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrEmpty(srcProp) || srcProp == "0"))
                            return GetEffectiveAccountLang();
                        return srcProp;
                    }
                }

                if (target.TryGetProperty(varName, out string prop))
                    return prop;

                // SERV.* / RTIME / RTICKS — delegate to the runtime resolver.
                if (servResolver != null)
                {
                    if (varName.StartsWith("SERV.", StringComparison.OrdinalIgnoreCase))
                    {
                        string servProp = varName[5..];
                        string? servVal = servResolver(servProp);
                        if (servVal != null) return servVal;
                    }
                    if (varName.StartsWith("RTIME", StringComparison.OrdinalIgnoreCase) ||
                        varName.StartsWith("RTICKS", StringComparison.OrdinalIgnoreCase))
                    {
                        string? rVal = servResolver(varName);
                        if (rVal != null) return rVal;
                    }
                    // Bare server metrics (CLIENTS, ACCOUNTS, CHARS, ITEMS, VERSION,
                    // SERVNAME, TIME, SAVECOUNT, MEM, REGEN0-3) as fallback.
                    string? bare = servResolver(varName);
                    if (bare != null) return bare;
                }

                if (_commands?.Resources != null && IsPlainDefToken(varName))
                {
                    var rid = _commands.Resources.ResolveDefName(varName);
                    if (rid.IsValid) return rid.Index.ToString();
                }

                return null;
            }
        };

        return parser.EvaluateStr(html);
    }

    internal static bool IsPlainDefToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        foreach (char ch in token)
        {
            bool ok = char.IsLetterOrDigit(ch) || ch is '_' or '.';
            if (!ok) return false;
        }
        return true;
    }

    private string GetEffectiveAccountLang()
    {
        if (_character != null && _character.TryGetProperty("ACCOUNT.LANG", out string langRaw))
        {
            string lang = (langRaw ?? "").Trim().ToUpperInvariant();
            if (lang.Length == 0) return "ENG";
            return lang switch
            {
                "ENU" => "ENG",
                "FRB" or "FRC" => "FRA",
                "ESN" => "ESP",
                _ => lang
            };
        }
        return "ENG";
    }

    /// <summary>Resolve a dialog coordinate token.
    /// Formats:
    ///   N      — absolute (preserves origin)
    ///   +N     — offset from origin (preserves origin)
    ///   *N     — rowCursor += N; cursor = rowCursor (next-row step, independent
    ///            of the +/- column walk)
    /// <paramref name="rowCursor"/> may alias <paramref name="cursor"/> when the
    /// caller hasn't wired a separate row tracker (old call sites).</summary>
    private static int ResolveDialogCoord(string token, ref int cursor, ref int rowCursor)
    {
        // Sphere DORIGIN coord rules (verified against d_SphereAdmin_PlayerTweak):
        //   bare N : return N without changing the baseline
        //   +N     : return baseline + N (NON-mutating row-relative offset)
        //   -N     : return baseline - N (NON-mutating row-relative offset)
        //   *N     : baseline += N, return baseline (advance the row)
        //
        // The earlier "+N means cursor += N" reading was wrong: with the
        // DORIGIN block
        //     DText  +35 +0   _Properties
        //     Button +0  -2   4005 4006 0 3 0
        // the button has to land at origin.x (X=5), to the LEFT of the
        // text. Cumulative cursor logic stuck the button at X=40 on top
        // of the label, which was the visible "buttons drift sideways /
        // text is unreadable" symptom in d_SphereAdmin_PlayerTweak.
        // `cursor` is kept around as a back-compat alias mirroring the
        // baseline so callers that still pass it observe the same value
        // as `rowCursor`.
        token = token.Trim();
        if (token.StartsWith('+'))
        {
            int delta = ParseIntToken(token[1..]);
            return rowCursor + delta;
        }
        if (token.StartsWith('-'))
        {
            int delta = ParseIntToken(token[1..]);
            return rowCursor - delta;
        }
        if (token.StartsWith('*'))
        {
            int delta = ParseIntToken(token[1..]);
            rowCursor += delta;
            cursor = rowCursor;
            return rowCursor;
        }

        // Absolute coordinates do not move the DORIGIN baseline.
        return ParseIntToken(token);
    }

    private void ReadDialogOrigin(string text, ref int x, ref int y)
    {
        var parser = CreateDialogNumberParser();
        int position = 0;
        int ReadAxis(int current)
        {
            while (position < text.Length && (char.IsWhiteSpace(text[position]) || text[position] is '.' or ',')) position++;
            if (position < text.Length && text[position] == '-' &&
                (position + 1 == text.Length || char.IsWhiteSpace(text[position + 1])))
            {
                position++;
                return current;
            }
            bool advance = position < text.Length && text[position] == '*';
            if (advance) position++;
            int value = unchecked((int)parser.EvaluateSingle(text.AsSpan(position), out int consumed));
            position += consumed;
            return advance ? current + value : value;
        }
        x = ReadAxis(x);
        y = ReadAxis(y);
    }

    private static int ResolveDialogCoord(string token, ref int cursor)
    {
        int dummy = cursor;
        return ResolveDialogCoord(token, ref cursor, ref dummy);
    }

    /// <summary>DEFNAME text values in Sphere scripts often ship wrapped
    /// in double quotes (<c>CharFlag.1 "Invulnerable"</c>). The quotes
    /// are a Sphere source-lexer convention, not part of the payload —
    /// strip a single matched pair when resolving so the gump label
    /// reads "Invulnerable" instead of <c>"Invulnerable"</c>.</summary>
    private static string StripSurroundingQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }

    private static bool TryReadTooltip(string text, out uint cliloc, out string arguments)
    {
        cliloc = 0;
        arguments = "";
        text = text.Trim();
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') quoted = !quoted;
            if (quoted) continue;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && (char.IsWhiteSpace(c) || c is ',' or '='))
            {
                int next = i + 1;
                if (char.IsWhiteSpace(c))
                {
                    while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                    if (next < text.Length && text[next] is ',' or '=') next++;
                }
                arguments = text[next..].Trim();
                return TryReadTooltipNumber(text.AsSpan(0, i), out cliloc);
            }
        }
        // CDialogDef requires two Str_ParseCmds arguments, even if the
        // second is explicitly empty after a separator.
        return false;
    }

    private static bool TryReadTooltipNumber(ReadOnlySpan<char> text, out uint value)
    {
        // Str_ToU/cstr_to_num uses an unsigned accumulator, auto-detected
        // Sphere hex and prefix conversion (not the expression evaluator).
        value = 0;
        if (text.IsEmpty) return false;
        static int Digit(char c) => c is >= '0' and <= '9' ? c - '0'
            : c is >= 'a' and <= 'f' ? c - 'a' + 10
            : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
        bool hex = text.Length > 1 && text[0] == '0' && Digit(text[1]) >= 0;
        int position = hex ? 1 : 0;
        int digits = 0;
        // The reference's decimal and hex fast paths fall through to the
        // following conversion stages; preserve that prefix behavior.
        for (int stage = hex ? 1 : 0; stage < 3; stage++)
        {
            int radix = stage == 1 || hex ? 16 : 10;
            if (stage == 1)
                while (position < text.Length && text[position] == '0') position++;
            while (position < text.Length)
            {
                char c = text[position];
                if (stage == 0 && c == '.') { position++; continue; }
                int digit = Digit(c);
                if (digit < 0) break;
                if (digit >= radix)
                {
                    if (stage == 2) return false;
                    break;
                }
                if (value > (uint.MaxValue - (uint)digit) / (uint)radix) return false;
                value = value * (uint)radix + (uint)digit;
                position++;
                digits++;
            }
        }
        return digits > 0;
    }

    private ExpressionParser CreateDialogNumberParser()
        => new ExpressionParser
        {
            VariableResolver = name =>
            {
                var resources = _commands?.Resources;
                if (resources == null) return null;
                if (resources.TryResolveDefNameValue(name, out long number))
                    return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return resources.TryGetDefValue(name, out string value) ? value : null;
            }
        };

    private int[] ReadButtonArguments(string text, int count,
        ref int cursorX, ref int cursorY, ref int originX, ref int originY)
        => ReadControlArguments(text, count, ref cursorX, ref cursorY, ref originX, ref originY, out _);

    private int[] ReadControlArguments(string text, int count,
        ref int cursorX, ref int cursorY, ref int originX, ref int originY, out string remainder, bool preserveRemainder = false)
    {
        var parser = CreateDialogNumberParser();
        var values = new int[count];
        int position = 0;
        for (int i = 0; i < count; i++)
        {
            // Retain the port's comma-separated form as well as Source-X's
            // whitespace/dot separators. Read one operand, not one word.
            while (position < text.Length && (char.IsWhiteSpace(text[position]) || text[position] is '.' or ','))
                position++;
            char relative = '\0';
            if (i < 2 && position < text.Length && text[position] is '+' or '-' or '*')
            {
                relative = text[position++];
                if (relative == '-' && position < text.Length && char.IsWhiteSpace(text[position]))
                {
                    values[i] = i == 0 ? originX : originY;
                    continue;
                }
            }
            int value = unchecked((int)parser.EvaluateSingle(text.AsSpan(position), out int consumed));
            position += consumed;
            int origin = i == 0 ? originX : originY;
            values[i] = relative switch
            {
                '+' or '*' => origin + value,
                '-' => origin - value,
                _ => value
            };
            if (relative == '*')
            {
                if (i == 0) cursorX = originX = values[i];
                else cursorY = originY = values[i];
            }
        }
        if (!preserveRemainder)
        {
            while (position < text.Length && text[position] == '.') position++;
            while (position < text.Length && (char.IsWhiteSpace(text[position]) || text[position] == ',')) position++;
        }
        remainder = text[position..];
        return values;
    }

    private (int X, int Y) ReadDialogPosition(string line)
    {
        var parser = CreateDialogNumberParser();
        // Source-X Str_ParseCmds uses =, comma, space and tab outside
        // grouped expressions. An omitted coordinate evaluates to zero.
        line = line.Trim();
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') quoted = !quoted;
            if (quoted) continue;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && (c is ',' or '=' || char.IsWhiteSpace(c)))
            {
                int next = i + 1;
                if (char.IsWhiteSpace(c))
                {
                    while (next < line.Length && char.IsWhiteSpace(line[next])) next++;
                    if (next < line.Length && line[next] is ',' or '=') next++;
                }
                return (unchecked((int)parser.Evaluate(line.AsSpan(0, i))),
                    unchecked((int)parser.Evaluate(line.AsSpan(next))));
            }
        }
        return (unchecked((int)parser.Evaluate(line)), 0);
    }

    private static int ParseIntToken(string token)
    {
        token = token.Trim();
        if (token.Length == 0)
            return 0;

        // Sphere convention (matches ScriptKey.TryParseNumber and Source-X
        // CExpression::GetVal): a leading '0' on a multi-digit token marks
        // the value as HEX. Without this rule "0480" silently parsed as
        // decimal 480 instead of 0x480 (1152) and admin gump hues came
        // out as random off-spectrum colors — the d_SphereAdmin_PlayerTweak
        // labels were rendered in colors the client treats as
        // near-invisible (the "yazÄ±lar okunmuyor" symptom).

        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token[2..], System.Globalization.NumberStyles.HexNumber, null, out int hex))
            return hex;

        if (token.Length > 1 && token[0] == '0' &&
            int.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out int legacyHex))
            return legacyHex;

        if (int.TryParse(token, out int dec))
            return dec;

        return 0;
    }

    private static string[] SplitTokens(string input, int minLeadingTokens, bool keepRemainder = false)
    {
        if (!keepRemainder)
            return input.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var parts = new List<string>();
        string text = input.Trim();
        int i = 0;
        while (i < text.Length && parts.Count < minLeadingTokens)
        {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
            if (i >= text.Length) break;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ',') i++;
            parts.Add(text[start..i]);
        }

        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
        parts.Add(i < text.Length ? text[i..] : "");
        return parts.ToArray();
    }
}
