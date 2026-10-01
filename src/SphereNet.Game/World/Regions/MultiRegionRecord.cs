using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.World.Regions;

/// <summary>
/// A structure's region as its multi record carries it. Upstream writes the LIVE
/// region of a house or ship into the multi's own record, its keys prefixed
/// "REGION." (CItemMulti::r_Write -> CRegion::r_WriteBody, CItemMulti.cpp:2564 /
/// CRegion.cpp:623): REGION.FLAGS as Sphere hex when any are set, one REGION.EVENTS
/// line naming the event list, and a REGION.TAG.&lt;name&gt; line per tag. Reading the
/// record hands each REGION.&lt;key&gt; line back to the region (SHL_REGION,
/// CItemMulti.cpp:3011). The item keeps the lines as tags of the same name between
/// a load and the region's realization, and again just before a save.
/// </summary>
public static class MultiRegionRecord
{
    public const string FlagsKey = "REGION.FLAGS";
    public const string EventsKey = "REGION.EVENTS";
    public const string TagPrefix = "REGION.TAG.";

    /// <summary>Put the live region's state on the multi item in the keys the record
    /// is written with. The flags are the region's OWN: the ones it took over from the
    /// land around it are the land's, and the engine's inherit marker is not a flag
    /// upstream sets on a structure.</summary>
    public static void Store(Item multi, Region region)
    {
        uint flags = (uint)(region.OwnFlags & ~RegionFlag.InheritParentFlags);
        if (flags != 0)
            multi.SetTag(FlagsKey, $"0{flags:X}");
        else
            multi.RemoveTag(FlagsKey);

        var names = new List<string>();
        foreach (var rid in region.Events)
        {
            if (region.EventName(rid) is { Length: > 0 } name)
                names.Add(name);
        }
        if (names.Count > 0)
            multi.SetTag(EventsKey, string.Join(",", names));
        else
            multi.RemoveTag(EventsKey);

        foreach (var (key, _) in multi.Tags.GetAll().ToList())
        {
            if (key.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
                multi.RemoveTag(key);
        }
        foreach (var (key, value) in region.TagEntries)
            multi.SetTag(TagPrefix + key, value);
    }

    /// <summary>Give a freshly realized region what the multi's record says of it.
    /// A record's flags are added to the base the realization starts from: a record
    /// upstream wrote always carries that base, so for it this is the same as taking
    /// the value, and an older record that lacked a base bit does not strip it. The
    /// ship bit is never taken from a record (CRegion::r_LoadVal RC_FLAGS,
    /// CRegion.cpp:548).</summary>
    public static void Apply(Item multi, Region region)
    {
        if (multi.TryGetTag(EventsKey, out string? events))
            region.LoadEventsValue(events);
        if (multi.TryGetTag(FlagsKey, out string? flags) &&
            ScriptNumber.TryParseToken(flags, out long bits) && bits > 0)
            region.AddFlags((RegionFlag)(uint)bits & ~RegionFlag.Ship);
        foreach (var (key, value) in multi.Tags.GetAll())
        {
            if (key.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
                region.SetTag(key[TagPrefix.Length..], value);
        }
    }
}
