using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Magic behaviour pinned to Source-X CCharSpell.cpp: the tithing bill of a cast
/// (Spell_CanCast LOCAL.TithingUse, Spell_CastFail LOCAL.TithingLoss), the fail
/// effect locals, @Success LOCAL.FollowerSlotsOverride, Spell_Dispel sparing
/// ATTR_MOVE_NEVER memories, and Spell_Teleport sending a jailed caster back to
/// the cell.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellTithingDispelJailParityTests
{
    private static (GameWorld World, SpellEngine Engine, Character Caster, Character Target)
        Setup(ScriptRuntimeStack? stack, params SpellDef[] defs)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var registry = new SpellRegistry();
        foreach (var d in defs)
            registry.Register(d);
        var engine = new SpellEngine(world, registry) { TriggerDispatcher = stack?.Dispatcher };

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.Player;
        caster.Str = 100; caster.MaxHits = 100; caster.Hits = 100;
        caster.Int = 100; caster.MaxMana = 100; caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.Str = 100; target.MaxHits = 100; target.Hits = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (world, engine, caster, target);
    }

    private static (ScriptRuntimeStack Stack, string Path) Scripts(string text)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sxmagic2-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, text);
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(path);
        return (stack, path);
    }

    private static SpellDef Flat(SpellType id, SpellFlag flags, int effect = 0, int durationTenths = 0,
        ushort tithing = 0) => new()
    {
        Id = id,
        Name = id.ToString(),
        Flags = flags,
        ManaCost = 0,
        TithingCost = tithing,
        CastTimeBase = 1,
        EffectBase = effect,
        EffectScale = effect,
        DurationBase = durationTenths,
        DurationScale = durationTenths,
    };

    // --- tithing ---------------------------------------------------------------

    [Fact]
    public void TithingUseIsCheckedAtCastStartAndSpentWhenTheCastLands()
    {
        Character.SpellbookRequiredEnabled = false;
        var (_, engine, caster, target) = Setup(null,
            Flat(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal, effect: 10, tithing: 7));
        var messages = new List<string>();
        engine.OnSysMessage = (_, m) => messages.Add(m);

        caster.Tithing = 5;
        Assert.Equal(-1, engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position));
        Assert.Contains(messages, m => m.Contains('7'));

        caster.Tithing = 10;
        Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
        caster.CastSkillSucceeded = true;
        Assert.True(engine.CastDone(caster));
        Assert.Equal(3, caster.Tithing);
    }

    [Fact]
    public void SelectLocalTithingUseReplacesTheBill()
    {
        Character.SpellbookRequiredEnabled = false;
        var (stack, path) = Scripts($"[SPELL {(int)SpellType.Heal}]\nON=@Select\nlocal.TithingUse=2\n");
        try
        {
            var (_, engine, caster, target) = Setup(stack,
                Flat(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal, effect: 10, tithing: 7));
            caster.Tithing = 5; // short of the def's 7, enough for the script's 2

            Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
            caster.CastSkillSucceeded = true;
            Assert.True(engine.CastDone(caster));
            Assert.Equal(3, caster.Tithing);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AWandCastOwesNoTithing()
    {
        // Calc_SpellTithingCost only bills a cast from the caster's own power.
        Character.SpellbookRequiredEnabled = false;
        var (world, engine, caster, target) = Setup(null,
            Flat(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal, effect: 10, tithing: 7));
        caster.Tithing = 0;
        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        caster.Equip(wand, Layer.OneHanded);
        caster.SetTag("WAND_UID", wand.Uid.Value.ToString());

        Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
    }

    [Fact]
    public void FailLocalTithingLossIsWhatAnAbortedCastLoses()
    {
        Character.SpellbookRequiredEnabled = false;
        bool saved = Character.ReagentLossAbort;
        var (stack, path) = Scripts($"[SPELL {(int)SpellType.Heal}]\nON=@Fail\nlocal.TithingLoss=2\n");
        try
        {
            Character.ReagentLossAbort = true;
            var (_, engine, caster, target) = Setup(stack,
                Flat(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal, effect: 10, tithing: 4));
            caster.Tithing = 10;

            Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
            target.SetStatFlag(StatFlag.Dead);   // the cast aborts at completion
            caster.CastSkillSucceeded = true;
            Assert.False(engine.CastDone(caster));

            Assert.Equal(8, caster.Tithing);
        }
        finally
        {
            Character.ReagentLossAbort = saved;
            File.Delete(path);
        }
    }

    [Fact]
    public void AnAbortedCastLosesTheDefsTithingOnlyUnderReagentLossAbort()
    {
        Character.SpellbookRequiredEnabled = false;
        bool saved = Character.ReagentLossAbort;
        try
        {
            foreach (bool loss in new[] { false, true })
            {
                Character.ReagentLossAbort = loss;
                var (_, engine, caster, target) = Setup(null,
                    Flat(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal, effect: 10, tithing: 4));
                caster.Tithing = 10;
                Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
                target.SetStatFlag(StatFlag.Dead);
                caster.CastSkillSucceeded = true;
                Assert.False(engine.CastDone(caster));
                Assert.Equal(loss ? 6 : 10, caster.Tithing);
            }
        }
        finally { Character.ReagentLossAbort = saved; }
    }

    // --- the fail effect -------------------------------------------------------

    private static List<PacketWriter> CaptureBroadcasts()
    {
        var sent = new List<PacketWriter>();
        Character.BroadcastNearby = (_, _, p, _) => sent.Add(p);
        return sent;
    }

    [Fact]
    public void AFailedCastShowsTheFailEffectAndFizzleSound()
    {
        var (_, engine, caster, target) = Setup(null,
            Flat(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal, effect: 10));
        caster.IsPlayer = false;
        var sent = CaptureBroadcasts();
        var sounds = new List<ushort>();
        engine.OnPlaySound = (_, s) => sounds.Add(s);

        Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
        target.SetStatFlag(StatFlag.Dead);
        caster.CastSkillSucceeded = true;
        Assert.False(engine.CastDone(caster));

        Assert.Single(sent.OfType<PacketEffect>());
        Assert.Contains((ushort)0x5C, sounds);
    }

    [Fact]
    public void FailLocalsRecolourOrSuppressTheFailEffect()
    {
        var (stack, path) = Scripts(
            $"[SPELL {(int)SpellType.Heal}]\nON=@Fail\nlocal.EffectColor=33\n" +
            $"[SPELL {(int)SpellType.Cure}]\nON=@Fail\nlocal.CreateObject1=0\n");
        try
        {
            var (_, engine, caster, target) = Setup(stack,
                Flat(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal, effect: 10),
                Flat(SpellType.Cure, SpellFlag.TargChar));
            caster.IsPlayer = false;
            var sent = CaptureBroadcasts();

            Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
            target.SetStatFlag(StatFlag.Dead);
            caster.CastSkillSucceeded = true;
            Assert.False(engine.CastDone(caster));
            Assert.Single(sent.OfType<PacketEffectHued>());
            Assert.Empty(sent.OfType<PacketEffect>());

            sent.Clear();
            Assert.True(engine.CastStart(caster, SpellType.Cure, target.Uid, target.Position) > 0);
            caster.CastSkillSucceeded = true;
            Assert.False(engine.CastDone(caster));
            Assert.Empty(sent.OfType<PacketEffect>());
            Assert.Empty(sent.OfType<PacketEffectHued>());
        }
        finally { File.Delete(path); }
    }

    // --- FollowerSlotsOverride -------------------------------------------------

    private static (GameWorld World, Character Caster, bool Cast) SummonWithOverride(int slots)
    {
        var (stack, path) = Scripts(
            $"[SPELL {(int)SpellType.SummonDaemon}]\nON=@Success\nlocal.FollowerSlotsOverride={slots}\n");
        try
        {
            var (world, engine, caster, _) = Setup(stack,
                Flat(SpellType.SummonDaemon, SpellFlag.Summon | SpellFlag.TargXYZ, durationTenths: 600));
            GameClient.ServerOptionFlags |= OptionFlags.PetSlots;
            caster.IsPlayer = false;
            caster.MaxFollower = 5;
            Assert.True(engine.CastStart(caster, SpellType.SummonDaemon, Serial.Invalid,
                new Point3D(102, 102, 0, 0)) > 0);
            caster.CastSkillSucceeded = true;
            return (world, caster, engine.CastDone(caster));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SuccessFollowerSlotsOverrideIsWhatTheSummonIsWeighedAt()
    {
        // Six slots do not fit under a cap of five: refused (Spell_Summon_Try :2662).
        var (world, caster, cast) = SummonWithOverride(6);
        Assert.False(cast);
        Assert.DoesNotContain(world.GetAllCharactersSnapshot(), c => c.HasOwner(caster.Uid));

        // Three do, and the summon counts at three while it serves.
        (world, caster, cast) = SummonWithOverride(3);
        Assert.True(cast);
        var summon = Assert.Single(world.GetAllCharactersSnapshot(), c => c.HasOwner(caster.Uid));
        Assert.Equal(3, summon.ControlSlots);
    }

    // --- Dispel and ATTR_MOVE_NEVER ------------------------------------------------

    private static Item SpellMemory(Character ch) =>
        Assert.Single(ch.Memories, m => m.ItemType == ItemType.Spell);

    [Fact]
    public void APlayersDispelSparesAMoveNeverMemoryAndAGmsDoesNot()
    {
        var (_, engine, caster, target) = Setup(null,
            Flat(SpellType.Bless, SpellFlag.TargChar | SpellFlag.Good, effect: 5, durationTenths: 600),
            Flat(SpellType.Dispel, SpellFlag.TargChar));
        Character.MagicFlags = 0;

        engine.ApplyDirectEffect(caster, target, SpellType.Bless, 500);
        Assert.Equal(105, target.Str);
        SpellMemory(target).SetAttr(ObjAttributes.Move_Never);

        // Level 50 (CCharSpell.cpp:3949) is <= 100: the memory stays (:90).
        engine.ApplyDirectEffect(caster, target, SpellType.Dispel, 500);
        Assert.Equal(105, target.Str);

        // A GM's dispel is level 150 and takes it.
        caster.PrivLevel = PrivLevel.GM;
        engine.ApplyDirectEffect(caster, target, SpellType.Dispel, 500);
        Assert.Equal(100, target.Str);
    }

    [Fact]
    public void AnOrdinaryMemoryIsStillDispelled()
    {
        var (_, engine, caster, target) = Setup(null,
            Flat(SpellType.Bless, SpellFlag.TargChar | SpellFlag.Good, effect: 5, durationTenths: 600),
            Flat(SpellType.Dispel, SpellFlag.TargChar));
        Character.MagicFlags = 0;

        engine.ApplyDirectEffect(caster, target, SpellType.Bless, 500);
        engine.ApplyDirectEffect(caster, target, SpellType.Dispel, 500);
        Assert.Equal(100, target.Str);
    }

    [Fact]
    public void DeathSparesAMoveNeverMemory()
    {
        // CChar::Death runs Spell_Dispel(100) (CCharAct.cpp:4397).
        var (_, engine, caster, target) = Setup(null,
            Flat(SpellType.Bless, SpellFlag.TargChar | SpellFlag.Good, effect: 5, durationTenths: 600));
        Character.MagicFlags = 0;

        engine.ApplyDirectEffect(caster, target, SpellType.Bless, 500);
        SpellMemory(target).SetAttr(ObjAttributes.Move_Never);
        engine.StripDispellableEffects(target);
        Assert.Equal(105, target.Str);
    }

    [Fact]
    public void MoveNeverSurvivesTheEffectRecordAndOlderRecordsStillLoad()
    {
        var (world, engine, caster, target) = Setup(null,
            Flat(SpellType.Bless, SpellFlag.TargChar | SpellFlag.Good, effect: 5, durationTenths: 600),
            Flat(SpellType.Dispel, SpellFlag.TargChar));
        Character.MagicFlags = 0;
        engine.ApplyDirectEffect(caster, target, SpellType.Bless, 500);
        SpellMemory(target).SetAttr(ObjAttributes.Move_Never);

        string record = Assert.Single(engine.GetPersistedEffectRecords(target, Environment.TickCount64));
        var fields = record.Split('|');
        Assert.Equal(22, fields.Length);
        Assert.Equal("1", fields[21]);

        // Reload onto a fresh character: the memory comes back MOVE_NEVER and a
        // player's dispel still spares it.
        var reloaded = world.CreateCharacter();
        reloaded.IsPlayer = true;
        reloaded.Str = 100;
        world.PlaceCharacter(reloaded, new Point3D(102, 100, 0, 0));
        reloaded.AddPendingSpellEffectRecord(record);
        Assert.Equal(1, engine.RestorePersistedEffects(reloaded));
        Assert.True(SpellMemory(reloaded).IsAttr(ObjAttributes.Move_Never));
        engine.ApplyDirectEffect(caster, reloaded, SpellType.Dispel, 500);
        Assert.Equal(105, reloaded.Str);

        // A 21-field record from before the flag existed loads as dispellable.
        var older = world.CreateCharacter();
        older.IsPlayer = true;
        older.Str = 100;
        world.PlaceCharacter(older, new Point3D(103, 100, 0, 0));
        older.AddPendingSpellEffectRecord(string.Join('|', fields.Take(21)));
        Assert.Equal(1, engine.RestorePersistedEffects(older));
        Assert.False(SpellMemory(older).IsAttr(ObjAttributes.Move_Never));
        engine.ApplyDirectEffect(caster, older, SpellType.Dispel, 500);
        Assert.Equal(100, older.Str);
    }

    // --- PRIV_JAILED travel ------------------------------------------------------

    private static Region JailRegion(string name, int x, int y, int z)
    {
        var r = new Region { Name = name, MapIndex = 0, P = new Point3D((short)x, (short)y, (sbyte)z, 0) };
        r.AddRect((short)(x - 5), (short)(y - 5), (short)(x + 5), (short)(y + 5));
        return r;
    }

    private static Item Rune(GameWorld world, Character caster, Point3D mark)
    {
        var rune = world.CreateItem();
        rune.SetRuneMark(mark);
        world.PlaceItem(rune, new Point3D((short)(caster.X + 1), caster.Y, 0, 0));
        return rune;
    }

    private static bool Cast(SpellEngine engine, Character caster, SpellType spell, Serial uid, Point3D pos)
    {
        Assert.True(engine.CastStart(caster, spell, uid, pos) > 0);
        caster.CastSkillSucceeded = true;
        return engine.CastDone(caster);
    }

    [Fact]
    public void AJailedCastersRecallLandsInTheJailCell()
    {
        Character.SpellbookRequiredEnabled = false;
        var (world, engine, caster, _) = Setup(null, Flat(SpellType.Recall, SpellFlag.TargObj));
        world.AddRegion(JailRegion("jail", 2000, 2000, 5));
        caster.SetJailState(true);

        Cast(engine, caster, SpellType.Recall, Rune(world, caster, new Point3D(300, 310, 0, 0)).Uid, default);

        Assert.Equal(2000, caster.X);
        Assert.Equal(2000, caster.Y);
    }

    [Fact]
    public void AJailedCastersRecallUsesTheAccountsJailCell()
    {
        Character.SpellbookRequiredEnabled = false;
        var (world, engine, caster, _) = Setup(null, Flat(SpellType.Recall, SpellFlag.TargObj));
        world.AddRegion(JailRegion("jail", 2000, 2000, 5));
        world.AddRegion(JailRegion("jail2", 2100, 2100, 5));
        caster.SetJailState(true, cell: 2);

        Cast(engine, caster, SpellType.Recall, Rune(world, caster, new Point3D(300, 310, 0, 0)).Uid, default);

        Assert.Equal(2100, caster.X);
        Assert.Equal(2100, caster.Y);
    }

    [Fact]
    public void AJailedCasterMayRecallWithinTheJail()
    {
        Character.SpellbookRequiredEnabled = false;
        var (world, engine, caster, _) = Setup(null, Flat(SpellType.Recall, SpellFlag.TargObj));
        world.AddRegion(JailRegion("jail", 2000, 2000, 5));
        caster.SetJailState(true);

        // The jail region has no RECALL_IN block, so its RECALLIN reads 1 (CRegion.cpp:368).
        Cast(engine, caster, SpellType.Recall, Rune(world, caster, new Point3D(2003, 2002, 0, 0)).Uid, default);

        Assert.Equal(2003, caster.X);
        Assert.Equal(2002, caster.Y);
    }

    [Fact]
    public void RegionRecallInReadsAndWritesTheInverseOfTheBlockFlag()
    {
        // CRegion.cpp:368-370 / :568-570: RECALLIN is "may recall in".
        var region = new Region { Name = "town", MapIndex = 0 };
        Assert.True(region.TryGetProperty("RECALLIN", out string? v));
        Assert.Equal("1", v);

        region.TrySetProperty("RECALLIN", "0");
        Assert.True(region.IsFlag(RegionFlag.Recall));
        region.TryGetProperty("RECALLIN", out v);
        Assert.Equal("0", v);

        region.TrySetProperty("RECALLIN", "1");
        Assert.False(region.IsFlag(RegionFlag.Recall));
    }

    [Fact]
    public void RegionMarkIsTheSameFlagAsRecallIn()
    {
        // CRegion.cpp:367-369 / :567-569: MARK and RECALLIN share REGION_ANTIMAGIC_RECALL_IN.
        var region = new Region { Name = "town", MapIndex = 0 };
        region.TrySetProperty("MARK", "0");
        Assert.True(region.IsFlag(RegionFlag.Recall));
        Assert.True(region.TryGetProperty("RECALLIN", out string? v));
        Assert.Equal("0", v);
        region.TrySetProperty("RECALLIN", "1");
        region.TryGetProperty("MARK", out v);
        Assert.Equal("1", v);
    }

    [Fact]
    public void AJailedCasterCannotOpenAGateEvenIntoTheJail()
    {
        // Spell_CreateGate refuses PRIV_JAILED outright (CCharSpell.cpp:265-276).
        Character.SpellbookRequiredEnabled = false;
        var (world, engine, caster, _) = Setup(null, Flat(SpellType.GateTravel, SpellFlag.TargObj));
        world.AddRegion(JailRegion("jail", 2000, 2000, 5));
        caster.SetJailState(true);

        Cast(engine, caster, SpellType.GateTravel, Rune(world, caster, new Point3D(2003, 2002, 0, 0)).Uid, default);

        Assert.DoesNotContain(world.GetItemsInRange(caster.Position, 2), i => i.ItemType == ItemType.Telepad);
    }

    [Fact]
    public void AJailedCastersTeleportLandsInTheJailCell()
    {
        Character.SpellbookRequiredEnabled = false;
        var (world, engine, caster, _) = Setup(null, Flat(SpellType.Teleport, SpellFlag.TargXYZ));
        world.AddRegion(JailRegion("jail", 2000, 2000, 5));
        caster.SetJailState(true);

        Cast(engine, caster, SpellType.Teleport, Serial.Invalid, new Point3D(105, 104, 0, 0));

        Assert.Equal(2000, caster.X);
        Assert.Equal(2000, caster.Y);
    }

    [Fact]
    public void AFreeCastersTeleportGoesWhereAimed()
    {
        Character.SpellbookRequiredEnabled = false;
        var (world, engine, caster, _) = Setup(null, Flat(SpellType.Teleport, SpellFlag.TargXYZ));
        world.AddRegion(JailRegion("jail", 2000, 2000, 5));

        Cast(engine, caster, SpellType.Teleport, Serial.Invalid, new Point3D(105, 104, 0, 0));

        Assert.Equal(105, caster.X);
        Assert.Equal(104, caster.Y);
    }
}
