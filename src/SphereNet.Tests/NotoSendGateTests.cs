using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

// Covers the IsTrigUsed gate (TriggerDispatcher.IsCharTriggerUsed +
// BuildUsedTriggerCache) and the @NotoSend override it guards. The gate must
// never report a hooked trigger as unused (that would silently drop the script),
// so it draws from registered handlers, script [ON=@X] blocks (incl. the
// cross-fired @charX mirror) and f_onchar_x functions. ComputeNotoriety only
// invokes the override hook when installed, so the per-observer hot path is a
// null check when nothing hooks @NotoSend.
public class NotoSendGateTests
{
    [Fact]
    public void IsCharTriggerUsed_RegisteredHandler_IsUsed()
    {
        var d = new TriggerDispatcher();
        d.RegisterCharEvent("EVENTSPLAYER", "NotoSend", (_, _) => TriggerResult.Default);

        Assert.True(d.IsCharTriggerUsed(CharTrigger.NotoSend));
        Assert.False(d.IsCharTriggerUsed(CharTrigger.StatChange)); // not hooked
    }

    [Fact]
    public void IsCharTriggerUsed_CrossFireMirror_IsUsed()
    {
        // A script hooking the cross-fired @charNotoSend must keep @NotoSend "used".
        var d = new TriggerDispatcher();
        d.RegisterCharEvent("EVENTSPLAYER", "charNotoSend", (_, _) => TriggerResult.Default);

        Assert.True(d.IsCharTriggerUsed(CharTrigger.NotoSend));
    }

    [Fact]
    public void BuildUsedTriggerCache_ScriptOnBlock_MarksTriggerUsed()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_noto_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_noto_gate_test]
            ON=@NotoSend
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
            Assert.False(d.IsCharTriggerUsed(CharTrigger.NotoSend)); // not scanned yet
            d.BuildUsedTriggerCache();
            Assert.True(d.IsCharTriggerUsed(CharTrigger.NotoSend));  // ON=@NotoSend found
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ComputeNotoriety_NoHook_ReturnsBaseValue()
    {
        var (world, viewer, subject) = MakeViewerAndSubject();
        // Two innocent players → base notoriety is 1 (the @NotoSend hook is null).
        Assert.Equal(1, GameClient.ComputeNotoriety(world, viewer, subject));
    }

    [Fact]
    public void ComputeNotoriety_NotoSendHook_OverridesResult()
    {
        var (world, viewer, subject) = MakeViewerAndSubject();
        Character.OnNotoSend = (_, _, _) => 6; // force "murderer" red regardless of base

        Assert.Equal(6, GameClient.ComputeNotoriety(world, viewer, subject));
    }

    private static (GameWorld world, Character viewer, Character subject) MakeViewerAndSubject()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var viewer = world.CreateCharacter();
        viewer.IsPlayer = true;
        world.PlaceCharacter(viewer, new Point3D(100, 100, 0, 0));
        var subject = world.CreateCharacter();
        subject.IsPlayer = true;
        world.PlaceCharacter(subject, new Point3D(101, 100, 0, 0));
        return (world, viewer, subject);
    }
}
