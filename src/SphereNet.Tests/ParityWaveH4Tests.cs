using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-H4 (wiki/hedef.txt long tail):
//   * char properties ISSTUCK / CANMOVE.<dir> (Source-X CHC_ISSTUCK/CANMOVE)
//   * char verbs GOUID / GOCHAR (teleport variants, previously GM-speech only)
//   * item verb CONTCONSUME (recursive stack consume by id)
//   * @SpellCast LOCAL.WOPColor / LOCAL.WOPFont style the spoken mantra
public class ParityWaveH4Tests
{
    private sealed class NullConsole : SphereNet.Core.Interfaces.ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "test";
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void IsStuck_FrozenCharReportsStuck()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Assert.True(ch.TryGetProperty("ISSTUCK", out string free));
        Assert.Equal("0", free);

        ch.SetStatFlag(StatFlag.Freeze);
        Assert.True(ch.TryGetProperty("ISSTUCK", out string stuck));
        Assert.Equal("1", stuck);

        // A frozen char can't step anywhere either.
        Assert.True(ch.TryGetProperty("CANMOVE.N", out string move));
        Assert.Equal("0", move);
    }

    [Fact]
    public void GoUidAndGoChar_TeleportToTarget()
    {
        var world = CreateWorld();
        var mover = world.CreateCharacter();
        world.PlaceCharacter(mover, new Point3D(100, 100, 0, 0));

        var beacon = world.CreateCharacter();
        beacon.Name = "Beacon";
        world.PlaceCharacter(beacon, new Point3D(250, 300, 0, 0));

        Assert.True(mover.TryExecuteCommand("GOUID", $"0{beacon.Uid.Value:X}", new NullConsole()));
        Assert.Equal(beacon.Position.X, mover.Position.X);
        Assert.Equal(beacon.Position.Y, mover.Position.Y);

        mover.MoveTo(new Point3D(100, 100, 0, 0));
        Assert.True(mover.TryExecuteCommand("GOCHAR", "beacon", new NullConsole()));
        Assert.Equal(beacon.Position.X, mover.Position.X);
        Assert.Equal(beacon.Position.Y, mover.Position.Y);
    }

    [Fact]
    public void ContConsume_EatsStacksRecursively()
    {
        var world = CreateWorld();
        var chest = world.CreateItem();
        chest.BaseId = 0x0E43;
        chest.ItemType = ItemType.Container;
        world.PlaceItem(chest, new Point3D(100, 100, 0, 0));

        var stackA = world.CreateItem();
        stackA.BaseId = 0x0EED;
        stackA.Amount = 3;
        chest.AddItem(stackA);

        var pouch = world.CreateItem();
        pouch.BaseId = 0x0E75;
        pouch.ItemType = ItemType.Container;
        chest.AddItem(pouch);
        var stackB = world.CreateItem();
        stackB.BaseId = 0x0EED;
        stackB.Amount = 10;
        pouch.AddItem(stackB);

        // Consume 5: stack A (3) is eaten whole, stack B loses 2.
        Assert.True(chest.TryExecuteCommand("CONTCONSUME", "0EED, 5", new NullConsole()));
        Assert.True(stackA.IsDeleted);
        Assert.Equal(8, stackB.Amount);
    }

    [Fact]
    public void CastStart_WopColorAndFont_ReachTheStyledHook()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Heal,
            Name = "Heal",
            Flags = SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good,
            ManaCost = 0,
            CastTimeBase = 1,
            Runes = "In Mani",
        });
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        (string Words, ushort Hue, byte Font)? seen = null;
        engine.OnSpellWordsEx = (_, words, hue, font) => seen = (words, hue, font);

        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position,
            wopHue: 0x22, wopFont: 1) > 0);
        Assert.NotNull(seen);
        Assert.Equal("In Mani", seen!.Value.Words);
        Assert.Equal(0x22, seen.Value.Hue);
        Assert.Equal(1, seen.Value.Font);
    }
}
