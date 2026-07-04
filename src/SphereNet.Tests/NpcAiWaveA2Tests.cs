using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

// NpcAI partial audit wave A2 (wiki/npcai-partial-audit.txt): behavioral P2s —
// guard go-home, blocked-step sidestep, creature innate RANGE.
[Collection("DefinitionLoaderSerial")]
public class NpcAiWaveA2Tests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void GuardOutsideGuardedArea_TeleportsToItsPost_OrDespawns()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());

        var observer = world.CreateCharacter();
        observer.IsPlayer = true;
        observer.IsOnline = true;
        observer.Hits = observer.MaxHits = 100;
        world.PlaceCharacter(observer, new Point3D(405, 400, 0, 0));
        world.AddOnlinePlayer(observer);
        world.OnTick(); // activate the sector so masterless NPCs act

        // Guard with a post, standing in unguarded wilderness.
        var guard = world.CreateCharacter();
        guard.NpcBrain = NpcBrainType.Guard;
        guard.Hits = guard.MaxHits = 100;
        guard.Home = new Point3D(200, 200, 0, 0);
        world.PlaceCharacter(guard, new Point3D(400, 400, 0, 0));
        guard.NextNpcActionTime = 0;
        ai.OnTickAction(guard);
        Assert.Equal(200, guard.X); // Source-X NPC_Act_GoHome teleport
        Assert.Equal(200, guard.Y);

        // Guard with NO post despawns instead of wandering forever.
        var stray = world.CreateCharacter();
        stray.NpcBrain = NpcBrainType.Guard;
        stray.Hits = stray.MaxHits = 100;
        world.PlaceCharacter(stray, new Point3D(400, 400, 0, 0));
        stray.NextNpcActionTime = 0;
        ai.OnTickAction(stray);
        Assert.True(stray.IsDeleted);
    }

    [Fact]
    public void BlockedDumbNpc_SidestepsInsteadOfFreezing()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.IsOnline = true;
        owner.Hits = owner.MaxHits = 100;
        world.PlaceCharacter(owner, new Point3D(110, 100, 0, 0));
        world.AddOnlinePlayer(owner);

        // Low-INT pet (effInt < 30 → no A*), direct east step blocked by a char.
        var pet = world.CreateCharacter();
        pet.NpcMaster = owner.Uid;
        pet.Int = 10;
        pet.Hits = pet.MaxHits = 50;
        pet.Stam = pet.MaxStam = 50;
        pet.PetAIMode = PetAIMode.Come;
        world.PlaceCharacter(pet, new Point3D(100, 100, 0, 0));

        var blocker = world.CreateCharacter();
        blocker.Hits = blocker.MaxHits = 50;
        world.PlaceCharacter(blocker, new Point3D(101, 100, 0, 0));

        var start = pet.Position;
        for (int i = 0; i < 40 && pet.Position == start; i++)
        {
            pet.NextNpcActionTime = 0;
            ai.OnTickAction(pet);
        }

        // Source-X NPC_WalkToPoint: ~70%/tick random sidestep even without
        // pathfinding — the old code froze facing the blocker forever.
        Assert.NotEqual(start, pet.Position);
    }

    [Fact]
    public void CreatureInnateRange_ComesFromTheCharDef()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_a2_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [CHARDEF 0999]
            DEFNAME=c_a2_reacher
            RANGE=2
            """);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
            resources.LoadResourceFile(tempFile);
            new DefinitionLoader(resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();

            var world = CreateWorld();
            var reacher = world.CreateCharacter();
            reacher.CharDefIndex = 0x999;

            // Source-X Fight_CalcRange: max(innate chardef RANGE, weapon).
            Assert.Equal(2, NpcAI.GetFightRange(reacher, null).Max);

            var plain = world.CreateCharacter();
            Assert.Equal(1, NpcAI.GetFightRange(plain, null).Max);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
