using SphereNet.Panel;

namespace SphereNet.Host;

/// <summary>
/// Builds a PanelContext whose delegates are backed by the IpcBridge and ServerProcess.
/// </summary>
public static class HostPanelContext
{
    public static PanelContext Build(
        ServerProcess proc,
        IpcBridge ipc,
        string? iniPath,
        string? scriptsPath)
    {
        return new PanelContext
        {
            IniPath     = iniPath,
            ScriptsPath = scriptsPath,

            // ── Lifecycle ──────────────────────────────────────────────────
            IsServerRunning = () => proc.IsRunning,
            StartServer = () => { proc.Start(); return proc.IsRunning; },
            OnShutdown = () => { proc.Stop(); return !proc.IsRunning; },
            OnRestart = () => { proc.Restart(); return true; },

            // ── Stats ──────────────────────────────────────────────────────
            GetStats = () => ipc.LastStats ?? new ServerStats(
                "SphereNet", "—", 0, 0, 0, 0, 0, 0, 0, 0),

            // ── Players ────────────────────────────────────────────────────
            GetOnlinePlayers = () =>
                Run(() => ipc.QueryAsync<List<PlayerInfo>>("players")) ?? [],

            // ── Accounts ───────────────────────────────────────────────────
            GetAllAccounts = () =>
                Run(() => ipc.QueryAsync<List<AccountInfo>>("accounts")) ?? [],

            GetAccount = name =>
                Run(() => ipc.QueryAsync<AccountInfo>("account", new { name })),

            CreateAccount = (name, pass) =>
                Run(() => ipc.MutateAsync("account_create", new { name, pass })),

            DeleteAccount = name =>
                Run(() => ipc.MutateAsync("account_delete", new { name })),

            SetAccountBanned = (name, banned) =>
                Run(() => ipc.MutateAsync("account_ban", new { name, banned })),

            SetAccountPassword = (name, pass) =>
                Run(() => ipc.MutateAsync("account_set_pass", new { name, pass })),

            SetAccountPrivLevel = (name, level) =>
                Run(() => ipc.MutateAsync("account_set_plevel", new { name, level })),

            // ── Commands ───────────────────────────────────────────────────
            OnSave = () => Run(() => ipc.MutateAsync("server_save")),
            OnResync = () => Run(() => ipc.MutateAsync("server_resync")),
            OnGc = () => Run(() => ipc.MutateAsync("server_gc")),
            OnRespawn = () => Run(() => ipc.MutateAsync("server_respawn")),
            OnRestock = () => Run(() => ipc.MutateAsync("server_restock")),
            OnBroadcast = msg => Run(() => ipc.MutateAsync("server_broadcast", new { msg })),

            ExecuteCommand = cmd =>
                Run(() => ipc.QueryAsync<string[]>("exec", new { raw = cmd })) ?? [],

            // ── Debug ──────────────────────────────────────────────────────
            GetDebugState = () =>
                Run(() => ipc.QueryAsync<DebugState>("debug")) ?? new DebugState(false, false),

            SetPacketDebug = on =>
                Run(() => ipc.MutateAsync("set_packet_debug", new { on })),

            SetScriptDebug = on =>
                Run(() => ipc.MutateAsync("set_script_debug", new { on })),

            // ── Dialog designer ────────────────────────────────────────────
            GetGumpPng = id => Run(() => ipc.QueryAsync<byte[]>("gump", new { id })),
            ListDialogNames = () => Run(() => ipc.QueryAsync<List<string>>("dialogs")) ?? [],
            GetDialogSource = name => Run(() => ipc.QueryAsync<string>("dialog_source", new { name })),
        };
    }

    private static T? Run<T>(Func<Task<T?>> fn) => fn().GetAwaiter().GetResult();

    private static bool Run(Func<Task<bool>> fn) => fn().GetAwaiter().GetResult();
}
