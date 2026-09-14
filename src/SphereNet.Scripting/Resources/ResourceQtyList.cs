namespace SphereNet.Scripting.Resources;

/// <summary>One entry of a resource list: what is wanted, and how much of it.</summary>
/// <param name="Name">The resource as written - a defname, a skill name, a number.</param>
/// <param name="Quantity">The amount, already through Sphere's numeric rule.</param>
/// <param name="HasExplicitQuantity">Whether the line said so, or the default of one
/// was supplied. A script asking for RESOURCES.n.VAL sees what was written.</param>
public readonly record struct ResourceQtyEntry(string Name, long Quantity, bool HasExplicitQuantity);

/// <summary>
/// The grammar every resource list in a script pack is written in - RESOURCES,
/// SKILLMAKE, AVERSIONS, a SKILLTEST argument - read once, in one place.
///
/// Upstream is CResourceQty::Load (CResourceQty.cpp:55), and its first comment is the
/// part that matters: <c>"Can be either order.: Name Qty or Qty Name"</c>. A leading
/// non-alpha token is the quantity and the name follows; otherwise the name comes
/// first and a quantity may trail it; a bare name means one.
///
/// This engine had four half-readers of it instead. Each accepted one order and
/// quietly dropped the other, which is the worst way to be wrong about a grammar: the
/// recipe still loads, the item is still craftable, and one requirement has silently
/// disappeared. In the shipped pack that cost <c>SKILLMAKE=...,1 i_pen_and_ink,...</c>
/// (scribing needed no pen) and 257 bare-name <c>RESOURCES</c> entries such as
/// <c>RESOURCES=i_spellbook</c> (the spellbook was never consumed).
/// </summary>
public static class ResourceQtyList
{
    /// <summary>Split a comma-separated resource list into its entries. Entries that
    /// name nothing are dropped, the way upstream drops them with a log line.</summary>
    public static List<ResourceQtyEntry> Parse(string? raw)
    {
        var list = new List<ResourceQtyEntry>();
        if (string.IsNullOrWhiteSpace(raw))
            return list;

        foreach (string part in raw.Split(','))
        {
            var entry = ParseEntry(part);
            if (entry.Name.Length > 0)
                list.Add(entry);
        }
        return list;
    }

    /// <summary>Read one entry. <paramref name="text"/> is a single comma-free element.</summary>
    public static ResourceQtyEntry ParseEntry(string? text)
    {
        string s = (text ?? "").Trim();
        if (s.Length == 0)
            return new ResourceQtyEntry("", 1, false);

        long qty = 1;
        bool explicitQty = false;

        // "Qty Name": upstream tests IsAlpha, so a name may not begin with a digit but
        // may begin with '_' - and '{' or '.' start a quantity, not a name.
        if (!char.IsLetter(s[0]))
        {
            int end = ReadNumber(s, 0, out qty);
            if (end == 0)
                return new ResourceQtyEntry("", 1, false);   // unreadable: no name either
            explicitQty = true;
            s = s[end..].TrimStart();
            if (s.Length == 0)
                return new ResourceQtyEntry("", 1, false);
        }

        // The name is ONE symbol - upstream eats it with ResourceGetID_EatStr - so
        // whatever follows the token is the trailing quantity, not part of the name.
        int nameEnd = 0;
        while (nameEnd < s.Length && (char.IsLetterOrDigit(s[nameEnd]) || s[nameEnd] == '_'))
            nameEnd++;
        string name = s[..nameEnd];
        if (name.Length == 0)
            return new ResourceQtyEntry("", 1, false);

        // "Name Qty", or nothing at all - and nothing means one (CResourceQty.cpp:88).
        if (!explicitQty)
        {
            string rest = s[nameEnd..].TrimStart();
            if (rest.Length > 0 && ReadNumber(rest, 0, out long trailing) > 0)
            {
                qty = trailing;
                explicitQty = true;
            }
        }

        return new ResourceQtyEntry(name, qty, explicitQty);
    }

    /// <summary>A skill requirement's quantity is a skill value in tenths: the pack
    /// writes <c>50.0</c> and means 50.0 skill, which upstream reads as 500 because its
    /// decimal path IGNORES the dot. The same rule makes a bare <c>76</c> mean 7.6, not
    /// 76 - reading it as 76.0 raised a colored-armor recipe's requirement tenfold.</summary>
    public static int SkillValue(in ResourceQtyEntry entry) =>
        (int)Math.Clamp(entry.Quantity, 0, 1000);

    /// <summary>
    /// One Sphere numeric token, returning how many characters it used (0 = none).
    ///
    /// This is Exp_GetVal's number path (CExpression.cpp:660-781) and it is NOT
    /// <see cref="SphereNet.Core.Types.ScriptNumber.TryParseToken"/>: that one reads an
    /// id or a flag, where a dot is meaningless and rejected. Here a dot is a GROUPING
    /// separator that the reference skips, so <c>50.0</c> is five hundred. A leading
    /// '0' still marks hexadecimal - but only when a dot does not follow it, or
    /// <c>0.15</c> would read as 0x15.
    /// </summary>
    private static int ReadNumber(string s, int start, out long value)
    {
        value = 0;
        int i = start;
        if (i < s.Length && s[i] == '.')   // legacy leading dot
            i++;
        if (i >= s.Length)
            return 0;

        if (s[i] == '0' && (i + 1 >= s.Length || s[i + 1] != '.'))
        {
            // Hex: the '0' is the base marker, an 'x' after it is optional.
            i++;
            if (i < s.Length && (s[i] == 'x' || s[i] == 'X'))
                i++;
            int hexStart = i;
            ulong acc = 0;
            while (i < s.Length && Uri.IsHexDigit(s[i]))
            {
                acc = (acc << 4) | (uint)Convert.ToInt32(s[i].ToString(), 16);
                i++;
            }
            value = (long)acc;
            // "0" on its own is a valid zero; "0abc" stops at the first non-hex char.
            return i > hexStart || i > start ? i - start : 0;
        }

        if (!char.IsAsciiDigit(s[i]))
            return 0;

        long dec = 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
        {
            if (s[i] != '.')
                dec = dec * 10 + (s[i] - '0');
            i++;
        }
        // A trailing dot is not part of the number ("i_x 5." is unusual but legal).
        while (i > start && s[i - 1] == '.')
            i--;
        value = dec;
        return i - start;
    }
}
