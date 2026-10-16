using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Network.Packets;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Lightning strikes with a visible bolt: OnSpellEffect sends Effect(EFFECT_LIGHTNING,
/// ITEMID_NOTHING, pCharSrc) on the target after the light flash (CCharSpell.cpp
/// :4078-4081). The packs give the spell EFFECT_ID=0, so nothing else draws it; only
/// the flash went out and the damage landed with no lightning seen.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class LightningBoltEffectTests
{
    [Fact]
    public void ALightningStrikeSendsTheBoltOnItsTarget()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Lightning,
            Name = "Lightning",
            Flags = SpellFlag.TargObj | SpellFlag.Harm | SpellFlag.Damage,
            ManaCost = 0,
            CastTimeBase = 1,
            EffectBase = 10,
            EffectScale = 10,
        });
        var spells = new SpellEngine(world, registry);
        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100;
        caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.MaxHits = 100;
        target.Hits = 100;
        world.PlaceCharacter(target, new Point3D(102, 100, 0, 0));

        var sent = new List<PacketWriter>();
        var saved = Character.BroadcastNearby;
        Character.BroadcastNearby = (_, _, p, _) => sent.Add(p);
        try
        {
            caster.BeginCast(SpellType.Lightning, target.Uid, target.Position);
            Assert.True(spells.CastDone(caster));
        }
        finally { Character.BroadcastNearby = saved; }

        var bolt = sent.OfType<SphereNet.Network.Packets.Outgoing.PacketEffect>()
            .Select(p => p.Build().Span.ToArray())
            .FirstOrDefault(b => b[0] == 0x70 && b[1] == 1);
        Assert.NotNull(bolt);
        uint dst = (uint)(bolt![6] << 24 | bolt[7] << 16 | bolt[8] << 8 | bolt[9]);
        Assert.Equal(target.Uid.Value, dst);
    }
}
