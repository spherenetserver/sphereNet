using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

// Covers the item-trigger IsTrigUsed gate (TriggerDispatcher.IsItemTriggerUsed,
// the item counterpart of IsCharTriggerUsed) and the @MemoryEquip fire it guards.
// Memory items are created frequently during combat, so @MemoryEquip is installed
// only when a script hooks it; the gate must never report a hooked item trigger as
// unused. It draws from registered item handlers, script [ON=@X] blocks (incl. the
// cross-fired @itemX mirror) and f_onitem_x functions.
namespace SphereNet.Tests;

public class ItemTriggerGateTests
{
    [Fact]
    public void IsItemTriggerUsed_RegisteredHandler_IsUsed()
    {
        var d = new TriggerDispatcher();
        d.RegisterItemEvent("EVENTSITEM", "MemoryEquip", (_, _) => TriggerResult.Default);

        Assert.True(d.IsItemTriggerUsed(ItemTrigger.MemoryEquip));
        Assert.False(d.IsItemTriggerUsed(ItemTrigger.Smelt)); // not hooked
    }

    [Fact]
    public void IsItemTriggerUsed_CrossFireMirror_IsUsed()
    {
        // A script hooking the cross-fired @itemMemoryEquip keeps @MemoryEquip used.
        var d = new TriggerDispatcher();
        d.RegisterItemEvent("EVENTSITEM", "itemMemoryEquip", (_, _) => TriggerResult.Default);

        Assert.True(d.IsItemTriggerUsed(ItemTrigger.MemoryEquip));
    }

    [Fact]
    public void BuildUsedTriggerCache_ScriptOnBlock_MarksItemTriggerUsed()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_mem_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_mem_gate_test]
            ON=@MemoryEquip
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
            Assert.False(d.IsItemTriggerUsed(ItemTrigger.MemoryEquip));
            d.BuildUsedTriggerCache();
            Assert.True(d.IsItemTriggerUsed(ItemTrigger.MemoryEquip));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void MemoryEquip_MemoryCreated_FiresHookWithMemoryItem()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Item? equipped = null;
        Character.OnMemoryEquip = m => equipped = m;

        var mem = ch.Memory_CreateObj(new Serial(0x1234), MemoryType.Fight);

        Assert.Same(mem, equipped);
        Assert.Equal(ItemType.EqMemoryObj, mem.ItemType);
    }
}
