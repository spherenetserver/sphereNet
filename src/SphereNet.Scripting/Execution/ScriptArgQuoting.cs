namespace SphereNet.Scripting.Execution;

/// <summary>
/// Whether the value of the script line being executed was written in quotes.
///
/// Source-X reads an assignment's value with <c>GetArgStr(&amp;fQuoted)</c> and hands
/// the flag to <c>CVarDefMap::SetStr</c>: <c>TAG.X="5"</c> is a string var and is
/// saved as <c>TAG.X="5"</c>, <c>TAG.X=5</c> a number saved as <c>TAG.X=05</c>, and
/// <c>TAG.X=""</c> keeps an empty string var where <c>TAG.X=</c> removes the key
/// (CObjBase.cpp:1788, CVarDefMap.cpp:467). The interpreter strips the quote pair
/// before the value reaches a setter; this carries the flag past that point for
/// the one value it belongs to.
/// </summary>
public static class ScriptArgQuoting
{
    [ThreadStatic] private static string? _quotedValue;

    /// <summary>True when <paramref name="value"/> is the unquoted text of the
    /// quoted value of the line currently executing.</summary>
    public static bool IsQuoted(string? value) =>
        value != null && _quotedValue != null && string.Equals(value, _quotedValue, StringComparison.Ordinal);

    /// <summary>Mark the current line's value for the lifetime of the returned scope.
    /// <paramref name="unquotedValue"/> is null when the value was not quoted.</summary>
    public static Scope Enter(string? unquotedValue)
    {
        var scope = new Scope(_quotedValue);
        _quotedValue = unquotedValue;
        return scope;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly string? _previous;
        internal Scope(string? previous) => _previous = previous;
        public void Dispose() => _quotedValue = _previous;
    }
}
