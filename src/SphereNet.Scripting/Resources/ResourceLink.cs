using SphereNet.Core.Types;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Scripting.Resources;

/// <summary>
/// Resource with link to script file location. Maps to CResourceLink in Source-X.
/// Stores the file reference and line context so the section can be re-read on demand.
/// Also manages trigger bitmasks from ScanSection.
/// </summary>
public class ResourceLink : ResourceDef
{
    private readonly Dictionary<int, int> _triggerBitmask = [];

    private string? _scriptFilePath;
    public string? ScriptFilePath
    {
        get => _scriptFilePath;
        set => _scriptFilePath = value != null ? string.Intern(value) : null;
    }
    public int ScriptLineNumber { get; set; }
    public string HeaderArgument { get; set; } = "";
    public bool HasBeenScanned { get; set; }

    /// <summary>
    /// Parsed keys from initial script load.
    /// Retained for definition types (ItemDef, CharDef, SpellDef) to avoid re-reading files.
    /// </summary>
    public List<ScriptKey>? StoredKeys { get; private set; }
    private readonly Dictionary<string, List<ScriptKey>> _triggerBodies = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ScriptKey>? FunctionBody { get; private set; }

    public ResourceLink(ResourceId id) : base(id) { }

    /// <summary>
    /// Scan a section for DEFNAME and ON=@Trigger entries.
    /// Builds the trigger bitmask for fast trigger existence checks.
    /// If <paramref name="retainKeys"/> is true, stores all keys for later use by DefinitionLoader.
    /// </summary>
    public void ScanSection(ScriptSection section, Func<string, int>? triggerNameToIndex = null,
        bool retainKeys = false)
    {
        foreach (var key in section.Keys)
        {
            if (key.Key.Equals("DEFNAME", StringComparison.OrdinalIgnoreCase))
            {
                DefName = key.Arg.Trim();
            }
            else if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase) && key.Arg.StartsWith('@'))
            {
                string trigName = key.Arg[1..].Trim().ToUpperInvariant();
                if (triggerNameToIndex != null)
                {
                    int idx = triggerNameToIndex(trigName);
                    if (idx >= 0)
                        SetTriggerActive(idx);
                }
            }
        }

        if (retainKeys)
        {
            StoredKeys = section.Keys;
            BuildBodyIndexes(section.Keys);
        }

        HasBeenScanned = true;
    }

    /// <summary>The retained section carried at least one ON=@... trigger block
    /// (only meaningful when scanned with retainKeys).</summary>
    public bool HasAnyTriggerBody => _triggerBodies.Count > 0;

    public bool TryGetTriggerBody(string triggerName, out IReadOnlyList<ScriptKey> body)
    {
        string key = triggerName.TrimStart('@');
        if (_triggerBodies.TryGetValue(key, out var lines))
        {
            body = lines;
            return true;
        }

        body = Array.Empty<ScriptKey>();
        return false;
    }

    private void BuildBodyIndexes(List<ScriptKey> keys)
    {
        if (Id.Type == Core.Enums.ResType.Function)
            FunctionBody = keys;

        for (int i = 0; i < keys.Count; i++)
        {
            if (!keys[i].Key.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
                !keys[i].HasArg ||
                !keys[i].Arg.StartsWith('@'))
                continue;

            string triggerName = keys[i].Arg[1..].Trim();
            _triggerBodies[triggerName] = CollectTriggerBody(keys, i + 1);
        }
    }

    private static List<ScriptKey> CollectTriggerBody(List<ScriptKey> keys, int startIdx)
    {
        var body = new List<ScriptKey>();
        for (int i = startIdx; i < keys.Count; i++)
        {
            if (keys[i].Key.Equals("ON", StringComparison.OrdinalIgnoreCase) &&
                keys[i].HasArg &&
                keys[i].Arg.StartsWith('@'))
                break;
            body.Add(keys[i]);
        }
        return body;
    }

    public void SetTriggerActive(int triggerIndex)
    {
        int wordIndex = triggerIndex / 32;
        int bitIndex = triggerIndex % 32;
        _triggerBitmask.TryGetValue(wordIndex, out int word);
        word |= (1 << bitIndex);
        _triggerBitmask[wordIndex] = word;
    }

    public bool IsTriggerActive(int triggerIndex)
    {
        int wordIndex = triggerIndex / 32;
        int bitIndex = triggerIndex % 32;
        if (!_triggerBitmask.TryGetValue(wordIndex, out int word))
            return false;
        return (word & (1 << bitIndex)) != 0;
    }

    /// <summary>
    /// Re-open the script file at the stored position for on-demand reading.
    /// Maps to CResourceLock in Source-X.
    /// </summary>
    public ScriptFile? OpenAtStoredPosition()
    {
        if (string.IsNullOrEmpty(ScriptFilePath))
            return null;

        var file = new ScriptFile { UseCache = true };
        if (!file.Open(ScriptFilePath))
            return null;

        if (ScriptLineNumber > 0)
            file.SeekToLine(ScriptLineNumber);

        return file;
    }
}
