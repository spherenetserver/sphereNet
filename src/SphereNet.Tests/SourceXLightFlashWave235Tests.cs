using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.World;

namespace SphereNet.Tests;

public sealed class SourceXLightFlashWave235Tests
{
    [Fact]
    public void LightFlash_SendsFullBrightThenRestoresSectorLight()
    {
        var (world, player) = CreateOnlinePlayer(new Point3D(10, 10, 0, 0));
        var levels = new List<byte>();
        world.OnSectorLight = (character, light) =>
        {
            if (character == player) levels.Add(light);
        };

        world.LightFlash(player.Position);

        Assert.Equal([0, 25], levels);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LightFlash_SkipsDeadAndNightSightPlayers(bool dead, bool nightSight)
    {
        var (world, player) = CreateOnlinePlayer(new Point3D(10, 10, 0, 0));
        if (dead) player.Kill();
        if (nightSight) player.SetStatFlag(StatFlag.NightSight);
        int sends = 0;
        world.OnSectorLight = (_, _) => sends++;

        world.LightFlash(player.Position);

        Assert.Equal(0, sends);
    }

    [Fact]
    public void LightFlash_OnlyAffectsPlayersInTargetSector()
    {
        var world = new GameWorld(TestHarness.CreateLoggerFactory());
        world.InitMap(0, 128, 64);
        var first = AddOnlinePlayer(world, new Point3D(10, 10, 0, 0));
        var second = AddOnlinePlayer(world, new Point3D(80, 10, 0, 0));
        var flashed = new List<uint>();
        world.OnSectorLight = (character, _) => flashed.Add(character.Uid.Value);

        world.LightFlash(first.Position);

        Assert.Equal(2, flashed.Count(uid => uid == first.Uid.Value));
        Assert.DoesNotContain(second.Uid.Value, flashed);
    }

    [Fact]
    public void LightningSpellEffect_FlashesTargetsSector()
    {
        var (world, target) = CreateOnlinePlayer(new Point3D(10, 10, 0, 0));
        var caster = world.CreateCharacter();
        caster.SetSkill(SkillType.Magery, 1000);
        world.PlaceCharacter(caster, new Point3D(11, 10, 0, 0));
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Lightning,
            Flags = SpellFlag.TargChar | SpellFlag.Damage | SpellFlag.Harm,
            EffectBase = 1,
            EffectScale = 1,
        });
        var engine = new SpellEngine(world, registry);
        var levels = new List<byte>();
        world.OnSectorLight = (character, light) =>
        {
            if (character == target) levels.Add(light);
        };

        Assert.True(engine.ApplyScriptSpellEffect(caster, target, SpellType.Lightning, 1000));
        Assert.Equal([0, 25], levels);
    }

    private static (GameWorld World, SphereNet.Game.Objects.Characters.Character Player)
        CreateOnlinePlayer(Point3D position)
    {
        var world = new GameWorld(TestHarness.CreateLoggerFactory());
        world.InitMap(0, 128, 64);
        return (world, AddOnlinePlayer(world, position));
    }

    private static SphereNet.Game.Objects.Characters.Character AddOnlinePlayer(
        GameWorld world, Point3D position)
    {
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        world.PlaceCharacter(player, position);
        return player;
    }
}
