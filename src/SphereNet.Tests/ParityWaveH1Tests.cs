using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-H1 (wiki/hedef.txt long tail):
//   * [SPELL n] section lifecycle stages: @Start (with @SpellCast), @Select,
//     @TargetCancel, @EffectAdd / @EffectRemove (Source-X SPTRIG_*)
//   * IT_SEXTANT reports real degree/minute coordinates
//   * IT_ITEM_STONE dispenses its MORE1 item, burns MORE2 charges, honours
//     the MOREX regen window and goes "dead" at zero
public class ParityWaveH1Tests
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
    public void SpellSectionStage_Start_FiresWithSpellCast()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-spellstart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spell.scp"),
            "[SPELL 9]\nNAME=Strength\nON=@Start\nTAG.CASTSTART=1\n");
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(Path.Combine(dir, "spell.scp"));

            var world = TestHarness.CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            stack.Dispatcher.FireCharTrigger(ch, CharTrigger.SpellCast,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch, N1 = 9 });

            Assert.True(ch.TryGetTag("CASTSTART", out var v) && v == "1");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SpellSectionStages_EffectAddAndRemove_FireOnBuffLifecycle()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-spellfxar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // SpellType.Strength = 16 in the spell table.
        File.WriteAllText(Path.Combine(dir, "spell.scp"),
            "[SPELL 16]\nNAME=Strength\nON=@EffectAdd\nTAG.FXADD=1\nON=@EffectRemove\nTAG.FXREM=1\n");
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(Path.Combine(dir, "spell.scp"));

            var world = CreateWorld();
            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.Strength,
                Name = "Strength",
                Flags = SpellFlag.TargChar | SpellFlag.Bless | SpellFlag.Good,
                ManaCost = 0,
                CastTimeBase = 1,
                EffectBase = 5,
                EffectScale = 5,
                DurationBase = 600,
                DurationScale = 600,
            });
            var engine = new SpellEngine(world, registry);
            engine.TriggerDispatcher = stack.Dispatcher;

            var caster = world.CreateCharacter();
            caster.IsPlayer = true;
            caster.PrivLevel = PrivLevel.GM;
            caster.MaxMana = caster.Mana = 100;
            world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
            var target = world.CreateCharacter();
            target.IsPlayer = true;
            target.Str = 30;
            world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

            caster.BeginCast(SpellType.Strength, target.Uid, target.Position);
            Assert.True(engine.CastDone(caster));
            Assert.True(target.TryGetTag("FXADD", out var add) && add == "1");
            Assert.False(target.TryGetTag("FXREM", out _)); // not yet removed

            engine.StripDispellableEffects(target);
            Assert.True(target.TryGetTag("FXREM", out var rem) && rem == "1");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Sextant_FormatsDegreesAndMinutes()
    {
        // World center (Lord British's throne) is 0°S 0°E.
        Assert.Equal("0° 0'S, 0° 0'E",
            ClientItemUseHandler.FormatSextant(new Point3D(1323, 1624, 0, 0)));
        // 512 tiles east = 512*360/5120 = 36 degrees of longitude.
        Assert.Equal("0° 0'S, 36° 0'E",
            ClientItemUseHandler.FormatSextant(new Point3D(1323 + 512, 1624, 0, 0)));
        // North of the center reads N latitude.
        Assert.EndsWith("'N, 0° 0'E",
            ClientItemUseHandler.FormatSextant(new Point3D(1323, 1624 - 1024, 0, 0)));
    }

    [Fact]
    public void ItemStone_DispensesAndExhausts()
    {
        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 1501);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, player);

        var stone = world.CreateItem();
        stone.ItemType = ItemType.ItemStone;
        stone.More1 = 0x0F3F; // dispenses arrows
        stone.More2 = 2;      // two charges
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));

        client.HandleDoubleClick(stone.Uid.Value);
        Assert.Contains(pack.Contents, i => i.BaseId == 0x0F3F);
        Assert.Equal(1u, stone.More2);

        client.HandleDoubleClick(stone.Uid.Value);
        Assert.Equal((uint)ushort.MaxValue, stone.More2); // exhausted → "dead"

        int before = pack.Contents.Sum(i => i.BaseId == 0x0F3F ? (int)i.Amount : 0);
        client.HandleDoubleClick(stone.Uid.Value); // dead stone gives nothing
        int after = pack.Contents.Sum(i => i.BaseId == 0x0F3F ? (int)i.Amount : 0);
        Assert.Equal(before, after);
    }
}
