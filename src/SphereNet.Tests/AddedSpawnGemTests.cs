using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A spawn gem made from its definition - placed with .ADD, or by a script - is a
/// working spawner, as upstream gives an IT_SPAWN_CHAR item its CCSpawn component when
/// it is created. SphereNet set the component up only at world load and on a runtime
/// TYPE change, so a gem a GM placed took SPAWNID, AMOUNT and START and never produced
/// anything. And START has to fire the gem now: it only moved the component's own clock,
/// so the gem slept until its old 15-30 minute timer.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class AddedSpawnGemTests
{
    private const string Script = """
        [ITEMDEF 01ea7]
        DEFNAME=i_worldgem_bit
        TYPE=t_spawn_char

        [CHARDEF 0cd]
        DEFNAME=c_rabbit
        NAME=rabbit
        """;

    [Fact]
    public void AGemMadeFromItsDefinitionSpawns()
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_addgem_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, Script);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? "",
        };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.OnSpawnTypeChanged = item => item.InitializeSpawnComponent(world, resources);

        // Someone near by: a sector with no player in it sleeps, and its spawns with it.
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.IsOnline = true;
        world.PlaceCharacter(gm, new Point3D(502, 500, 0, 0));
        world.AddOnlinePlayer(gm);
        world.OnTick();

        var gem = world.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(gem, 0x1EA7));
        world.PlaceItem(gem, new Point3D(500, 500, 0, 0));
        Assert.NotNull(gem.SpawnChar);

        Assert.True(gem.TrySetProperty("SPAWNID", "c_rabbit"));
        Assert.True(gem.TrySetProperty("AMOUNT", "2"));
        // START fires the gem now (pItem->SetTimeout(0), CCSpawn.cpp:1257) - through the
        // world's own timer queue, not by calling the gem directly.
        Assert.True(gem.TryExecuteCommand("START", "", null!, out _));
        for (int i = 0; i < 5 && gem.SpawnChar!.CurrentCount == 0; i++)
        {
            System.Threading.Thread.Sleep(20);
            world.OnTick();
        }

        Assert.Equal(1, gem.SpawnChar!.CurrentCount); // one per timer (CCSpawn::OnTickComponent)
    }
}
