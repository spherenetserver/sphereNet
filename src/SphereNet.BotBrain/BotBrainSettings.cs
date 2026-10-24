namespace SphereNet.BotBrain;

/// <summary>
/// The brain's settings, read from <c>playerbots/ai.txt</c> (no sphere.ini keys): one
/// <c>key value</c> per line, <c>#</c> starts a comment. Without the file, or without a
/// <c>model</c> line, there is no brain and bots answer from their own lines.
/// </summary>
public sealed class BotBrainSettings
{
    /// <summary>The GGUF model file; a relative path is taken from the data folder.</summary>
    public string ModelPath { get; set; } = "";
    /// <summary>CPU threads llama.cpp works on; the rest are the game's.</summary>
    public int Threads { get; set; } = 4;
    /// <summary>Most tokens in a reply (a short chat line is 10-25).</summary>
    public int Tokens { get; set; } = 24;
    /// <summary>Milliseconds a bot waits for its reply before it says one of its own lines.</summary>
    public int WaitMs { get; set; } = 4000;
    /// <summary>Thoughts waiting beyond the ones being written; more are refused.</summary>
    public int Queue { get; set; } = 4;
    /// <summary>Replies written at once (sequences of one context): on the CPU a few
    /// cost hardly more than one.</summary>
    public int Parallel { get; set; } = 2;
    /// <summary>Tokens of context: the persona, a few exchanges and the reply.</summary>
    public int Context { get; set; } = 1024;
    /// <summary>Exchanges with the same player remembered for the next reply.</summary>
    public int History { get; set; } = 3;
    /// <summary>Sampling temperature: higher is livelier, lower is plainer.</summary>
    public float Temperature { get; set; } = 0.7f;

    public static BotBrainSettings? Read(string dataDir)
    {
        string path = Path.Combine(dataDir, "ai.txt");
        if (!File.Exists(path)) return null;
        var s = new BotBrainSettings();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;
            int sp = line.IndexOfAny([' ', '\t']);
            if (sp <= 0) continue;
            string key = line[..sp].ToLowerInvariant(), value = line[(sp + 1)..].Trim();
            switch (key)
            {
                case "model": s.ModelPath = Path.IsPathRooted(value) ? value : Path.Combine(dataDir, value); break;
                case "threads" when int.TryParse(value, out int n): s.Threads = Math.Clamp(n, 1, 64); break;
                case "tokens" when int.TryParse(value, out int n): s.Tokens = Math.Clamp(n, 8, 200); break;
                case "wait" when int.TryParse(value, out int n): s.WaitMs = Math.Clamp(n, 500, 30_000); break;
                case "parallel" when int.TryParse(value, out int n): s.Parallel = Math.Clamp(n, 1, 16); break;
                case "queue" when int.TryParse(value, out int n): s.Queue = Math.Clamp(n, 1, 32); break;
                case "context" when int.TryParse(value, out int n): s.Context = Math.Clamp(n, 256, 8192); break;
                case "history" when int.TryParse(value, out int n): s.History = Math.Clamp(n, 0, 10); break;
                case "temperature" when float.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float t):
                    s.Temperature = Math.Clamp(t, 0f, 2f);
                    break;
            }
        }
        return s.ModelPath.Length > 0 ? s : null;
    }
}
