using SphereNet.Game.Objects;

namespace SphereNet.Game.Messages;

/// <summary>
/// The <c>@hue,font,unicode </c> prefix a script may put in front of ANY line of text.
///
/// Upstream parses it in one place, <c>CClient::addBarkParse</c>
/// (CClientMsg.cpp:798-830), and every text path reaches that place: a system message
/// (addSysMessage, CClientLog.cpp:200), an object message (addObjMessage,
/// CClientMsg.cpp:967) and speech alike. So a pack writes
/// <c>MESSAGE @,,1,1 [&lt;SERV.NAME&gt; Staff]</c> and expects the reader to see only
/// the text, in unicode.
///
/// Nothing here consumed it, so the prefix was printed verbatim as part of the line.
/// </summary>
public readonly record struct SpeechFormat(ushort Hue, ushort Font, ushort UnicodeMode, string Text, bool Drop);

public static class SpeechPrefix
{
    /// <summary>FONT_NORMAL (uofiles_enums.h:400) - the filled block letters the
    /// client draws by default.</summary>
    public const ushort FontNormal = 3;

    /// <summary>FONT_QTY: one past the last real font. Upstream falls back to
    /// FONT_NORMAL for anything above it (CClientMsg.cpp:828).</summary>
    public const ushort FontQty = 10;

    /// <summary>Split a line into its format prefix and its text.
    ///
    /// The defaults passed in are what an unspecified field keeps: upstream seeds the
    /// three slots from the caller's arguments and only overwrites the ones the prefix
    /// actually names, which is what makes <c>@,,1</c> mean "just switch to unicode".
    ///
    /// <c>@@</c> is an escape for a literal <c>@</c>, and a prefix with no space after
    /// it drops the line entirely - upstream returns without saying anything
    /// (CClientMsg.cpp:806), so a malformed prefix is silence rather than a line with
    /// the prefix showing.</summary>
    public static SpeechFormat Parse(string? raw, ushort defaultHue = 0,
        ushort defaultFont = FontNormal, ushort defaultUnicode = 0)
    {
        string text = raw ?? "";
        if (text.Length == 0 || text[0] != '@')
            return new SpeechFormat(defaultHue, defaultFont, defaultUnicode, text, Drop: false);

        if (text.Length > 1 && text[1] == '@')
            return new SpeechFormat(defaultHue, defaultFont, defaultUnicode, text[1..], Drop: false);

        int sp = text.IndexOf(' ', 1);
        if (sp < 0)
            return new SpeechFormat(defaultHue, defaultFont, defaultUnicode, "", Drop: true);

        var slots = new ushort[] { defaultHue, defaultFont, defaultUnicode };
        string spec = text[1..sp];
        int pos = 0;
        for (int i = 0; i < 3 && pos < spec.Length; )
        {
            if (spec[pos] == ',')
            {
                // An empty field means "leave this one alone".
                i++; pos++;
                continue;
            }
            int end = pos;
            while (end < spec.Length && spec[end] != ',')
                end++;
            slots[i] = (ushort)ObjBase.ParseHexOrDecUInt(spec[pos..end]);
            i++;
            if (end < spec.Length && spec[end] == ',')
                pos = end + 1;
            else
                break;      // a field that does not end in a comma ends the prefix
        }

        if (slots[1] > FontQty)
            slots[1] = FontNormal;

        // Exactly one space is consumed, as upstream does - the rest of the line is
        // the text the script wrote, leading spaces and all.
        return new SpeechFormat(slots[0], slots[1], slots[2], text[(sp + 1)..], Drop: false);
    }
}
