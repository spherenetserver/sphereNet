using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-H8 (wiki/hedef.txt long tail):
//   * IT_SPAWN_CHAMPION: the champion spawner ignores the amount cap and
//     never pauses its timer (Source-X CCSpawn champion special case)
//   * CANCAST.<spell> property via the engine hook (Source-X CHC_CANCAST)
public class ParityWaveH8Tests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void ChampionSpawner_IgnoresAmountCap()
    {
        var world = CreateWorld();

        var spawner = world.CreateItem();
        spawner.BaseId = 0x1F13;
        world.PlaceItem(spawner, new Point3D(100, 100, 0, 0));

        var comp = new SpawnComponent(spawner, world)
        {
            IsChampion = true,
            CharDefId = 0x0190,
            MaxCount = 2,
        };

        // A normal spawner pauses at MaxCount; a champion keeps its timer
        // armed and spawns past the cap.
        long now = Environment.TickCount64;
        for (int i = 0; i < 5; i++)
        {
            comp.ForceSpawn();
            comp.OnTick(now + i);
        }

        Assert.True(comp.CurrentCount > 2,
            $"champion spawner stopped at {comp.CurrentCount} (cap 2)");
    }

    [Fact]
    public void NormalSpawner_StillPausesAtCap()
    {
        var world = CreateWorld();

        var spawner = world.CreateItem();
        spawner.BaseId = 0x1F13;
        world.PlaceItem(spawner, new Point3D(100, 100, 0, 0));

        var comp = new SpawnComponent(spawner, world)
        {
            CharDefId = 0x0190,
            MaxCount = 2,
        };

        long now = Environment.TickCount64;
        for (int i = 0; i < 5; i++)
        {
            comp.ForceSpawn();
            comp.OnTick(now + i);
        }

        Assert.True(comp.CurrentCount <= 2);
    }

    [Fact]
    public void CanCastProperty_UsesEngineHook()
    {
        var saved = Character.OnCanCastCheck;
        try
        {
            var world = CreateWorld();
            var ch = world.CreateCharacter();
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            // No hook wired → conservative "0".
            Assert.True(ch.TryGetProperty("CANCAST.4", out string noHook));
            Assert.Equal("0", noHook);

            int seenSpell = 0;
            Character.OnCanCastCheck = (c, spellId) => { seenSpell = spellId; return spellId == 4; };

            Assert.True(ch.TryGetProperty("CANCAST.4", out string yes));
            Assert.Equal("1", yes);
            Assert.Equal(4, seenSpell);

            // Spell enum names resolve too (Heal = 4).
            Assert.True(ch.TryGetProperty("CANCAST.HEAL", out string byName));
            Assert.Equal("1", byName);

            Assert.True(ch.TryGetProperty("CANCAST.9", out string no));
            Assert.Equal("0", no);
        }
        finally
        {
            Character.OnCanCastCheck = saved;
        }
    }
}
