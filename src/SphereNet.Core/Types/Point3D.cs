using System.Diagnostics.CodeAnalysis;
using SphereNet.Core.Enums;

namespace SphereNet.Core.Types;

/// <summary>
/// 3D world point with map index. Maps to CPointMap in Source-X.
/// </summary>
public readonly struct Point3D : IEquatable<Point3D>
{
    public static readonly Point3D Zero = new(0, 0, 0, 0);

    public short X { get; }
    public short Y { get; }
    public sbyte Z { get; }
    public byte Map { get; }

    public Point3D(short x, short y, sbyte z = 0, byte map = 0)
    {
        X = x;
        Y = y;
        Z = z;
        Map = map;
    }

    public Point3D WithZ(sbyte z) => new(X, Y, z, Map);
    public Point3D WithMap(byte map) => new(X, Y, Z, map);

    public int GetDistanceTo(Point3D other)
    {
        int dx = Math.Abs(X - other.X);
        int dy = Math.Abs(Y - other.Y);
        return Math.Max(dx, dy);
    }

    public Direction GetDirectionTo(Point3D other)
    {
        int dx = other.X - X;
        int dy = other.Y - Y;

        int ax = Math.Abs(dx);
        int ay = Math.Abs(dy);

        if (ay > ax)
        {
            if (ax == 0)
                return dy > 0 ? Direction.South : Direction.North;

            int slope = ay / ax;
            if (slope > 2)
                return dy > 0 ? Direction.South : Direction.North;

            return (dy > 0)
                ? (dx > 0 ? Direction.SouthEast : Direction.SouthWest)
                : (dx > 0 ? Direction.NorthEast : Direction.NorthWest);
        }

        if (ay == 0)
            return dx > 0 ? Direction.East : Direction.West;

        int slopeX = ax / ay;
        if (slopeX > 2)
            return dx > 0 ? Direction.East : Direction.West;

        return (dx > 0)
            ? (dy > 0 ? Direction.SouthEast : Direction.NorthEast)
            : (dy > 0 ? Direction.SouthWest : Direction.NorthWest);
    }

    /// <summary>
    /// Calculates sector index for given sector size.
    /// </summary>
    public (int SectorX, int SectorY) GetSector(int sectorSize)
    {
        return (X / sectorSize, Y / sectorSize);
    }

    public bool Equals(Point3D other) =>
        X == other.X && Y == other.Y && Z == other.Z && Map == other.Map;

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is Point3D p && Equals(p);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z, Map);

    public static bool operator ==(Point3D left, Point3D right) => left.Equals(right);
    public static bool operator !=(Point3D left, Point3D right) => !left.Equals(right);

    public override string ToString() => $"{X},{Y},{Z},{Map}";

    /// <summary>Split a written point into its components the way Sphere does.
    ///
    /// A coordinate list separates on COMMA, SPACE and TAB, not on comma alone
    /// (Source-X CPointBase::Read passes " ,	" to Str_ParseCmds). Packs rely on it:
    /// one AREADEF in the live map file reads <c>P=1978 2080,0,0</c>, and a
    /// comma-only split hands "1978 2080" to the number parser, fails, and leaves
    /// the region with no P at all - which a script then reads back as 0,0,0,0 and
    /// teleports a player to the corner of the map.
    ///
    /// Whitespace RUNS collapse and whitespace around a comma is absorbed, because
    /// Str_Parse skips leading whitespace before each argument; repeated COMMAS do
    /// not collapse, so "100,,5" still yields an empty middle component the way it
    /// does upstream.</summary>
    public static string[] SplitComponents(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var sb = new System.Text.StringBuilder(text.Length);
        bool pendingSeparator = false, wroteAny = false, lastWasComma = false;
        foreach (char c in text)
        {
            if (c == ' ' || c == '	')
            {
                // A run of whitespace is one separator at most, and leading
                // whitespace is none at all.
                if (wroteAny) pendingSeparator = true;
                continue;
            }
            if (c == ',')
            {
                // The comma is the separator; whitespace on either side of it is
                // absorbed rather than counted a second time.
                sb.Append(',');
                pendingSeparator = false;
                lastWasComma = true;
                wroteAny = true;
                continue;
            }
            if (pendingSeparator && !lastWasComma)
                sb.Append(',');
            pendingSeparator = false;
            lastWasComma = false;
            sb.Append(c);
            wroteAny = true;
        }
        return sb.ToString().Split(',');
    }

    public static bool TryParse(ReadOnlySpan<char> text, out Point3D result)
    {
        result = Zero;
        // A point written with spaces or tabs ("1500 1620 5") is read the same way
        // (CPointBase::Read, " ,\t"); the comma form stays on the allocation-free path
        // the save loader uses.
        if (text.IndexOfAny(' ', '\t') >= 0)
            return TryParse(string.Join(',', SplitComponents(text.ToString())).AsSpan(), out result);
        Span<Range> ranges = stackalloc Range[4];
        int count = text.Split(ranges, ',');
        if (count < 2) return false;

        if (!short.TryParse(text[ranges[0]], out short x)) return false;
        if (!short.TryParse(text[ranges[1]], out short y)) return false;

        sbyte z = 0;
        byte map = 0;
        if (count >= 3) sbyte.TryParse(text[ranges[2]], out z);
        if (count >= 4) byte.TryParse(text[ranges[3]], out map);

        result = new Point3D(x, y, z, map);
        return true;
    }
}
