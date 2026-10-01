using SphereNet.Game.Objects;

namespace SphereNet.Tests;

/// <summary>
/// The text a script stored in a TAG, for tests that assert on the value a script
/// computed. A script READ of a number var (&lt;TAG.X&gt;) answers in Sphere hex by
/// default (Source-X CVarDefContNum::GetValStr with DECIMALVARIABLES=0), so "1"
/// reads back "01"; these helpers look at the stored value instead, and a missing
/// key reads "0" the way the old harness reads did.
/// </summary>
internal static class TagValueTestExtensions
{
    public static string TagValue(this ObjBase obj, string key) => obj.Tags.Get(key) ?? "0";

    /// <summary>A Sphere number as a script wrote it - "0A" or "10" - or null when the
    /// text is not one. For values a script copied from a number var, which it read
    /// in Sphere hex.</summary>
    public static long? SphereNum(string? text) =>
        SphereNet.Core.Types.ScriptNumber.TryParseLong(text, out long v) ? v : null;
}
