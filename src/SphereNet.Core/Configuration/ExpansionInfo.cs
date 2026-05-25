using SphereNet.Core.Enums;

namespace SphereNet.Core.Configuration;

public sealed class ExpansionInfo
{
    public Expansion Id { get; }
    public string Name { get; }
    public FeatureFlags ClientFlags { get; }
    public CharacterListFlags CharListFlags { get; }
    public int MobileStatusVersion { get; }

    private ExpansionInfo(Expansion id, string name, FeatureFlags clientFlags,
        CharacterListFlags charListFlags, int mobileStatusVersion)
    {
        Id = id;
        Name = name;
        ClientFlags = clientFlags;
        CharListFlags = charListFlags;
        MobileStatusVersion = mobileStatusVersion;
    }

    private static readonly ExpansionInfo[] Table =
    [
        new(Expansion.None, "Pre-T2A",   FeatureFlags.ExpansionNone, CharacterListFlags.ExpansionNone, 0),
        new(Expansion.T2A,  "T2A",       FeatureFlags.ExpansionT2A,  CharacterListFlags.ExpansionT2A,  0),
        new(Expansion.UOR,  "UOR",       FeatureFlags.ExpansionUOR,  CharacterListFlags.ExpansionUOR,  0),
        new(Expansion.UOTD, "UOTD",      FeatureFlags.ExpansionUOTD, CharacterListFlags.ExpansionUOTD, 0),
        new(Expansion.LBR,  "LBR",       FeatureFlags.ExpansionLBR,  CharacterListFlags.ExpansionLBR,  0),
        new(Expansion.AOS,  "AOS",       FeatureFlags.ExpansionAOS,  CharacterListFlags.ExpansionAOS,  2),
        new(Expansion.SE,   "SE",        FeatureFlags.ExpansionSE,   CharacterListFlags.ExpansionSE,   4),
        new(Expansion.ML,   "ML",        FeatureFlags.ExpansionML,   CharacterListFlags.ExpansionML,   5),
        new(Expansion.SA,   "SA",        FeatureFlags.ExpansionSA,   CharacterListFlags.ExpansionSA,   6),
        new(Expansion.HS,   "HS",        FeatureFlags.ExpansionHS,   CharacterListFlags.ExpansionHS,   6),
        new(Expansion.TOL,  "TOL",       FeatureFlags.ExpansionTOL,  CharacterListFlags.ExpansionTOL,  6),
        new(Expansion.EJ,   "EJ",        FeatureFlags.ExpansionEJ,   CharacterListFlags.ExpansionEJ,   6),
    ];

    public static ExpansionInfo GetInfo(Expansion expansion)
    {
        int idx = (int)expansion;
        if (idx < 0 || idx >= Table.Length)
            idx = 0;
        return Table[idx];
    }
}
