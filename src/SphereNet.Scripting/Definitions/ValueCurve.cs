namespace SphereNet.Scripting.Definitions;

/// <summary>
/// Skill-level value curve used by [SKILL] definitions (ADV_RATE, DELAY,
/// EFFECT). Port of the reference curve type (credit: Sphere 0.56
/// CValueCurveDef): a comma-separated list of values spread evenly across
/// skill 0.0-100.0, linearly interpolated. Script numbers use the legacy
/// fixed-point convention — the decimal point is skipped and digits
/// concatenate ("2.5" → 25, "200.0" → 2000), and a leading zero marks HEX
/// ("0480" → 0x480) unless followed by a dot.
/// </summary>
public sealed class ValueCurve
{
    public static readonly ValueCurve Empty = new(Array.Empty<int>());

    private readonly int[] _values;

    public ValueCurve(int[] values)
    {
        _values = values;
    }

    public bool IsEmpty => _values.Length == 0;
    public int Count => _values.Length;
    public int this[int index] => _values[index];

    public static ValueCurve Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Empty;

        var tokens = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return Empty;

        var values = new int[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            values[i] = ParseSphereNumber(tokens[i]);
        return new ValueCurve(values);
    }

    /// <summary>
    /// Legacy script number parse (reference ahextoi): skips a decimal point
    /// so digits concatenate, and treats a leading '0' (not followed by '.')
    /// as hex.
    /// </summary>
    public static int ParseSphereNumber(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return 0;

        int pos = 0;
        while (pos < token.Length && char.IsWhiteSpace(token[pos]))
            pos++;
        if (pos >= token.Length)
            return 0;

        bool negative = token[pos] == '-';
        if (negative)
            pos++;

        bool hex = pos < token.Length && token[pos] == '0' &&
                   (pos + 1 >= token.Length || token[pos + 1] != '.');

        int value = 0;
        for (; pos < token.Length; pos++)
        {
            char ch = char.ToUpperInvariant(token[pos]);
            int digit;
            if (ch is >= '0' and <= '9')
                digit = ch - '0';
            else if (hex && ch is >= 'A' and <= 'F')
                digit = ch - 'A' + 10;
            else if (!hex && ch == '.')
                continue;
            else
                break;

            value = value * (hex ? 16 : 10) + digit;
        }

        return negative ? -value : value;
    }

    /// <summary>
    /// Linear interpolation across the curve. <paramref name="skillPercent"/>
    /// is 0-1000 (0% to 100.0%). Exact port of the reference GetLinear.
    /// </summary>
    public int GetLinear(int skillPercent)
    {
        skillPercent = Math.Clamp(skillPercent, 0, 1000);

        int loIdx;
        long segSize;
        int count = _values.Length;
        switch (count)
        {
            case 0:
                return 0;
            case 1:
                return _values[0];
            case 2:
                loIdx = 0;
                segSize = 1000;
                break;
            case 3:
                if (skillPercent >= 500)
                {
                    loIdx = 1;
                    skillPercent -= 500;
                }
                else
                {
                    loIdx = 0;
                }
                segSize = 500;
                break;
            default:
                loIdx = (int)((long)skillPercent * count / 1000);
                count--;
                if (loIdx >= count)
                    loIdx = count - 1;
                segSize = 1000 / count;
                skillPercent -= (int)(loIdx * segSize);
                break;
        }

        long loVal = _values[loIdx];
        long hiVal = _values[loIdx + 1];
        long val = loVal + (hiVal - loVal) * skillPercent / segSize;
        return (int)Math.Max(0, val);
    }

    /// <summary>
    /// ADV_RATE chance: the curve values express "skill uses per 0.1 gain"
    /// (fixed-point ×10); the gain chance per use is the inverse. Returns a
    /// per-mille chance (may exceed 1000 for trivially easy gains). Exact
    /// port of the reference GetChancePercent.
    /// </summary>
    public int GetChancePercent(int skillPercent)
    {
        int uses = GetLinear(skillPercent);
        if (uses <= 0)
            return 0;
        return 100000 / uses;
    }
}
