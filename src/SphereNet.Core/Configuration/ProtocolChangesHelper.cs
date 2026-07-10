using SphereNet.Core.Enums;

namespace SphereNet.Core.Configuration;

public static class ProtocolChangesHelper
{
    public static ProtocolChanges DetermineProtocolChanges(uint versionNumber) => versionNumber switch
    {
        >= 70_061_000 => ProtocolChanges.Version70610,
        >= 70_050_000 => ProtocolChanges.Version70500,
        >= 70_045_065 => ProtocolChanges.Version704565,
        >= 70_033_001 => ProtocolChanges.Version70331,
        >= 70_030_000 => ProtocolChanges.Version70300,
        >= 70_016_000 => ProtocolChanges.Version70160,
        >= 70_013_000 => ProtocolChanges.Version70130,
        >= 70_009_000 => ProtocolChanges.Version7090,
        >= 70_000_000 => ProtocolChanges.Version7000,
        >= 60_014_002 => ProtocolChanges.Version60142,
        >= 60_001_007 => ProtocolChanges.Version6017,
        >= 60_000_000 => ProtocolChanges.Version6000,
        >= 50_002_002 => ProtocolChanges.Version502b,
        >= 50_000_000 => ProtocolChanges.Version500a,
        _ => ProtocolChanges.None
    };
}
