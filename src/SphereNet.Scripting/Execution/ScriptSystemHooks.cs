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
        ["unkdata"] = ["unknown_client_data"],
        ["quotaexceed"] = ["exceed_network_quota"]
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

    public bool DispatchPacket(byte opcode, IScriptObj source, IScriptObj? argo = null, string args = "")
        => Dispatch($"f_packet_0x{opcode:X2}", source, argo, args, opcode);
}
