using SphereNet.Core.Interfaces;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// The SRC of the script line whose &lt;...&gt; is being resolved. Upstream every read
/// carries its pSrc (r_WriteVal(key, sVal, pSrc)), and a few keys answer relative to it -
/// &lt;obj.CANSEELOS&gt; with no argument is "can SRC see this object" (OC_CANSEELOS,
/// CObjBase.cpp:1152-1155). Property reads here take no source, so the interpreter keeps
/// it here for the duration of each resolution; per thread, restored on the way out, so
/// nested resolutions and other threads never see someone else's SRC.
/// </summary>
public static class ScriptReadContext
{
    [ThreadStatic] private static IScriptObj? t_source;

    /// <summary>SRC of the line being resolved, or null outside a script read.</summary>
    public static IScriptObj? Source
    {
        get => t_source;
        internal set => t_source = value;
    }
}
