using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A script-set THREAT has to survive a restart (port plan İŞ-19 / PLAN-402).
///
/// Threat is not derived from anything - a script writes it, or a master's order does
/// - so if the save drops it, every aggro weighting a shard has set up is gone at the
/// next restart and nothing says so. The field is appended to the ATTACKER line, so a
/// save written before it still reads (the loader defaults the missing field to zero).
/// </summary>
public sealed class AttackerThreatPersistenceTests : IDisposable
{
    private readonly string _dir;

    public AttackerThreatPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_threat_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeChar(GameWorld world, int x, bool player = false)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.BaseId = 0x0190;
        ch.Str = 50; ch.MaxHits = ch.Hits = 50;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void ThreatSurvivesASaveCycle()
    {
        var world = NewWorld();
        var npc = MakeChar(world, 100);
        var foe = MakeChar(world, 101);
        npc.RecordAttack(foe.Uid, 25);
        Assert.True(npc.CombatState.SetAttackerThreat(0, 640));

        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { }))
            .Save(world, _dir);

        var reloaded = NewWorld();
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { }))
            .Load(reloaded, _dir);

        var backNpc = reloaded.FindChar(npc.Uid);
        Assert.NotNull(backNpc);
        Assert.Single(backNpc!.Attackers);
        Assert.Equal(foe.Uid, backNpc.Attackers[0].Uid);
        Assert.Equal(25, backNpc.Attackers[0].TotalDamage);
        Assert.Equal(640, backNpc.CombatState.GetAttackerThreat(0));
    }

    [Fact]
    public void AnOlderSaveWithoutTheFieldStillLoads()
    {
        // Three fields is what this engine wrote before threat existed; reading it
        // must not lose the attacker, only the value that was never there.
        var world = NewWorld();
        var npc = MakeChar(world, 100);
        var foe = MakeChar(world, 101);
        npc.RecordAttack(foe.Uid, 25);

        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { }))
            .Save(world, _dir);

        foreach (string file in Directory.EnumerateFiles(_dir, "*.scp"))
        {
            string text = File.ReadAllText(file);
            if (!text.Contains("ATTACKER=", StringComparison.Ordinal)) continue;
            var rebuilt = new System.Text.StringBuilder();
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("ATTACKER=", StringComparison.Ordinal))
                {
                    var parts = trimmed.Split(',');
                    trimmed = string.Join(',', parts[..3]);   // drop the threat field
                }
                rebuilt.Append(trimmed).Append('\n');
            }
            File.WriteAllText(file, rebuilt.ToString());
        }

        var reloaded = NewWorld();
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { }))
            .Load(reloaded, _dir);

        var backNpc = reloaded.FindChar(npc.Uid);
        Assert.NotNull(backNpc);
        Assert.Single(backNpc!.Attackers);
        Assert.Equal(0, backNpc.CombatState.GetAttackerThreat(0));
    }
}
