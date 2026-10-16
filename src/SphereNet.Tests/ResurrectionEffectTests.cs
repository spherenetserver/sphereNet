using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Network.Packets;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// However a character is raised - healer, shrine, bandage or spell - the resurrection
/// shows the Resurrection spell's EFFECT_ID and plays its SOUND (Spell_Resurrection,
/// CCharSpell.cpp:519-525). It came back to life with neither.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ResurrectionEffectTests
{
    [Fact]
    public void ARaisedCharacterShowsTheResurrectionEffectAndSound()
    {
        var world = TestHarness.CreateWorld();
        var ghost = world.CreateCharacter();
        ghost.IsPlayer = true;
        ghost.MaxHits = 50;
        world.PlaceCharacter(ghost, new Point3D(100, 100, 0, 0));
        ghost.SetStatFlag(StatFlag.Dead);
        Character.ResolveSpellDef = s => s == SpellType.Resurrection
            ? new SpellDef { Id = SpellType.Resurrection, EffectId = 0x376A, Sound = 0x214 }
            : null;

        var sent = new List<PacketWriter>();
        var saved = Character.BroadcastNearby;
        Character.BroadcastNearby = (_, _, p, _) => sent.Add(p);
        try { ghost.Resurrect(); }
        finally { Character.BroadcastNearby = saved; }

        Assert.False(ghost.IsDead);
        var bytes = sent.Select(p => p.Build().Span.ToArray()).ToList();
        Assert.Contains(bytes, b => b[0] == 0x70 && (b[10] << 8 | b[11]) == 0x376A);
        Assert.Contains(bytes, b => b[0] == 0x54 && (b[2] << 8 | b[3]) == 0x214);
    }
}
