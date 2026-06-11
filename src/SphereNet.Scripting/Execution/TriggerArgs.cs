using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// Trigger arguments container. Maps to CScriptTriggerArgs in Source-X.
/// </summary>
public sealed class TriggerArgs : ITriggerArgs
{
    private string _argString = "";
    private string[]? _argvCache;

    public IScriptObj? Source { get; set; }
    public IScriptObj? Object1 { get; set; }
    public IScriptObj? Object2 { get; set; }
    public int Number1 { get; set; }
    public int Number2 { get; set; }
    public int Number3 { get; set; }
    public string ArgString
    {
        get => _argString;
        set
        {
            _argString = value ?? "";
            _argvCache = null;
        }
    }

    /// <summary>Shared LOCAL.* pool for the whole trigger chain (Source-X
    /// CScriptTriggerArgs.m_VarsLocal). When set, every trigger block run with
    /// these args uses this map as its scope locals instead of a fresh one —
    /// the engine can seed values (e.g. @SpellEffectTick LOCAL.EFFECT) and
    /// read script writes back after the fire. Functions still get their own
    /// locals (Source-X function-call semantics).</summary>
    public Variables.VarMap? SharedLocals { get; set; }

    public TriggerArgs() { }

    public TriggerArgs(IScriptObj? source, int n1 = 0, int n2 = 0, string argStr = "")
    {
        Source = source;
        Number1 = n1;
        Number2 = n2;
        ArgString = argStr;
    }

    public IReadOnlyList<string> GetArgv() => _argvCache ??= SplitArgString(_argString);

    public int GetArgc() => GetArgv().Count;

    private static string[] SplitArgString(string argString) =>
        string.IsNullOrWhiteSpace(argString)
            ? []
            : argString.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
