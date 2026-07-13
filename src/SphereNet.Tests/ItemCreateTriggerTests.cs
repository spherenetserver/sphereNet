using System;
using System.IO;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Source-X applies a magic weapon's magic entirely through its ITEMDEF
/// <c>@Create</c> block (MOREY/ATTR/HITPOINTS/COLOR), and fires that trigger on
/// EVERY item it materialises from a def (CItem::GenerateScript → ITRIG_Create) —
/// loot, spawns, NEWITEM, vendor restock, carve, .add. SphereNet omitted the
/// per-item @Create call, so magic loot came out inert (a "Bardiche of
/// Vanquishing" with MOREY=0, no hue). ItemDefHelper.ApplyInstanceMetadata now
/// fires @Create once per fresh instance via the wired CreateTriggerHook.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemCreateTriggerTests
{
    [Fact]
    public void ApplyInstanceMetadata_FiresItemDefCreateTrigger_ApplyingMagicProps()
    {
        // A magic weapon whose "magic" lives ENTIRELY in @Create, exactly like the
        // reference i_bardiche_vanq (MOREY/ATTR/HITPOINTS/COLOR set in the body).
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_create_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp,
            "[ITEMDEF 0f4d]\r\n" +
            "DEFNAME=i_bardiche_vanq_test\r\n" +
            "TYPE=t_weapon_mace_sharp\r\n" +
            "ON=@Create\r\n" +
            "COLOR=0481\r\n" +
            "MORE1=7\r\n" +
            "TAG.MAGICLOOT=1\r\n");
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tmp);
            ScriptTestBootstrap.LoadDefinitions(stack.Resources);

            var world = new GameWorld(stack.LoggerFactory);
            world.InitMap(0, 6144, 4096);
            ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;

            // Wire @Create the same way Program.EngineWiring does at startup.
            Item.CreateTriggerHook = item =>
                stack.Dispatcher.FireItemTrigger(item, ItemTrigger.Create,
                    new TriggerArgs { ItemSrc = item });

            var loot = world.CreateItem();
            loot.BaseId = 0x0F4D; // the graphic is set first by the loot/spawn path

            // Materialising the def is what every loot/spawn path does; it must run
            // the def @Create so the plain graphic becomes an actual magic weapon.
            Assert.True(ItemDefHelper.ApplyInstanceMetadata(loot, 0x0F4D,
                setDisplayId: false, setName: false));

            Assert.Equal(0x0481, loot.Hue.Value);      // COLOR applied
            Assert.Equal(7u, loot.More1);              // MOREx applied
            Assert.True(loot.TryGetTag("MAGICLOOT", out var magic) && magic == "1");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void CreateTrigger_FiresExactlyOncePerInstance()
    {
        // The guard matters: a magic @Create body that adds a damage bonus would
        // compound it on every metadata re-stamp. FireCreateTrigger must be a
        // once-per-instance no-op thereafter.
        var lf = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        int fired = 0;
        Item.CreateTriggerHook = _ => fired++;

        var item = world.CreateItem();
        item.FireCreateTrigger();
        item.FireCreateTrigger();
        item.FireCreateTrigger();

        Assert.Equal(1, fired);
    }

    [Fact]
    public void CreateTrigger_UnwiredHook_IsHarmlessNoop()
    {
        // Unit paths that never wire the hook (most of the suite) must not throw
        // when a def is materialised.
        var lf = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.CreateTriggerHook = null;

        var item = world.CreateItem();
        var ex = Record.Exception(() => item.FireCreateTrigger());
        Assert.Null(ex);
    }
}
