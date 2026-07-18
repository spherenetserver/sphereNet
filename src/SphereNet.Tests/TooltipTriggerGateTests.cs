using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// V1 (perf): the AOS tooltip built for every object entering a client's view now gates
/// its @ClientTooltip / @ClientTooltipAfterDefault fires behind IsTrigUsed, like the
/// single-click path. These guard the gate keys: a script that hooks the trigger MUST
/// still report it used (otherwise gating would silently drop script tooltips), and an
/// unhooked trigger MUST report unused (so the expensive per-object fire is skipped).
/// </summary>
public sealed class TooltipTriggerGateTests
{
    [Fact]
    public void IsCharTriggerUsed_ClientTooltip_TracksHandler()
    {
        var d = new TriggerDispatcher();
        Assert.False(d.IsCharTriggerUsed(CharTrigger.ClientTooltip)); // unhooked → gate skips

        d.RegisterCharEvent("EVENTS", "ClientTooltip", (_, _) => TriggerResult.Default);
        Assert.True(d.IsCharTriggerUsed(CharTrigger.ClientTooltip)); // hooked → gate fires
    }

    [Fact]
    public void IsItemTriggerUsed_ClientTooltipVariants_TrackHandlers()
    {
        var d = new TriggerDispatcher();
        Assert.False(d.IsItemTriggerUsed(ItemTrigger.ClientTooltip));
        Assert.False(d.IsItemTriggerUsed(ItemTrigger.ClientTooltipAfterDefault));

        d.RegisterItemEvent("EVENTSITEM", "ClientTooltip", (_, _) => TriggerResult.Default);
        d.RegisterItemEvent("EVENTSITEM", "ClientTooltipAfterDefault", (_, _) => TriggerResult.Default);
        Assert.True(d.IsItemTriggerUsed(ItemTrigger.ClientTooltip));
        Assert.True(d.IsItemTriggerUsed(ItemTrigger.ClientTooltipAfterDefault));
    }

    [Fact]
    public void BuildUsedTriggerCache_ScriptOnBlock_MarksClientTooltipUsed()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_tt_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_tooltip_gate_test]
            ON=@ClientTooltip
            RETURN 0
            """);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
            {
                ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
            };
            resources.LoadResourceFile(tempFile);

            var d = new TriggerDispatcher { Resources = resources };
            Assert.False(d.IsCharTriggerUsed(CharTrigger.ClientTooltip));
            d.BuildUsedTriggerCache();
            Assert.True(d.IsCharTriggerUsed(CharTrigger.ClientTooltip));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
