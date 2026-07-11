using System.Text;

namespace SphereNet.Scripting.Parsing;

/// <summary>
/// Source-X script packs are a mix of UTF-8 and legacy Windows-1252 files.
/// Decode valid UTF-8 (including BOM files) as UTF-8 and fall back to the
/// legacy code page only when strict UTF-8 validation fails.
/// </summary>
internal static class ScriptTextEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static Encoding s_legacyEncoding;
    private static ScriptEncodingMode s_mode = ScriptEncodingMode.Auto;

    static ScriptTextEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        s_legacyEncoding = Encoding.GetEncoding(1252);
    }

    public static void Configure(string? mode, int legacyCodePage)
    {
        s_mode = mode?.Trim().ToUpperInvariant() switch
        {
            "UTF8" or "UTF-8" => ScriptEncodingMode.Utf8,
            "LEGACY" or "ANSI" => ScriptEncodingMode.Legacy,
            _ => ScriptEncodingMode.Auto
        };

        try
        {
            s_legacyEncoding = Encoding.GetEncoding(legacyCodePage);
        }
        catch (ArgumentException)
        {
            s_legacyEncoding = Encoding.GetEncoding(1252);
        }
    }

    public static string ReadAllText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return StrictUtf8.GetString(bytes, 3, bytes.Length - 3);

        if (s_mode == ScriptEncodingMode.Legacy)
            return s_legacyEncoding.GetString(bytes);
        if (s_mode == ScriptEncodingMode.Utf8)
            return StrictUtf8.GetString(bytes);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return s_legacyEncoding.GetString(bytes);
        }
    }

    public static string[] ReadAllLines(string path)
    {
        string text = ReadAllText(path);
        using var reader = new StringReader(text);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    private enum ScriptEncodingMode
    {
        Auto,
        Utf8,
        Legacy
    }
}
