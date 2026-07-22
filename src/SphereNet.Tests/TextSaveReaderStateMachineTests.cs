using System.Collections.Generic;
using System.IO;
using System.Text;
using SphereNet.Persistence.Formats;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 17 / M7 — TextSaveReader.NextRecord checked its stashed-header state before
/// draining the previous record. An early-exit caller (one that stopped reading a
/// record's properties, or read none) made the drain stash the NEXT header, but
/// the stash was never re-consulted that call — so a whole section was skipped and
/// the stale header surfaced out of order later. NextRecord now drains first, then
/// consumes the single stashed header, then reads forward.
/// </summary>
public sealed class TextSaveReaderStateMachineTests
{
    private static TextSaveReader Reader(string content) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static List<string> AllSectionsWithoutReadingProps(string content)
    {
        using var r = Reader(content);
        var sections = new List<string>();
        while (r.NextRecord(out var s))
            sections.Add(s);
        return sections;
    }

    [Fact]
    public void ConsecutiveNextRecord_WithoutReadingProperties_LosesNoSection()
    {
        const string content = "[A]\nK1=v1\n[B]\nK2=v2\n[C]\nK3=v3\n[EOF]\n";
        Assert.Equal(new[] { "A", "B", "C" }, AllSectionsWithoutReadingProps(content));
    }

    [Fact]
    public void ReadFirstPropertyThenNextRecord_ReturnsTheCorrectNextSection()
    {
        const string content = "[A]\nK1=v1\nK2=v2\n[B]\nK3=v3\n[EOF]\n";
        using var r = Reader(content);

        Assert.True(r.NextRecord(out var a));
        Assert.Equal("A", a);
        Assert.True(r.NextProperty(out var k1, out var v1));
        Assert.Equal(("K1", "v1"), (k1, v1));

        // Stopped after K1 (K2 unread) — the next record must be B, not C or lost.
        Assert.True(r.NextRecord(out var b));
        Assert.Equal("B", b);
        Assert.True(r.NextProperty(out var k3, out var v3));
        Assert.Equal(("K3", "v3"), (k3, v3));

        Assert.False(r.NextRecord(out _));
    }

    [Fact]
    public void FullyDrainedRecords_StillReadInOrder()
    {
        // Regression: the common caller that drains every record must be unaffected.
        const string content = "[A]\nK1=v1\n[B]\nK2=v2\n[EOF]\n";
        using var r = Reader(content);

        Assert.True(r.NextRecord(out var a));
        Assert.Equal("A", a);
        Assert.True(r.NextProperty(out var k1, out _));
        Assert.Equal("K1", k1);
        Assert.False(r.NextProperty(out _, out _)); // reaches [B]

        Assert.True(r.NextRecord(out var b));
        Assert.Equal("B", b);
        Assert.True(r.NextProperty(out var k2, out _));
        Assert.Equal("K2", k2);
        Assert.False(r.NextProperty(out _, out _));

        Assert.False(r.NextRecord(out _));
    }

    [Fact]
    public void RecordBeforeEof_DrainedEarly_EndsCleanly()
    {
        const string content = "[A]\nK1=v1\n[EOF]\n";
        using var r = Reader(content);

        Assert.True(r.NextRecord(out var a));
        Assert.Equal("A", a);
        // No properties read: draining A hits [EOF], so there is no further record.
        Assert.False(r.NextRecord(out _));
    }

    [Fact]
    public void SectionAfterEof_IsStillRead()
    {
        // [EOF] ends a record but does not stop the stream — a trailing section is
        // still surfaced (preserves the classic reader behaviour).
        const string content = "[A]\nK1=v1\n[EOF]\n[B]\nK2=v2\n";
        Assert.Equal(new[] { "A", "B" }, AllSectionsWithoutReadingProps(content));
    }
}
