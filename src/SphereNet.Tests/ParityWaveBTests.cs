using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-B (wiki/hedef.txt) — combat core parity:
//   * a player's landed swings train the weapon skill AND Tactics
//     (Source-X Fight_Hit: Skill_Experience(skill) + Skill_Experience(TACTICS))
//   * @HitTry fires with SRC = the victim and ARGO = the weapon (the pre-W-B
//     contract passed SRC = attacker / ARGO = victim, so Source-X scripts read
//     the wrong objects)
//   * a frozen/paralyzed target is hit near-certainly (Source-X returns a
//     trivially-easy difficulty; the old rand(10)*10 percent averaged 45%)
public class ParityWaveBTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void ResolveAttack_PlayerAttacker_TrainsWeaponSkillAndTactics()
    {
        TestHarness.SeedSkillAdvRates(); // gain follows ADV_RATE strictly (no curve = no gain)
        var world = CreateWorld();
        var attacker = world.CreateCharacter();
        attacker.IsPlayer = true;
        attacker.PrivLevel = PrivLevel.Player; // GM would be excluded from gain
        attacker.Str = 50;
        attacker.SetSkill(SkillType.Wrestling, 500);
        attacker.SetSkill(SkillType.Tactics, 500);
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = false;
        target.Str = 50;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        int wrestlingBefore = attacker.GetSkill(SkillType.Wrestling);
        int tacticsBefore = attacker.GetSkill(SkillType.Tactics);

        // The per-swing gain roll is per-mille; enough swings make at least one
        // gain in either skill effectively certain. Combat was previously the
        // one activity that NEVER trained a skill (no GainExperience call).
        for (int i = 0; i < 4000 &&
             attacker.GetSkill(SkillType.Wrestling) == wrestlingBefore &&
             attacker.GetSkill(SkillType.Tactics) == tacticsBefore; i++)
        {
            target.Hits = 100;
            CombatEngine.ResolveAttack(attacker, target, null);
        }

        Assert.True(
            attacker.GetSkill(SkillType.Wrestling) > wrestlingBefore ||
            attacker.GetSkill(SkillType.Tactics) > tacticsBefore,
            "4000 landed swings produced no weapon-skill or Tactics gain");
    }

    [Fact]
    public void ResolveAttack_NpcAttacker_GainsNothing()
    {
        var world = CreateWorld();
        var attacker = world.CreateCharacter();
        attacker.IsPlayer = false; // Source-X gates passive combat gain on m_pPlayer
        attacker.Str = 50;
        attacker.SetSkill(SkillType.Wrestling, 500);
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = false;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        for (int i = 0; i < 500; i++)
        {
            target.Hits = 100;
            CombatEngine.ResolveAttack(attacker, target, null);
        }

        Assert.Equal(500, attacker.GetSkill(SkillType.Wrestling));
        Assert.Equal(0, attacker.GetSkill(SkillType.Tactics));
    }

    [Fact]
    public void HitTry_SrcIsVictim_ArgoIsWeapon()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_hittry_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_hittry_probe]
            ON=@HitTry
            TAG.GOTSRC=<SRC.UID>
            TAG.GOTARGO=<ARGO.UID>
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var loggerFactory = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(loggerFactory, world,
                new SphereNet.Game.Accounts.AccountManager(loggerFactory), 1401);

            var attacker = world.CreateCharacter();
            attacker.IsPlayer = true;
            attacker.Str = 100;
            attacker.Stam = 100;
            attacker.SetSkill(SkillType.Swordsmanship, 1000);
            attacker.SetStatFlag(StatFlag.War);
            attacker.Events.Add(stack.Resources.ResolveDefName("e_hittry_probe"));
            world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, attacker);

            var weapon = world.CreateItem();
            weapon.ItemType = ItemType.WeaponSword;
            weapon.BaseId = 0x0F5E;
            attacker.Equip(weapon, Layer.OneHanded);

            var target = world.CreateCharacter();
            target.IsPlayer = true;
            target.Hits = target.MaxHits = 100;
            world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

            client.SetEngines(triggerDispatcher: stack.Dispatcher);
            attacker.FightTarget = target.Uid;
            attacker.NextAttackTime = 0;
            attacker.SetCombatSwingState(SwingState.Ready);
            client.TickCombat();

            // Source-X @HitTry: SRC = pCharTarg (the victim), ARGO = the weapon.
            Assert.True(attacker.TryGetTag("GOTSRC", out var src));
            Assert.Equal(target.Uid.Value, ParseUid(src!));
            Assert.True(attacker.TryGetTag("GOTARGO", out var argo));
            Assert.Equal(weapon.Uid.Value, ParseUid(argo!));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void CalcHitChance_FrozenTarget_NearCertainHit()
    {
        var world = CreateWorld();
        var attacker = world.CreateCharacter();
        attacker.IsPlayer = true;
        attacker.SetSkill(SkillType.Wrestling, 300);
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.SetStatFlag(StatFlag.Freeze);
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        // Deterministic: no random component left in the frozen branch.
        for (int i = 0; i < 20; i++)
            Assert.Equal(95, CombatEngine.CalcHitChance(attacker, target));
    }

    // <X.UID> script reads render as bare hex (no 0x prefix).
    private static uint ParseUid(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return Convert.ToUInt32(s, 16);
    }
}
