using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Execution;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A region's own script runs ON THE REGION.
///
/// Upstream calls OnTriggerScript from the region itself and hands it the character's
/// console (CRegion::OnRegionTrigger, CRegion.cpp:882), so inside a region @Enter the
/// object is the REGION - its TAG, its NAME - and the walker is SRC. Running it on the
/// character instead answered every region-level read from the wrong object, which
/// matters most for the very tag a house region carries: TAG.owner.
/// </summary>
public sealed class RegionTriggerObjectParityTests
{
    private const string Script = """
        [EVENTS r_test_region]
        ON=@Enter
        TAG.SEEN_OWNER=<TAG.owner>
        TAG.SEEN_NAME=<NAME>
        TAG.SEEN_SRC=<SRC.NAME>
        """;

    private static (TriggerDispatcher Dispatcher, ResourceHolder Resources) Bench()
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_rgn_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, Script);
        try { resources.LoadResourceFile(path); }
        finally { File.Delete(path); }

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher
        {
            Resources = resources,
            Runner = runner,
        };
        return (dispatcher, resources);
    }

    [Fact]
    public void ARegionsOwnScriptReadsTheRegionsTagsAndTheWalkerIsSrc()
    {
        var (dispatcher, _) = Bench();

        var region = new Region { Name = "Saints keep" };
        region.AddRect(50, 50, 70, 70);
        region.SetTag("OWNER", "09191");
        region.AddEventsFromTag("r_test_region");

        var walker = new Character { Name = "Passerby" };
        walker.SetTag("OWNER", "WRONG");     // the character has its own tag of that name

        dispatcher.FireRegionEvents(region, "Enter", walker,
            new TriggerArgs { CharSrc = walker, S1 = region.Name });

        // The region answered, not the character.
        Assert.True(region.TryGetTag("SEEN_OWNER", out string? seenOwner));
        Assert.Equal("09191", seenOwner);
        Assert.True(region.TryGetTag("SEEN_NAME", out string? seenName));
        Assert.Equal("Saints keep", seenName);
        // ...and the walker is SRC.
        Assert.True(region.TryGetTag("SEEN_SRC", out string? seenSrc));
        Assert.Equal("Passerby", seenSrc);

        // Nothing was written onto the character, and its own tag is untouched.
        Assert.False(walker.TryGetTag("SEEN_OWNER", out _));
        Assert.True(walker.TryGetTag("OWNER", out string? walkerOwner));
        Assert.Equal("WRONG", walkerOwner);
    }
}
