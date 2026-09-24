namespace SphereNet.Game.Speech;

/// <summary>
/// Keyword matching the way Source-X matches spoken words against a keyword list.
/// </summary>
public static class SpeechWords
{
    /// <summary>The words a player says to call the guards. Source-X reads VAR.guardcall
    /// first, but its empty-test is <c>strnicmp(s, "", 0)</c>, which is always 0, so
    /// this default is what every shard actually uses (CClientEvent.cpp:1868).</summary>
    public const string GuardCallWords = "GUARD,GUARDS";

    /// <summary>Source-X FindStrWord (sstring.cpp:751): find any of the comma-separated
    /// <paramref name="keywords"/> as a whole word in <paramref name="text"/>, case
    /// insensitively. A match must start a word (the character before it is not a
    /// letter) and end at whitespace or the end of the text. Returns the index just
    /// past the match, or 0 when nothing matched.</summary>
    public static int FindStrWord(string? text, string keywords)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keywords))
            return 0;

        int kw = 0; // start of the keyword being tried
        int j = 0;
        for (int i = 0; ; ++i)
        {
            char k = kw + j < keywords.Length ? keywords[kw + j] : '\0';
            if (k == '\0' || k == ',')
            {
                char t = i < text.Length ? text[i] : '\0';
                if (t == '\0' || IsWhitespace(t))
                    return i;
                j = 0;
            }
            if (i >= text.Length)
            {
                int comma = keywords.IndexOf(',', kw);
                if (comma < 0)
                    return 0;
                kw = comma + 1;
                i = 0;
                j = 0;
            }
            if (j == 0 && i > 0 && IsAlpha(text[i - 1]))
                continue; // not the start of a word
            char tc = i < text.Length ? text[i] : '\0';
            char kc = kw + j < keywords.Length ? keywords[kw + j] : '\0';
            if (char.ToUpperInvariant(tc) == char.ToUpperInvariant(kc))
                ++j;
            else
                j = 0;
        }
    }

    /// <summary>Did the speaker call the guards (Source-X Event_Talk_Common,
    /// CClientEvent.cpp:1870)?</summary>
    public static bool IsGuardCall(string? text) => FindStrWord(text, GuardCallWords) > 0;

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f';
    private static bool IsAlpha(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
