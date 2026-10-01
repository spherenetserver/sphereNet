namespace SphereNet.Core.Types;

/// <summary>How a script line ends a block section.</summary>
public enum ScriptBlockTerminator : byte
{
    /// <summary>Not a terminator.</summary>
    None,
    /// <summary>END, ENDDO, ENDFOR, ENDIF, ENDRAND, ENDSWITCH or ENDWHILE.</summary>
    End,
    /// <summary>ELSE, ELIF or ELSEIF.</summary>
    Else,
}

/// <summary>
/// Block structure of the script language - one definition shared by the interpreter
/// and the panel's script validator, so the two cannot disagree about where a block
/// ends.
///
/// Source-X CScriptObj::OnTriggerRun ends a section run on ANY of END, ENDDO,
/// ENDFOR, ENDIF, ENDRAND, ENDSWITCH and ENDWHILE - each returns TRIGRET_ENDIF - so
/// the seven are interchangeable: an ENDRAND closes a FOR and an END closes an IF.
/// ELSE / ELIF / ELSEIF end a section as well (TRIGRET_ELSE / TRIGRET_ELSEIF).
/// Nesting is counted by the statements that open a section - IF, BEGIN, DORAND,
/// DOSWITCH, WHILE, FOR and the object loops - never by pairing keyword names.
///
/// Keys passed in are upper-case statement keywords.
/// </summary>
public static class ScriptBlockSyntax
{
    public static ScriptBlockTerminator GetTerminator(string key) => key switch
    {
        "END" or "ENDDO" or "ENDFOR" or "ENDIF" or "ENDRAND" or "ENDSWITCH" or "ENDWHILE"
            => ScriptBlockTerminator.End,
        "ELSE" or "ELIF" or "ELSEIF" => ScriptBlockTerminator.Else,
        _ => ScriptBlockTerminator.None,
    };

    /// <summary>The loops that walk objects rather than a number range.</summary>
    public static bool IsObjectLoop(string key) =>
        key is "FORPLAYERS" or "FORCHARS" or "FORITEMS" or "FORCLIENTS"
            or "FOROBJS" or "FORINSTANCES" or "FORCONT" or "FORCONTID" or "FORCONTTYPE"
            or "FORCHARLAYER" or "FORCHARMEMORYTYPE" or "FORTIMERF";

    /// <summary>Statements whose block runs to the next terminator at their depth.</summary>
    public static bool OpensBlock(string key) =>
        key is "IF" or "BEGIN" or "DORAND" or "DOSWITCH" or "WHILE" or "FOR" || IsObjectLoop(key);

    /// <summary>Index of the terminator that ends the section starting at
    /// <paramref name="start"/>, or lines.Count when the section is never closed.</summary>
    public static int FindSectionEnd<T>(IReadOnlyList<T> lines, int start, Func<T, string> keyOf,
        Action<int, int>? onBlock = null)
    {
        int i = start;
        while (i < lines.Count)
        {
            if (GetTerminator(keyOf(lines[i])) != ScriptBlockTerminator.None)
                return i;
            i = SkipStatement(lines, i, keyOf, onBlock);
        }
        return lines.Count;
    }

    /// <summary>Index after the one statement at <paramref name="idx"/>, without
    /// running it - Source-X's TRIGRUN_SINGLE_FALSE. An IF is skipped through each of
    /// its ELSE / ELSEIF sections up to its end; any other block opener is skipped up
    /// to and including whichever terminator ends its section.
    ///
    /// <paramref name="onBlock"/>, when given, is told (opener index, index of the
    /// terminator that ended the block - or lines.Count when nothing did) for every
    /// block met on the way; the validator reports from it.</summary>
    public static int SkipStatement<T>(IReadOnlyList<T> lines, int idx, Func<T, string> keyOf,
        Action<int, int>? onBlock = null)
    {
        string key = keyOf(lines[idx]);
        if (key == "IF")
        {
            int end = FindSectionEnd(lines, idx + 1, keyOf, onBlock);
            while (end < lines.Count && GetTerminator(keyOf(lines[end])) == ScriptBlockTerminator.Else)
                end = FindSectionEnd(lines, end + 1, keyOf, onBlock);
            onBlock?.Invoke(idx, end);
            return Math.Min(end + 1, lines.Count);
        }
        if (OpensBlock(key))
        {
            int end = FindSectionEnd(lines, idx + 1, keyOf, onBlock);
            onBlock?.Invoke(idx, end);
            return Math.Min(end + 1, lines.Count);
        }
        return idx + 1;
    }
}
