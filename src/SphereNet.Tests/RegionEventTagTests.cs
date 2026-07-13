using System.Linq;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A dynamically realized house/ship region carries its @Enter/@Step scripts only
/// through the multi item's REGION.EVENTS tag. Region.AddEventsFromTag must turn
/// that tag into exactly the ResourceIds that TriggerDispatcher.FireRegionEvents
/// looks up (ResourceId.FromString(name, ResType.Events)), so the events fire.
/// </summary>
public sealed class RegionEventTagTests
{
    [Fact]
    public void AddEventsFromTag_ProducesFireRegionEventsLookupKeys()
    {
        var region = new Region();
        region.AddEventsFromTag("e_house_enter,e_house_step");

        Assert.Equal(2, region.Events.Count);
        // The exact key FireRegionEvents resolves via Resources.GetResource(eventRid).
        Assert.Contains(ResourceId.FromString("e_house_enter", ResType.Events), region.Events);
        Assert.Contains(ResourceId.FromString("e_house_step", ResType.Events), region.Events);
    }

    [Fact]
    public void AddEventsFromTag_TrimsPlusPrefix_AndDeduplicates()
    {
        var region = new Region();
        region.AddEventsFromTag("+e_ship_enter, e_ship_enter ,+e_ship_deck");

        // '+' trimmed, duplicate collapsed -> two distinct events.
        Assert.Equal(2, region.Events.Count);
        Assert.Contains(ResourceId.FromString("e_ship_enter", ResType.Events), region.Events);
        Assert.Contains(ResourceId.FromString("e_ship_deck", ResType.Events), region.Events);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",, ,")]
    public void AddEventsFromTag_IgnoresBlankInput(string? csv)
    {
        var region = new Region();
        region.AddEventsFromTag(csv);
        Assert.Empty(region.Events);
    }
}
