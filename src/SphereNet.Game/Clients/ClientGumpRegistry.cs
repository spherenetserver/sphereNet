namespace SphereNet.Game.Clients;

/// <summary>
/// Tracks native callbacks and each open script dialog instance. Script
/// responses retain their subject, while counts follow Source-X's open map.
/// </summary>
public sealed class ClientGumpRegistry
{
    public HashSet<uint> ActiveGumps { get; } = [];

    public Dictionary<uint, Action<uint, uint[], (ushort, string)[]>> Callbacks { get; } = [];

    /// <summary>Open script dialogs (name → gump id). Entries drop when the
    /// last instance is consumed by a response or DIALOGCLOSE.</summary>
    public Dictionary<string, uint> OpenScriptDialogs { get; } = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ScriptResponse(uint Serial, Action<uint, uint[], (ushort, string)[]> Callback);
    private readonly Dictionary<uint, List<ScriptResponse>> _scriptResponses = [];

    internal void RegisterScript(string name, uint id, uint serial, Action<uint, uint[], (ushort, string)[]> callback)
    {
        OpenScriptDialogs[name] = id;
        if (!_scriptResponses.TryGetValue(id, out var entries))
            _scriptResponses[id] = entries = [];
        entries.Add(new(serial, callback));
    }

    internal int ScriptCount(uint id) => _scriptResponses.TryGetValue(id, out var entries) ? entries.Count : 1;
    internal bool HasScript(uint id) => _scriptResponses.ContainsKey(id);
    internal uint ScriptSerial(uint id, uint fallback) =>
        _scriptResponses.TryGetValue(id, out var entries) ? entries[0].Serial : fallback;

    internal bool TryTakeScript(uint id, uint serial, out Action<uint, uint[], (ushort, string)[]>? callback)
    {
        callback = null;
        if (!_scriptResponses.TryGetValue(id, out var entries)) return false;
        int index = entries.FindIndex(e => e.Serial == serial);
        if (index < 0) return false;
        callback = entries[index].Callback;
        entries.RemoveAt(index);
        if (entries.Count == 0)
        {
            _scriptResponses.Remove(id);
            ActiveGumps.Remove(id);
            foreach (string name in OpenScriptDialogs.Where(e => e.Value == id).Select(e => e.Key).ToArray())
                OpenScriptDialogs.Remove(name);
        }
        return true;
    }

    internal void Clear()
    {
        ActiveGumps.Clear();
        Callbacks.Clear();
        OpenScriptDialogs.Clear();
        _scriptResponses.Clear();
    }
}
