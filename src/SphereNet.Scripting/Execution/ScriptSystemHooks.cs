using SphereNet.Core.Interfaces;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// Central dispatcher for Source-X style global lifecycle hooks.
/// Produces a consistent trigger arg shape (SRC/ARGO/ARGS/ARGN) and logs failures.
/// </summary>
public sealed class ScriptSystemHooks
{
    private readonly TriggerRunner _runner;
    private static readonly Dictionary<string, string[]> ServerHookAliases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string[]> AccountHookAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["connect"] = ["login"],
        ["pwchange"] = ["pinchange"]
    };
    private static readonly Dictionary<string, string[]> ClientHookAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unkdata"] = ["unknown_client_data"]
    };

    public ScriptSystemHooks(TriggerRunner runner)
    {
        _runner = runner;
    }

    public bool Dispatch(
        string functionName,
        IScriptObj? source,
        IScriptObj? argo = null,
        string args = "",
        int argn1 = 0,
        int argn2 = 0,
        int argn3 = 0,
        ITextConsole? console = null)
    {
        return TryDispatch(functionName, source, argo, args, argn1, argn2, argn3, console, out bool handled)
            && handled;
    }

    private bool TryDispatch(
        string functionName,
        IScriptObj? source,
        IScriptObj? argo,
        string args,
        int argn1,
        int argn2,
        int argn3,
        ITextConsole? console,
        out bool handled)
    {
        handled = false;
        IScriptObj? target = source ?? argo;
        if (target == null)
            return false;

        var triggerArgs = new TriggerArgs(source, argn1, argn2, args)
        {
            Number3 = argn3,
            Object1 = argo,
            Object2 = source
        };

        if (!_runner.TryRunFunction(functionName, target, console, triggerArgs, out var result))
            return false;

        handled = result == Core.Enums.TriggerResult.True;
        return true;
    }

    public bool DispatchServer(string hookSuffix, IScriptObj serverContext, string args = "", int argn1 = 0, int argn2 = 0, int argn3 = 0)
    {
        if (TryDispatch($"f_onserver_{hookSuffix}", serverContext, null, args,
                argn1, argn2, argn3, null, out bool handled))
            return handled;

        if (!ServerHookAliases.TryGetValue(hookSuffix, out var aliases))
            return false;

        foreach (string alias in aliases)
        {
            string function = alias.StartsWith("f_", StringComparison.OrdinalIgnoreCase)
                ? alias
                : $"f_{alias}";
            if (TryDispatch(function, serverContext, null, args,
                    argn1, argn2, argn3, null, out handled))
                return handled;
        }

        return false;
    }

    /// <summary>Dispatch a server hook while preserving Source-X RETURN values
    /// beyond boolean RETURN 1 (notably connectreq_ex RETURN 2 = reject + ban).</summary>
    public Core.Enums.TriggerResult DispatchServerResult(string hookSuffix, IScriptObj serverContext,
        string args = "", int argn1 = 0, int argn2 = 0, int argn3 = 0)
    {
        var triggerArgs = new TriggerArgs(serverContext, argn1, argn2, args)
        {
            Number3 = argn3,
            Object2 = serverContext
        };
        return _runner.TryRunFunction($"f_onserver_{hookSuffix}", serverContext, null, triggerArgs, out var result)
            ? result
            : Core.Enums.TriggerResult.Default;
    }

    /// <summary>Dispatch a server hook whose ARGS the script may rewrite, handing the
    /// rewritten text back (Source-X CWorldComm::Broadcast, CWorldComm.cpp:228-234:
    /// <c>f_onserver_broadcast</c> runs with ARGS = the message, RETURN 1 drops the
    /// broadcast and otherwise <c>m_s1</c> is what gets sent).</summary>
    public Core.Enums.TriggerResult DispatchServerRewrite(string hookSuffix, IScriptObj serverContext,
        ref string args)
    {
        var triggerArgs = new TriggerArgs(serverContext, 0, 0, args)
        {
            Object2 = serverContext
        };
        if (!_runner.TryRunFunction($"f_onserver_{hookSuffix}", serverContext, null, triggerArgs, out var result))
            return Core.Enums.TriggerResult.Default;
        args = triggerArgs.ArgString;
        return result;
    }

    public bool DispatchAccount(string hookSuffix, IScriptObj accountObj, IScriptObj? argo = null, string args = "", int argn1 = 0, int argn2 = 0, int argn3 = 0)
    {
        if (TryDispatch($"f_onaccount_{hookSuffix}", accountObj, argo, args,
                argn1, argn2, argn3, null, out bool handled))
            return handled;

        if (!AccountHookAliases.TryGetValue(hookSuffix, out var aliases))
            return false;

        foreach (string alias in aliases)
        {
            string function = alias.StartsWith("f_", StringComparison.OrdinalIgnoreCase)
                ? alias
                : $"f_onaccount_{alias}";
            if (TryDispatch(function, accountObj, argo, args,
                    argn1, argn2, argn3, null, out handled))
                return handled;
        }

        return false;
    }

    public bool DispatchClient(string hookSuffix, IScriptObj clientObj, IScriptObj? argo = null, string args = "", int argn1 = 0, int argn2 = 0, int argn3 = 0, ITextConsole? console = null)
    {
        if (TryDispatch($"f_onclient_{hookSuffix}", clientObj, argo, args,
                argn1, argn2, argn3, console, out bool handled))
            return handled;

        // Source-X compatibility: support legacy/verbose client hook names.
        if (!ClientHookAliases.TryGetValue(hookSuffix, out var aliases))
            return false;

        foreach (string alias in aliases)
        {
            if (TryDispatch($"f_onclient_{alias}", clientObj, argo, args,
                    argn1, argn2, argn3, console, out handled))
                return handled;
        }

        return false;
    }

    public bool DispatchObject(string hookSuffix, IScriptObj obj, IScriptObj? source = null, string args = "", int argn1 = 0, int argn2 = 0, int argn3 = 0)
        => Dispatch($"f_onobj_{hookSuffix}", source ?? obj, obj, args, argn1, argn2, argn3);

    public bool DispatchItem(string hookSuffix, IScriptObj itemObj, IScriptObj? source = null, string args = "", int argn1 = 0, int argn2 = 0, int argn3 = 0)
        => Dispatch($"f_onitem_{hookSuffix}", source ?? itemObj, itemObj, args, argn1, argn2, argn3);

    /// <summary>Source-X CClient::Event_ExceededNetworkQuota (CClientEvent.cpp:819-842):
    /// <c>f_onclient_exceed_network_quota</c> runs on the server object with SRC = the
    /// client, ARGN1 = 1 (output) / 2 (input), ARGN2 = the bytes moved in the check
    /// period, ARGN3 = the quota, LOCAL.ACCOUNT = the account name ("NA" without one)
    /// and LOCAL.IP = the peer address. RETURN 0 keeps the client and logs, RETURN 1
    /// keeps it silently; anything else - no RETURN, another value, no such function -
    /// is the default: disconnect and log.</summary>
    public NetworkQuotaVerdict RunNetworkQuotaHook(IScriptObj server, IScriptObj? client,
        ITextConsole? console, byte type, long bytes, long quota, string account, string ip)
    {
        var locals = new Variables.VarMap();
        locals.SetStr("ACCOUNT", false, account);
        locals.SetStr("IP", false, ip);
        var args = new TriggerArgs(client, type, bytes, "")
        {
            Number3 = quota,
            SharedLocals = locals
        };
        if (!_runner.TryRunFunctionNumeric("f_onclient_exceed_network_quota", server, console, args,
                out long? ret))
            return NetworkQuotaVerdict.Default;
        return ret switch
        {
            0 => NetworkQuotaVerdict.Log,
            1 => NetworkQuotaVerdict.Ignore,
            _ => NetworkQuotaVerdict.Default
        };
    }

    /// <summary>Source-X SCRIPT_MAX_LINE_LEN, the cap on LOCAL.STR.</summary>
    private const int ScriptMaxLineLen = 4096;

    /// <summary>Run a PACKETx / OUTPACKETx filter function the way Source-X
    /// CClient::xPacketFilter / xOutPacketFilter do (CClientEvent.cpp:3151-3248): on
    /// the server object (it is both the default object and SRC), with
    /// ARGN1 = the opcode, ARGS = the client's address, ARGO = the client, and in
    /// LOCAL: CONNECTIONTYPE, NUM (packet length), STR (the bytes as text, up to the
    /// first zero), ACCOUNT and CHAR when logged in, and 0..NUM-1 holding every byte.
    /// True when the function exists and returned 1, which drops the packet.</summary>
    public bool RunPacketFilter(string functionName, IScriptObj server, IScriptObj? client,
        ReadOnlySpan<byte> packet, string peer, int connectionType, string? account, uint? charUid)
    {
        if (packet.Length == 0 || string.IsNullOrEmpty(functionName))
            return false;

        var locals = new Variables.VarMap();
        locals.SetInt("CONNECTIONTYPE", connectionType);
        locals.SetInt("NUM", packet.Length);
        var text = packet[..Math.Min(packet.Length, ScriptMaxLineLen)];
        int zero = text.IndexOf((byte)0);
        if (zero >= 0)
            text = text[..zero];
        locals.SetStr("STR", true, System.Text.Encoding.Latin1.GetString(text));
        if (account != null)
        {
            locals.SetStr("ACCOUNT", false, account);
            if (charUid is uint uid)
                locals.SetInt("CHAR", uid);
        }
        for (int i = 0; i < packet.Length; i++)
            locals.SetInt(i.ToString(System.Globalization.CultureInfo.InvariantCulture), packet[i]);

        var args = new TriggerArgs(server, packet[0], 0, peer)
        {
            Object1 = client,
            SharedLocals = locals
        };
        return _runner.TryRunFunction(functionName, server, server as ITextConsole, args, out var result)
            && result == Core.Enums.TriggerResult.True;
    }
}

/// <summary>What <c>f_onclient_exceed_network_quota</c> decided.</summary>
public enum NetworkQuotaVerdict
{
    /// <summary>No RETURN 0/1: disconnect the client and log.</summary>
    Default,
    /// <summary>RETURN 0: keep the client, log.</summary>
    Log,
    /// <summary>RETURN 1: keep the client, no log.</summary>
    Ignore
}
