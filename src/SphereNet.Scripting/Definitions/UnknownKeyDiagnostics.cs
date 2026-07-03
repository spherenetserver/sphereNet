using System.Collections.Concurrent;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// Visibility for the script-pack surface: ITEMDEF/CHARDEF lines whose key no
/// parser recognized used to be dropped SILENTLY, so nobody could tell which
/// legacy properties a real pack was losing. Def parsers record every dropped
/// key here; the loader logs a top-N summary after a pack load.
/// </summary>
public static class UnknownKeyDiagnostics
{
    private static readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public static void Record(string defType, string key) =>
        _counts.AddOrUpdate($"{defType}.{key.ToUpperInvariant()}", 1, (_, n) => n + 1);

    public static int TotalDropped
    {
        get { int t = 0; foreach (var v in _counts.Values) t += v; return t; }
    }

    public static void Clear() => _counts.Clear();

    /// <summary>Top-N dropped keys as "CHARDEF.FOO x123" lines.</summary>
    public static IReadOnlyList<string> Summary(int top = 20)
    {
        var list = new List<KeyValuePair<string, int>>(_counts);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        var result = new List<string>(Math.Min(top, list.Count));
        for (int i = 0; i < list.Count && i < top; i++)
            result.Add($"{list[i].Key} x{list[i].Value}");
        return result;
    }
}
