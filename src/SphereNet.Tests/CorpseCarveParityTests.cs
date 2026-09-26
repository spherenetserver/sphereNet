using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

// Source-X CChar::Use_CarveCorpse: the corpse is typed at death, the parts come
// from that type's RESOURCES, @CarveCorpse can rewrite them through
// LOCAL.resource.N.*, a player's parts fall to the ground and its corpse turns
// to bones at once, and an untyped corpse yields nothing at all.
[Collection("DefinitionLoaderSerial")]
public sealed class CorpseCarveParityTests
{
    private const string Pack = """
        [ITEMDEF 01da0]
        DEFNAME=i_flesh_head
        NAME=head

        [ITEMDEF 01d9f]
        DEFNAME=i_flesh_torso
        NAME=torso

        [ITEMDEF 09f1]
        DEFNAME=i_ribs_raw
        NAME=raw ribs
        TYPE=t_meat_raw

        [ITEMDEF 0df8]
        DEFNAME=i_wool
        NAME=wool
        TYPE=t_wool

        [CHARDEF 0190]
        DEFNAME=c_man
        RESOURCES=i_flesh_head, i_flesh_torso

        [CHARDEF 03db]
        DEFNAME=c_man_gm

        [CHARDEF 0cf]
        DEFNAME=c_sheep
        RESOURCES=3 i_ribs_raw, 2 i_wool
        """;

    private static (GameWorld World, DeathEngine Engine) Setup()
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_carve_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, Pack);
        var res = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? ""
        };
        res.LoadResourceFile(file);
        new DefinitionLoader(res, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        return (world, new DeathEngine(world));
    }

    private static Character MakePlayer(GameWorld world, ushort body = 0x0190, int x = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Yunus";
        ch.BodyId = body;               // players carry no chardef of their own
        ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static Character MakeSheep(GameWorld world)
    {
        var npc = world.CreateCharacter();
        npc.BodyId = 0x00CF;
        npc.CharDefIndex = 0x00CF;
        npc.MaxHits = 20; npc.Hits = 20;
        world.PlaceCharacter(npc, new Point3D(110, 100, 0, 0));
        return npc;
    }

    [Fact]
    public void PlayerCorpse_IsTypedByTheBody_AndItsPartsFallToTheGroundNamedAfterTheVictim()
    {
        var (world, engine) = Setup();
        var victim = MakePlayer(world);
        var corpse = engine.ProcessDeath(victim)!;

        var parts = engine.CarveCorpse(victim, corpse);

        Assert.Equal(2, parts.Count);
        Assert.Equal(new ushort[] { 0x1DA0, 0x1D9F }, parts.Select(p => p.BaseId).ToArray());
        Assert.Equal("head of Yunus", parts[0].GetName());
        Assert.All(parts, p =>
        {
            Assert.True(p.IsOnGround);
            Assert.Equal(corpse.Position, p.Position);
            Assert.Equal(victim.Uid, p.Link);
        });
        Assert.DoesNotContain(corpse.Contents, parts.Contains);
    }

    [Fact]
    public void CarvedPlayerCorpse_IsDueToTurnToBonesAtOnce()
    {
        var (world, engine) = Setup();
        var victim = MakePlayer(world);
        var corpse = engine.ProcessDeath(victim)!;
        long before = Environment.TickCount64;

        engine.CarveCorpse(victim, corpse);

        Assert.True(corpse.DecayTime > 0 && corpse.DecayTime <= Environment.TickCount64);
        Assert.True(corpse.DecayTime >= before - 1);
        Assert.True(corpse.TryGetTag("CORPSE_CARVED", out string? c) && c == "1");
        Assert.True(corpse.TryGetTag("KILLER_UID", out string? k) && k == victim.Uid.Value.ToString());
    }

    [Fact]
    public void CorpseType_IsFixedAtDeath_NotReadFromTheOwnersLaterBody()
    {
        var (world, engine) = Setup();
        var victim = MakePlayer(world);
        var corpse = engine.ProcessDeath(victim)!;
        victim.BodyId = 0x03DB; // resurrected into a body with no RESOURCES

        var parts = engine.CarveCorpse(victim, corpse);

        Assert.Equal(2, parts.Count);
    }

    [Fact]
    public void BodyWithoutResources_YieldsNothing_ButCountsAsCarved()
    {
        var (world, engine) = Setup();
        var gm = MakePlayer(world, body: 0x03DB);
        var corpse = engine.ProcessDeath(gm)!;
        var carver = MakePlayer(world, x: 101);
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        carver.Equip(pack, Layer.Pack);

        var parts = engine.CarveCorpse(carver, corpse);

        Assert.Empty(parts);
        Assert.Empty(pack.Contents);   // no invented hides/ribs/bones
        // The type exists, so the reference goes through with the carve: "nothing
        // useful", the corpse is marked carved and the owner's corpse goes to bones.
        Assert.True(corpse.TryGetTag("CORPSE_CARVED", out _));
    }

    [Fact]
    public void UntypedCorpse_YieldsNothing_AndFiresNoTrigger()
    {
        var (world, engine) = Setup();
        var carver = MakePlayer(world);
        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.Amount = 0x0999; // no chardef for this body
        world.PlaceItem(corpse, carver.Position);
        var dispatcher = new TriggerDispatcher();
        int fired = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "CarveCorpse", (_, _) => { fired++; return TriggerResult.False; });
        engine.TriggerDispatcher = dispatcher;

        Assert.Empty(engine.CarveCorpse(carver, corpse));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void CreatureCorpse_TakesItsPartsInside_WithTheirAmounts()
    {
        var (world, engine) = Setup();
        var sheep = MakeSheep(world);
        var corpse = engine.ProcessDeath(sheep)!;
        var carver = MakePlayer(world, x: 110);

        var parts = engine.CarveCorpse(carver, corpse);

        Assert.Equal(2, parts.Count);
        Assert.Equal(ItemType.MeatRaw, parts[0].ItemType);
        Assert.Equal(3, parts[0].Amount);
        Assert.Equal(ItemType.Wool, parts[1].ItemType);
        Assert.Equal(2, parts[1].Amount);
        Assert.All(parts, p => Assert.Equal(corpse.Uid, p.ContainedIn));
        Assert.Equal("raw ribs", parts[0].GetName()); // creature parts keep their name
        Assert.True(corpse.DecayTime > Environment.TickCount64); // NPC corpse keeps its timer
    }

    [Fact]
    public void CarveCorpseTrigger_SeesAndRewritesTheResourceList()
    {
        var (world, engine) = Setup();
        var sheep = MakeSheep(world);
        var corpse = engine.ProcessDeath(sheep)!;
        var carver = MakePlayer(world, x: 110);
        var blade = world.CreateItem();
        blade.BaseId = 0x0F52;

        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "CarveCorpse", (_, args) =>
        {
            Assert.Equal(2, args.N1);
            Assert.Same(blade, args.O1);
            Assert.Equal(3, args.Locals!.GetInt("resource.0.amount"));
            args.Locals.SetInt("resource.0.amount", 7);
            args.Locals.Set("resource.1.ID", "0"); // ITEMID_NOTHING ends the list
            return TriggerResult.False;
        });
        engine.TriggerDispatcher = dispatcher;

        var parts = engine.CarveCorpse(carver, corpse, blade);

        Assert.Single(parts);
        Assert.Equal(7, parts[0].Amount);
    }

    [Fact]
    public void CarveCorpseTrigger_Return1_CancelsTheCarve()
    {
        var (world, engine) = Setup();
        var sheep = MakeSheep(world);
        var corpse = engine.ProcessDeath(sheep)!;
        var carver = MakePlayer(world, x: 110);
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "CarveCorpse", (_, _) => TriggerResult.True);
        engine.TriggerDispatcher = dispatcher;

        Assert.Empty(engine.CarveCorpse(carver, corpse));
        Assert.False(corpse.TryGetTag("CORPSE_CARVED", out _));
    }

    [Fact]
    public void ACorpse_IsCarvedOnlyOnce()
    {
        var (world, engine) = Setup();
        var sheep = MakeSheep(world);
        var corpse = engine.ProcessDeath(sheep)!;
        var carver = MakePlayer(world, x: 110);

        Assert.NotEmpty(engine.CarveCorpse(carver, corpse));
        Assert.Empty(engine.CarveCorpse(carver, corpse));
    }

    [Fact]
    public void CarvingAnInnocentPlayersCorpse_FlagsTheCarverCriminal()
    {
        var (world, engine) = Setup();
        var victim = MakePlayer(world);
        var corpse = engine.ProcessDeath(victim)!;
        var carver = MakePlayer(world, x: 101);

        engine.CarveCorpse(carver, corpse);

        Assert.True(carver.IsStatFlag(StatFlag.Criminal));
    }

    [Fact]
    public void PartAddedToAnOpenCorpse_IsSentToItsViewers()
    {
        var (world, engine) = Setup();
        var sheep = MakeSheep(world);
        var corpse = engine.ProcessDeath(sheep)!;
        var carver = MakePlayer(world, x: 110);
        var redrawn = new List<Item>();
        Item.OnVisualUpdate = redrawn.Add;

        var parts = engine.CarveCorpse(carver, corpse);

        Assert.Equal(parts, redrawn);
    }
}
