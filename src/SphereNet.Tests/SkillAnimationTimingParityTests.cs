using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Skill and combat animations, sounds and timings against Source-X.
///
/// The field report: mining with a pickaxe put a bag in the pack at once and no
/// animation played. The tool path resolved the skill in a single call - no
/// @SkillStart, no DELAY, no strokes - so the pack, which switches the engine's
/// mining animation off (FLAGS=skf_gather|skf_noanim) and plays its own from
/// @SkillStart, never got to play it, and the swing finished the moment the rock
/// was picked. (The bag is the pack's own reward: its rock REGIONTYPE reaps
/// i_bag_random_armor_ore_plate_* definitions drawn as ID=I_BAG.)
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SkillAnimationTimingParityTests
{
    private const byte AnimPacket = 0x6E;
    private const byte SoundPacket = 0x54;
    private const byte ContainerItemPacket = 0x25;

    private static void LoadDefinitions(string contents)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_skillanim_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, contents);
        try
        {
            var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
            {
                ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
            };
            resources.LoadResourceFile(tempFile);
            new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        }
        finally
        {
            try { File.Delete(tempFile); } catch (IOException) { }
        }
    }

    private static ushort U16(PacketBuffer p, int at) => (ushort)((p.Span[at] << 8) | p.Span[at + 1]);

    private sealed class MiningRig
    {
        public required GameWorld World;
        public required GameClient Client;
        public required Character Miner;
        public required Item Pack;
        public required Item Pick;
        public readonly List<PacketBuffer> Packets = [];
        public readonly List<string> Order = [];
    }

    private static MiningRig SetupMining(string flags, int? scriptStrokes)
    {
        LoadDefinitions($"""
            [SKILL 45]
            DEFNAME=Skill_Mining
            KEY=Mining
            DELAY=1
            FLAGS={flags}
            RANGE=2

            [ITEMDEF 019b7]
            DEFNAME=i_ore_iron
            NAME=Iron Ore

            [REGIONRESOURCE mr_anim_ore]
            DEFNAME=mr_anim_ore
            AMOUNT=50
            REAP=019b7
            REAPAMOUNT=1
            SKILL=0.0

            [REGIONTYPE r_anim_rock t_rock]
            RESOURCES=100.0 mr_anim_ore
            """);

        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 24_501);
        var miner = world.CreateCharacter();
        miner.IsPlayer = true;
        miner.BodyId = 0x0190;
        miner.SetSkill(SkillType.Mining, 1000);
        world.PlaceCharacter(miner, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, miner);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        miner.Equip(pack, Layer.Pack);
        var pick = world.CreateItem();
        pick.BaseId = 0x0E85;
        pick.ItemType = ItemType.WeaponMacePick;
        miner.Equip(pick, Layer.OneHanded);

        var rig = new MiningRig { World = world, Client = client, Miner = miner, Pack = pack, Pick = pick };

        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillStart", (_, a) =>
        {
            rig.Order.Add($"start:{a.N1}:{a.Locals?.GetInt("GatherStrokeCnt")}");
            // What the live pack does: LOCAL.GatherStrokeCnt=2 in @SkillStart.
            if (scriptStrokes.HasValue)
                a.Locals?.SetInt("GatherStrokeCnt", scriptStrokes.Value);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillStroke", (_, a) =>
        {
            rig.Order.Add($"stroke:{a.Locals?.GetInt("Strokes")}");
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillSuccess", (_, a) =>
        {
            rig.Order.Add($"success:{a.N1}");
            return TriggerResult.Default;
        });
        client.SetEngines(skillHandlers: new SkillHandlers(world, new GatheringEngine(world)),
            triggerDispatcher: dispatcher);
        client.BroadcastNearby = (_, _, packet, _) => rig.Packets.Add(packet.Build());
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        return rig;
    }

    private static void DigAt(MiningRig rig, short x, short y)
    {
        rig.Client.HandleDoubleClick(rig.Pick.Uid.Value);
        rig.Client.HandleTargetResponse(1, rig.Client.ActiveTargetCursorId, 0, x, y, 0, 0x053E);
    }

    private static void RunStrokes(MiningRig rig)
    {
        for (int i = 0; i < 20 && rig.Miner.HasActiveSkillPending(); i++)
        {
            rig.Miner.SetSkillStrokeNext(0);   // make the next stroke due now
            rig.Client.TickPendingSkill();
        }
    }

    private static bool PackHasOre(MiningRig rig) => rig.Pack.Contents.Any(i => i.BaseId == 0x19B7);

    [Fact]
    public void PickaxeOnRock_RunsTheSkill_StartThenStrokes_NotAnInstantResult()
    {
        var rig = SetupMining("skf_gather", scriptStrokes: 3);

        DigAt(rig, 101, 100);

        // Skill_Start, not a one-call resolve: nothing is dug yet, the skill is running,
        // and @SkillStart saw ARGN1 = the skill with the rolled stroke count.
        Assert.True(rig.Miner.HasActiveSkillPending());
        Assert.False(PackHasOre(rig));
        Assert.Matches(@"^start:45:[2-6]$", rig.Order[0]);

        RunStrokes(rig);

        Assert.False(rig.Miner.HasActiveSkillPending());
        Assert.True(PackHasOre(rig));
        // The script's LOCAL.GatherStrokeCnt=3 is the stroke count: three strokes,
        // counting down, and the count reaching zero is the success.
        Assert.Equal(["stroke:3", "stroke:2", "stroke:1", "success:45"], rig.Order.Skip(1));

        // One pick swing and one pick sound at the start, and one of each per stroke
        // (Skill_Start :4543-4555, Skill_Stroke :3615-3618) - and none at the result.
        var anims = rig.Packets.Where(p => p.Span[0] == AnimPacket).ToList();
        var sounds = rig.Packets.Where(p => p.Span[0] == SoundPacket).ToList();
        Assert.Equal(4, anims.Count);
        Assert.All(anims, a => Assert.Equal((ushort)AnimationType.Attack1HBash, U16(a, 5)));
        Assert.Equal(4, sounds.Count);
        Assert.All(sounds, s => Assert.Contains(U16(s, 2), new ushort[] { 0x125, 0x126 }));
    }

    [Fact]
    public void NoAnimFlag_KeepsTheSounds_AndLeavesTheAnimationToTheScript()
    {
        // The live pack: FLAGS=skf_gather|skf_noanim, @SkillStart plays ANIM itself.
        var rig = SetupMining("skf_gather|skf_noanim", scriptStrokes: 2);

        DigAt(rig, 101, 100);
        RunStrokes(rig);

        Assert.True(PackHasOre(rig));
        Assert.StartsWith("start:45:", rig.Order[0]);   // the script's hook ran
        Assert.DoesNotContain(rig.Packets, p => p.Span[0] == AnimPacket);
        Assert.Equal(3, rig.Packets.Count(p => p.Span[0] == SoundPacket));
    }

    [Fact]
    public void StrokeAnimationsAndSounds_AreUpstreamsTables()
    {
        // Skill_GetAnim / Skill_GetSound (CCharSkill.cpp:3526-3569).
        Assert.Equal((ushort)AnimationType.Attack2HSlash, SkillEngine.GetSkillAnim(SkillType.Lumberjacking));
        Assert.Equal((ushort)AnimationType.Attack1HBash, SkillEngine.GetSkillAnim(SkillType.Mining));
        Assert.Equal((ushort)AnimationType.Attack2HBash, SkillEngine.GetSkillAnim(SkillType.Fishing));
        Assert.Equal((ushort)AnimationType.AttackWeapon, SkillEngine.GetSkillAnim(SkillType.Blacksmithing));
        Assert.Null(SkillEngine.GetSkillAnim(SkillType.Tailoring));
        Assert.Equal(0x13E, SkillEngine.GetSkillSound(SkillType.Lumberjacking));
        Assert.Equal(0x055, SkillEngine.GetSkillSound(SkillType.Bowcraft));
        Assert.Equal(0, SkillEngine.GetSkillSound(SkillType.Tinkering));

        // Crafts: only smithing animates; tinkering and cooking are silent.
        Assert.Equal(((ushort)0, (ushort)0x248), ClientWorldFeaturesHandler.GetCraftAnimAndSound(SkillType.Tailoring));
        Assert.Equal(((ushort)0, (ushort)0), ClientWorldFeaturesHandler.GetCraftAnimAndSound(SkillType.Tinkering));
        Assert.Equal(((ushort)0, (ushort)0), ClientWorldFeaturesHandler.GetCraftAnimAndSound(SkillType.Cooking));
    }

    [Fact]
    public void SmithingSwing_IsTheHammersOwnSwing()
    {
        // UpdateAnimate translates ANIM_ATTACK_WEAPON through the weapon in hand
        // (GenerateAnimate, CCharAct.cpp:811-850): a smith's hammer bashes.
        var world = TestHarness.CreateWorld();
        var smith = world.CreateCharacter();
        smith.BodyId = 0x0190;
        world.PlaceCharacter(smith, new Point3D(100, 100, 0, 0));
        var hammer = world.CreateItem();
        hammer.ItemType = ItemType.WeaponMaceSmith;
        smith.Equip(hammer, Layer.OneHanded);

        Assert.Equal((ushort)AnimationType.Attack1HBash,
            BodyAnimTranslator.Generate(smith, (ushort)AnimationType.AttackWeapon));
    }

    [Fact]
    public void BracedReap_PicksOneDefinition_InsteadOfReapingNothing()
    {
        // REAP={ a 1 b 1 } is an expression upstream (ResourceGetIndexType ->
        // Exp_GetDWVal -> GetRangeNumber); read as one defname it named nothing and
        // every vein that drew it was barren.
        LoadDefinitions("""
            [ITEMDEF 0f13]
            DEFNAME=i_gem_ruby_t

            [ITEMDEF 0f26]
            DEFNAME=i_gem_diamond_t

            [REGIONRESOURCE mr_gems_t]
            DEFNAME=mr_gems_t
            AMOUNT=1,4
            REAP={ i_gem_ruby_t 1 i_gem_diamond_t 1 }
            SKILL=0.0,100.0
            """);

        var rid = DefinitionLoader.StaticResources!.ResolveDefName("mr_gems_t");
        var gems = DefinitionLoader.GetRegionResourceDef(rid.Index);
        Assert.NotNull(gems);
        Assert.Contains(gems!.Reap, new ushort[] { 0x0F13, 0x0F26 });
        Assert.Equal(gems.Reap, gems.ReapDefIndex);
    }

    [Fact]
    public void DeliveredItem_IsNotAddedTwice_ByTheDirtyDrainCatchUp()
    {
        var rig = SetupMining("skf_gather", scriptStrokes: 1);
        TestHarness.ClearQueuedPackets(rig.Client.NetState);

        var ore = rig.World.CreateItem();
        ore.BaseId = 0x19B7;
        new GameClient.InfoSkillSink(rig.Client, rig.Miner).DeliverItem(ore);

        // The world's dirty drain catches up on the new item's container flag.
        rig.Client.SendItemVisualUpdate(ore, fromDirtyDrain: true);
        Assert.Equal(1, TestHarness.GetQueuedPackets(rig.Client.NetState).Count(p => p.Span[0] == ContainerItemPacket));

        // A real change still goes out, and a caller asking for a redraw gets one.
        ore.Amount = 5;
        rig.Client.SendItemVisualUpdate(ore, fromDirtyDrain: true);
        rig.Client.SendItemVisualUpdate(ore);
        Assert.Equal(3, TestHarness.GetQueuedPackets(rig.Client.NetState).Count(p => p.Span[0] == ContainerItemPacket));
    }

    [Fact]
    public void CombatSwing_FollowsGenerateAnimate()
    {
        var world = TestHarness.CreateWorld();
        var fighter = world.CreateCharacter();
        fighter.BodyId = 0x0190;
        world.PlaceCharacter(fighter, new Point3D(100, 100, 0, 0));

        // Bare hands swing ANIM_ATTACK_WEAPON (CCharFight.cpp:1925, CCharAct.cpp:814).
        Assert.Equal((ushort)AnimationType.AttackWeapon, GameClient.GetSwingAction(fighter, null));

        var halberd = world.CreateItem();
        halberd.ItemType = ItemType.WeaponSword;
        fighter.Equip(halberd, Layer.TwoHanded);
        Assert.Equal((ushort)AnimationType.Attack2HSlash, GameClient.GetSwingAction(fighter, halberd));

        // On horseback: a two-handed swing is the slap, bare hands the attack
        // (CCharAct.cpp:876-883).
        fighter.SetStatFlag(StatFlag.OnHorse);
        Assert.Equal((ushort)AnimationType.HorseSlap, GameClient.GetSwingAction(fighter, halberd));
        Assert.Equal((ushort)AnimationType.HorseAttack, GameClient.GetSwingAction(fighter, null));

        // A creature swings its own attack groups (monster 4-6).
        var ogre = world.CreateCharacter();
        ogre.BodyId = 0x0001;
        world.PlaceCharacter(ogre, new Point3D(102, 100, 0, 0));
        Assert.InRange(GameClient.GetSwingAction(ogre, null), (ushort)4, (ushort)6);
    }

    [Fact]
    public void GetHit_IsSkippedForAKillingBlow_AndDuringTheTargetsOwnSwing()
    {
        var world = TestHarness.CreateWorld();
        var target = world.CreateCharacter();
        world.PlaceCharacter(target, new Point3D(100, 100, 0, 0));
        target.MaxHits = 50;
        target.Hits = 20;
        Assert.True(CombatHelper.ShouldPlayGetHit(target));

        target.Hits = 0;   // the blow that kills does not flinch (CCharFight.cpp:1054-1058)
        Assert.False(CombatHelper.ShouldPlayGetHit(target));
    }

    [Fact]
    public void CastGesture_IsTheSpellsOwn_NotChosenByTheTarget()
    {
        var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.MaxMana = caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        caster.Equip(pack, Layer.Pack);
        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        pack.AddItem(book);
        book.TryLearnSpell((int)SpellType.Heal);
        book.TryLearnSpell((int)SpellType.Strength);

        var registry = new SpellRegistry();
        registry.Register(new SpellDef { Id = SpellType.Heal, ManaCost = 1, CastTimeBase = 5,
            Flags = SpellFlag.Heal | SpellFlag.TargChar | SpellFlag.DirAnim });
        registry.Register(new SpellDef { Id = SpellType.Strength, ManaCost = 1, CastTimeBase = 5,
            Flags = SpellFlag.Good | SpellFlag.NoCastAnim });
        var engine = new SpellEngine(world, registry);
        var played = new List<ushort>();
        engine.OnCastAnimation = (_, anim) => played.Add(anim);
        var oldMagic = Character.MagicFlags;
        Character.MagicFlags = 0;
        try
        {
            // A self-cast of a DIR_ANIM spell is still the directed gesture
            // (Spell_CastStart, CCharSpell.cpp:3566-3567).
            engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position);
            Assert.Equal([(ushort)AnimationType.CastDirected], played);

            caster.ClearCastState(notifyAbort: false);
            caster.Mana = 100;
            played.Clear();
            engine.CastStart(caster, SpellType.Strength, caster.Uid, caster.Position);
            Assert.Empty(played);   // SPELLFLAG_NO_CASTANIM
        }
        finally
        {
            Character.MagicFlags = oldMagic;
            logs.Dispose();
        }
    }
}
