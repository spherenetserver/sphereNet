using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Regression for "logged in, backpack shows empty, but .edit still lists the
/// items." The container reverse index (what the client's open-container 0x3C
/// batch reads via GameWorld.GetContainerContents) is maintained incrementally by
/// the Item.ContainedIn setter — but ONLY when Item.ResolveWorld is wired. The
/// initial world load reads the save BEFORE the engine wiring sets ResolveWorld,
/// so contained items landed in the parent's Contents (what .edit reads) yet never
/// entered the index. GameWorld.RebuildContainerIndex (called at the end of
/// WorldLoader.Load) must reconcile the two independently of wiring order.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ContainerIndexLoadOrderTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void ContainmentEstablishedBeforeResolveWorld_IndexEmpty_RebuildReconciles()
    {
        var world = CreateWorld();
        var pack = world.CreateItem();
        var a = world.CreateItem();
        var b = world.CreateItem();
        var c = world.CreateItem();

        // Simulate the load-time ordering: ResolveWorld is not yet wired when the
        // save establishes containment. The setter's ContainerIndexAdd is gated on
        // ResolveWorld returning non-null, so returning null reproduces load.
        Item.ResolveWorld = () => null!;
        try
        {
            pack.AddItem(a);
            pack.AddItem(b);
            pack.AddItem(c);

            // .edit reads Contents — the items are here.
            Assert.Equal(3, pack.Contents.Count(i => !i.IsDeleted));
            // The client reads the reverse index — empty, so the bag looks empty.
            Assert.Empty(world.GetContainerContents(pack.Uid));

            // The rebuild does not depend on ResolveWorld (it calls the index
            // directly), so it reconciles the two views.
            world.RebuildContainerIndex();

            var indexed = world.GetContainerContents(pack.Uid).ToList();
            Assert.Equal(3, indexed.Count);
            Assert.Contains(a, indexed);
            Assert.Contains(b, indexed);
            Assert.Contains(c, indexed);
        }
        finally
        {
            Item.ResolveWorld = () => world;
        }
    }

    [Fact]
    public void RebuildContainerIndex_DropsStaleEntries_AndReindexesMoved()
    {
        var world = CreateWorld();
        var pack1 = world.CreateItem();
        var pack2 = world.CreateItem();
        var item = world.CreateItem();

        pack1.AddItem(item);
        Assert.Contains(item, world.GetContainerContents(pack1.Uid));

        // Move to pack2 while ResolveWorld is null (index not updated live).
        Item.ResolveWorld = () => null!;
        try
        {
            pack1.RemoveItem(item);
            pack2.AddItem(item);

            // Rebuild reflects the authoritative ContainedIn: pack2, not pack1.
            world.RebuildContainerIndex();
            Assert.Empty(world.GetContainerContents(pack1.Uid));
            Assert.Contains(item, world.GetContainerContents(pack2.Uid));
        }
        finally
        {
            Item.ResolveWorld = () => world;
        }
    }
}
