using SphereNet.Game.Objects;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>Dialog handler (decomposition phase 3) — the members below
    /// delegate so every call site stays unchanged. The logic lives in
    /// <see cref="ClientDialogHandler"/>.</summary>
    internal ClientDialogHandler Dialogs => _dialogs ??= new ClientDialogHandler(this);
    private ClientDialogHandler? _dialogs;

    public bool IsScriptDialogOpen(string dialogId) => Dialogs.IsScriptDialogOpen(dialogId);

    public bool CloseScriptDialog(string dialogId) => Dialogs.CloseScriptDialog(dialogId);

    public bool TryShowScriptDialog(string dialogId, int requestedPage) =>
        Dialogs.TryShowScriptDialog(dialogId, requestedPage);

    public bool TryShowScriptDialog(string dialogId, int requestedPage, ObjBase? subject) =>
        Dialogs.TryShowScriptDialog(dialogId, requestedPage, subject);

    public bool OpenNamedDialog(string dialogId, int requestedPage = 0, ObjBase? subject = null) =>
        Dialogs.OpenNamedDialog(dialogId, requestedPage, subject);

    // Cross-partial bridges (ScriptConsole MENU rendering + def-token checks)
    // until that partial gets its own phase-3 conversion.
    internal bool TryFindMenuSection(string menuDefname, out SphereNet.Scripting.Parsing.ScriptSection menuSection) =>
        Dialogs.TryFindMenuSection(menuDefname, out menuSection);

    internal static bool IsPlainDefToken(string token) => ClientDialogHandler.IsPlainDefToken(token);
}
