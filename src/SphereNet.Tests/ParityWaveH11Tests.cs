using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-H11 (wiki/hedef.txt long tail):
//   * item verbs SMELT / CARVECORPSE through the client script console
//     (Source-X CIV_SMELT / CIV_CARVECORPSE — both act with SRC)
//   * MDB.* secondary MySQL reference object (verb surface + property reads)
[Collection("DefinitionLoaderSerial")]
public class ParityWaveH11Tests
{
    private static (GameWorld world, SphereNet.Game.Clients.GameClient client, Character player)
        CreateClientEnv(int port)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, player);
        return (world, client, player);
    }

    [Fact]
    public void SmeltVerb_SmeltsOreAtTheNearbyForge()
    {
        var (world, client, player) = CreateClientEnv(2201);
        player.SetSkill(SkillType.Mining, 1000);
        Character.OnSkillUseQuick = (_, _, _, _) => 1; // pin the roll (reset by ResetEngineStatics)

        var forge = world.CreateItem();
        forge.BaseId = 0x0FB1;
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        var ore = world.CreateItem();
        ore.BaseId = 0x19B9;
        ore.ItemType = ItemType.Ore;
        ore.Amount = 4;
        // The ingot an ore yields is its definition's TDATA1 (CCharSkill.cpp:1149),
        // and the ingot must be an IT_INGOT definition to come out of the fire (:1203).
        SphereNet.Game.Definitions.DefinitionLoader.SetItemDef(0x19B9,
            new SphereNet.Scripting.Definitions.ItemDef(ResourceId.Invalid) { Type = ItemType.Ore, TData1 = 0x1BF2 });
        SphereNet.Game.Definitions.DefinitionLoader.SetItemDef(0x1BF2,
            new SphereNet.Scripting.Definitions.ItemDef(ResourceId.Invalid) { Type = ItemType.Ingot });
        player.Backpack!.AddItem(ore);

        // No forge uid in the arg: the nearest one in reach is used.
        Assert.True(client.TryExecuteScriptCommand(ore, "SMELT", "", null));

        Assert.True(ore.IsDeleted); // the ore was consumed
        Assert.Contains(player.Backpack.Contents, i => i.ItemType == ItemType.Ingot);
    }

    [Fact]
    public void CarveCorpseVerb_CarvesWithSrcAsTheButcher()
    {
        var (world, client, player) = CreateClientEnv(2202);

        var victim = world.CreateCharacter();
        victim.IsPlayer = true; // keep the char (an NPC is deleted with its death)
        victim.BodyId = 0xD8;   // cow — carvable meat/hides
        victim.CharDefIndex = 0xD8;
        TestHarness.SeedCharDef(0xD8); // the corpse is typed by the cow's chardef
        victim.MaxHits = victim.Hits = 10;
        world.PlaceCharacter(victim, new Point3D(101, 100, 0, 0));

        var death = new DeathEngine(world);
        client.SetEngines(deathEngine: death);
        var corpse = death.ProcessDeath(victim);
        Assert.NotNull(corpse);

        Assert.True(client.TryExecuteScriptCommand(corpse!, "CARVECORPSE", "", null));

        // The corpse is marked carved (forensics reads this tag).
        Assert.Equal("1", corpse!.Tags.Get("CORPSE_CARVED"));
    }

    [Fact]
    public void MdbSurface_IsWiredIndependentlyOfDb()
    {
        var (world, client, player) = CreateClientEnv(2203);

        // Without an adapter the verbs are swallowed (no crash) and the
        // property reads answer disconnected.
        Assert.True(client.TryExecuteScriptCommand(player, "MDB.QUERY", "SELECT 1", null));
        Assert.True(client.TryResolveScriptVariable("MDB.CONNECTED", player, null, out string connected));
        Assert.Equal("0", connected);
    }
}
