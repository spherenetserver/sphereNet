namespace SphereNet.Core.Types;

/// <summary>
/// Character creation appearance data parsed from 0xF8 (HS) or 0x00 (old) packets.
/// </summary>
public sealed class CharCreateInfo
{
    public string Name { get; init; } = "";
    public bool Female { get; init; }
    public byte Str { get; init; }
    public byte Dex { get; init; }
    public byte Int { get; init; }
    public ushort SkinHue { get; init; }
    public ushort HairStyle { get; init; }
    public ushort HairHue { get; init; }
    public ushort BeardStyle { get; init; }
    public ushort BeardHue { get; init; }
    public (byte Id, byte Value)[] Skills { get; init; } = [];

    /// <summary>Index of the starting city the player picked, into the city list
    /// sent in the 0xA9 character list. Drives the new character's spawn point.</summary>
    public byte City { get; init; }
}
