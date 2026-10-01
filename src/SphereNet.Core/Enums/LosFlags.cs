namespace SphereNet.Core.Enums;

/// <summary>
/// Line-of-sight modifiers, bit for bit the Source-X LOS_* flags (CChar.h:424-436)
/// that CChar::CanSeeLOS_New reads (CCharLOS.cpp:239-643). They shape only the
/// eye-height ray of ADVANCEDLOS; the legacy walk ignores every one of them
/// (CCharLOS.cpp:12-96), exactly as upstream does. Script CANSEELOSFLAG passes the
/// number straight through, so the values must stay the reference's.
/// </summary>
[System.Flags]
public enum LosFlags : ushort
{
    None = 0,
    /// <summary>LOS_NB_LOCAL_TERRAIN: terrain inside the region the viewer stands in does not block.</summary>
    NbLocalTerrain = 0x0001,
    /// <summary>LOS_NB_LOCAL_STATIC: statics inside the viewer's region do not block.</summary>
    NbLocalStatic = 0x0002,
    /// <summary>LOS_NB_LOCAL_DYNAMIC: world items inside the viewer's region do not block.</summary>
    NbLocalDynamic = 0x0004,
    /// <summary>LOS_NB_LOCAL_MULTI: multi components inside the viewer's region do not block.</summary>
    NbLocalMulti = 0x0008,
    /// <summary>LOS_NB_TERRAIN: terrain never blocks.</summary>
    NbTerrain = 0x0010,
    /// <summary>LOS_NB_STATIC: statics never block.</summary>
    NbStatic = 0x0020,
    /// <summary>LOS_NB_DYNAMIC: world items never block.</summary>
    NbDynamic = 0x0040,
    /// <summary>LOS_NB_MULTI: multi components never block.</summary>
    NbMulti = 0x0080,
    /// <summary>LOS_NB_WINDOWS: a window tile (UFLAG2_WINDOW) does not block
    /// (ranged combat, spell casting, crime witnessing pass it).</summary>
    NbWindows = 0x0100,
    /// <summary>LOS_NO_OTHER_REGION: the ray may not leave the viewer's region.</summary>
    NoOtherRegion = 0x0200,
    /// <summary>LOS_NC_MULTI: the ray may not cross a multi region other than the viewer's own.</summary>
    NcMulti = 0x0400,
    /// <summary>LOS_FISHING: two or more tiles out, terrain must be water or untyped
    /// and nothing solid that is not water may stand in the way.</summary>
    Fishing = 0x0800,
    /// <summary>LOS_NC_WATER. Upstream's handling marks a water terrain tile as a null
    /// tile, which blocks unless an item stands on it (CCharLOS.cpp:323-324).</summary>
    NcWater = 0x1000,
}
