using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-A (wiki/hedef.txt) — cheap P0 Source-X parity fixes:
//   * trigger names @Jailed / @Ship_Move / @itemSPELL match Source-X sm_szTrigName
//   * @Death fires with SRC = the dying char (killer stays on ARGO)
//   * @Kill seeds ARGN1 = the victim's attacker-log count
//   * item @Step seeds ARGN1 = fStanding
//   * equipped cursed items refuse to move (CChar::CanMoveItem)
//   * IT_TRAP dclick/step springs the trap (CItem::Use_Trap), not RemoveTrap
//   * TAG.OVERRIDE.NOTO forces the notoriety colour (Noto_CalcFlag first line)
public class ParityWaveATests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    // ---- trigger name contract ----

    [Fact]
    public void JailTrigger_FiresScriptBlocksNamedJailed()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_jailed_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_jailed_probe]
            ON=@Jailed
            TAG.WASJAILED=1
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = TestHarness.CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.Events.Add(stack.Resources.ResolveDefName("e_jailed_probe"));
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            stack.Dispatcher.FireCharTrigger(ch, CharTrigger.Jail,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch, N1 = 5 });

            // Source-X names the trigger @Jailed; a SphereNet-era @Jail block must
            // no longer be the match target.
            Assert.True(ch.TryGetTag("WASJAILED", out var v) && v == "1");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ShipMoveTrigger_FiresScriptBlocksNamedShip_Move()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_shipmove_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_shipmove_probe]
            ON=@Ship_Move
            TAG.MOVED=1
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = TestHarness.CreateWorld();
            var item = world.CreateItem();
            item.Events.Add(stack.Resources.ResolveDefName("e_shipmove_probe"));

            stack.Dispatcher.FireItemTrigger(item, ItemTrigger.ShipMove,
                new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = item });

            Assert.True(item.TryGetTag("MOVED", out var v) && v == "1");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ---- @Death / @Kill arg contract ----

    [Fact]
    public void DeathTrigger_SrcIsVictim_ArgoIsKiller()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_deathsrc_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_death_src_probe]
            ON=@Death
            TAG.GOTSRC=<SRC.UID>
            TAG.GOTARGO=<ARGO.UID>
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = TestHarness.CreateWorld();
            var victim = world.CreateCharacter();
            victim.IsPlayer = true;
            world.PlaceCharacter(victim, new Point3D(100, 100, 0, 0));
            var killer = world.CreateCharacter();
            killer.IsPlayer = true;
            world.PlaceCharacter(killer, new Point3D(101, 100, 0, 0));
            victim.Events.Add(stack.Resources.ResolveDefName("e_death_src_probe"));

            var death = new DeathEngine(world) { TriggerDispatcher = stack.Dispatcher };
            death.ProcessDeath(victim, killer);

            // Source-X OnTrigger(CTRIG_Death, <empty>, this): SRC = the dying char.
            Assert.True(victim.TryGetTag("GOTSRC", out var src));
            Assert.Equal(victim.Uid.Value, ParseUid(src!));
            Assert.True(victim.TryGetTag("GOTARGO", out var argo));
            Assert.Equal(killer.Uid.Value, ParseUid(argo!));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void KillTrigger_Argn1IsVictimAttackerCount()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_killn1_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_kill_n1_probe]
            ON=@Kill
            TAG.GOTN1=<ARGN1>
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = TestHarness.CreateWorld();
            var victim = world.CreateCharacter();
            victim.IsPlayer = true;
            world.PlaceCharacter(victim, new Point3D(100, 100, 0, 0));
            var killer = world.CreateCharacter();
            killer.IsPlayer = true;
            world.PlaceCharacter(killer, new Point3D(101, 100, 0, 0));
            var helper = world.CreateCharacter();
            helper.IsPlayer = true;
            world.PlaceCharacter(helper, new Point3D(102, 100, 0, 0));
            killer.Events.Add(stack.Resources.ResolveDefName("e_kill_n1_probe"));

            // Two logged attackers on the victim → ARGN1 = 2 (Source-X
            // Init(GetAttackersCount(), 0, 0, this)).
            victim.CombatState.RecordAttack(killer.Uid, 10);
            victim.CombatState.RecordAttack(helper.Uid, 5);

            var death = new DeathEngine(world) { TriggerDispatcher = stack.Dispatcher };
            death.ProcessDeath(victim, killer);

            Assert.True(killer.TryGetTag("GOTN1", out var n1) && n1 == "2");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // <X.UID> script reads render as bare hex (no 0x prefix).
    private static uint ParseUid(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return Convert.ToUInt32(s, 16);
    }

    // ---- cursed equip move gate ----

    [Fact]
    public void CanMove_EquippedCursedItem_Denied()
    {
        var world = CreateWorld();
        var mover = MakePlayer(world, 100);
        mover.PrivLevel = PrivLevel.Player;

        var ring = world.CreateItem();
        ring.SetAttr(ObjAttributes.Cursed);
        ring.IsEquipped = true;

        Assert.False(ItemMoveRules.CanMove(mover, ring, out var denial));
        Assert.Equal(ItemMoveRules.MoveDenial.ItemCursed, denial);

        // The same cursed item NOT equipped moves freely (curse binds on wear).
        ring.IsEquipped = false;
        Assert.True(ItemMoveRules.CanMove(mover, ring, out _));
    }

    // ---- IT_TRAP Use_Trap semantics ----

    [Fact]
    public void UseTrap_ArmsIdleTrap_AndReturnsMore2Damage()
    {
        var world = CreateWorld();
        var trap = world.CreateItem();
        trap.BaseId = 0x1100;
        trap.ItemType = ItemType.Trap;
        trap.More2 = 17; // base damage
        trap.MoreP = new Point3D(4, 0, 0, 0); // morex = 4s active window

        int dmg = trap.UseTrap();

        Assert.Equal(17, dmg);
        Assert.Equal(ItemType.TrapActive, trap.ItemType);
        // Graphic swapped to dispid+1 (MORE1 was 0), old graphic saved into MORE1.
        Assert.Equal(0x1101, trap.BaseId);
        Assert.Equal(0x1100u, trap.More1);
        Assert.True(trap.Timeout > 0); // armed window timer running
    }

    [Fact]
    public void UseTrap_DamageDefaultsTo2()
    {
        var world = CreateWorld();
        var trap = world.CreateItem();
        trap.BaseId = 0x1100;
        trap.ItemType = ItemType.TrapActive; // already sprung — no re-arm

        Assert.Equal(2, trap.UseTrap());
        Assert.Equal(ItemType.TrapActive, trap.ItemType);
        Assert.Equal(0x1100, trap.BaseId); // active trap: no graphic swap
    }

    // ---- TAG.OVERRIDE.NOTO ----

    [Fact]
    public void OverrideNotoTag_ForcesNotorietyColour()
    {
        var world = CreateWorld();
        var viewer = MakePlayer(world, 100);
        var subject = MakePlayer(world, 101);
        subject.Karma = 5000; // would compute to innocent blue (1)

        Assert.Equal((byte)1, SphereNet.Game.Clients.GameClient.ComputeNotoriety(world, viewer, subject));

        subject.SetTag("OVERRIDE.NOTO", "6");
        Assert.Equal((byte)6, SphereNet.Game.Clients.GameClient.ComputeNotoriety(world, viewer, subject));

        // Out-of-range values fall back to the computed colour.
        subject.SetTag("OVERRIDE.NOTO", "12");
        Assert.Equal((byte)1, SphereNet.Game.Clients.GameClient.ComputeNotoriety(world, viewer, subject));
    }
}
