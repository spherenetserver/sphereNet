using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Three roads into and out of a spell memory that used to skip the common
/// Spell_Effect_Add / Spell_Effect_Remove stages (CCharSpell.cpp:965 / :541):
/// a memory taken off its wearer without being deleted (CChar::OnRemoveObj,
/// CCharAct.cpp:560), the poison memory (SetPoison -> Spell_Effect_Create,
/// CCharAct.cpp:4192) and the summoning memory every summoned creature wears
/// (Spell_Summon_Place -> OnSpellEffect(SPELL_Summon), CCharSpell.cpp:368; its removal
/// deletes the creature, :589).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellEffectTakeOffPoisonSummonTests : IDisposable
{
    private readonly List<string> _temp = [];
    private readonly Dictionary<FieldInfo, object?> _savedServer = new();
    private readonly bool _savedReagents = Character.ReagentsRequiredEnabled;
    private readonly bool _savedSpellbook = Character.SpellbookRequiredEnabled;

    public void Dispose()
    {
        Character.ReagentsRequiredEnabled = _savedReagents;
        Character.SpellbookRequiredEnabled = _savedSpellbook;
        foreach (var (field, value) in _savedServer) field.SetValue(null, value);
        foreach (var path in _temp)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else File.Delete(path);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ fixture

    private static GameWorld World()
    {
        var w = TestHarness.CreateWorld();
        Character.ResolveCharByUid = w.FindChar;
        Character.MagicFlags = 0;
        return w;
    }

    private static Character Char(GameWorld w, int x = 100, bool player = true)
    {
        var c = w.CreateCharacter();
        c.IsPlayer = player;
        c.BodyId = 0x190;
        c.Name = "Probe";
        c.Str = c.Dex = c.Int = 50;
        c.Hits = c.MaxHits = 100;
        c.Mana = c.MaxMana = 100;
        c.Stam = c.MaxStam = 100;
        c.SetSkill(SkillType.Magery, 1000);
        w.PlaceCharacter(c, new Point3D((short)x, 100, 0, 0));
        return c;
    }

    private static SpellDef Def(SpellType id = SpellType.Strength, int effect = 20, int duration = 600,
        SpellFlag flags = SpellFlag.Good | SpellFlag.TargChar) => new()
    {
        Id = id, Name = id.ToString(), Flags = flags,
        ManaCost = 0, CastTimeBase = 1, EffectBase = effect, EffectScale = effect,
        DurationBase = duration, DurationScale = duration, RuneItemId = 0x2085,
    };

    private static SpellDef SummonDef() => Def(SpellType.SummonCreature, 0, 600,
        SpellFlag.TargXYZ | SpellFlag.Summon);

    private static (SpellEngine E, GameWorld W, Character C, Character T, SpellRegistry R) Setup(params SpellDef[] defs)
    {
        var w = World();
        var c = Char(w);
        var t = Char(w, 101);
        var r = new SpellRegistry();
        if (defs.Length == 0) r.Register(Def());
        foreach (var d in defs) r.Register(d);
        return (new SpellEngine(w, r), w, c, t, r);
    }

    private static Item? Mem(Character c, SpellType spell) =>
        c.Memories.FirstOrDefault(m => !m.IsDeleted && m.ItemType == ItemType.Spell && m.MoreP.X == (int)spell);

    private static int Str(Character c) => CombatEngine.EffectiveStr(c);

    private static string Tag(ObjBase obj, string key) => obj.TryGetTag(key, out var v) ? v ?? "" : "missing";

    private static long Num(string raw) => ScriptNumber.TryParseToken(raw, out long n) ? n : -1;

    private static Item GroundBox(GameWorld w)
    {
        var box = w.CreateItem();
        box.ItemType = ItemType.Container;
        box.BaseId = 0x0E75;
        w.PlaceItem(box, new Point3D(103, 100, 0, 0));
        return box;
    }

    private TriggerDispatcher Script(string content, params Character[] eventHolders)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string file = Path.Combine(Path.GetTempPath(), $"magic-takeoff-{Guid.NewGuid():N}.scp");
        _temp.Add(file);
        File.WriteAllText(file, content);
        stack.Resources.LoadResourceFile(file);
        var rid = stack.Resources.ResolveDefName("e_magic");
        if (rid.IsValid)
            foreach (var ch in eventHolders)
                ch.Events.Add(rid);
        stack.Dispatcher.BuildUsedTriggerCache();
        return stack.Dispatcher;
    }

    /// <summary>The production hooks (Program.RefreshCharacterScriptHooks), as at startup.</summary>
    private void WireHost(SpellEngine e, GameWorld w, TriggerDispatcher d)
    {
        SetServer("_world", w);
        SetServer("_spellEngine", e);
        SetServer("_triggerDispatcher", d);
        typeof(SphereNet.Server.Program)
            .GetMethod("RefreshCharacterScriptHooks", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null);
        e.TriggerDispatcher = d;
    }

    private void SetServer(string name, object value)
    {
        var field = typeof(SphereNet.Server.Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        if (!_savedServer.ContainsKey(field))
            _savedServer.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private (GameWorld World, SpellEngine Engine) RoundTrip(GameWorld src, SpellRegistry registry)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"magic-takeoff-save-{Guid.NewGuid():N}");
        _temp.Add(dir);
        Directory.CreateDirectory(dir);
        var lf = LoggerFactory.Create(_ => { });
        var saver = new WorldSaver(lf) { Format = SaveFormat.Text, ShardCount = 0 };
        Assert.True(saver.Save(src, dir));

        var next = TestHarness.CreateWorld();
        Character.ResolveCharByUid = next.FindChar;
        new WorldLoader(lf).Load(next, dir);
        var engine = new SpellEngine(next, registry);
        engine.RestorePersistedEffectsFromWorld();
        return (next, engine);
    }

    private static int CountRemoves(List<(Character Owner, Item Memory)> log, Item mem) =>
        log.Count(x => ReferenceEquals(x.Memory, mem));

    // ======================================================= N01: memory take-off

    /// <summary>N01: a +20 Strength memory moved to the ground or into a container stays
    /// alive there, but leaves its wearer the way any removal does - off the memory
    /// list, @SpellEffectRemove once, the +20 taken back (CCharAct.cpp:560). It used to
    /// keep the wearer at 70 and on the list, even after its timer ran out.</summary>
    [Theory]
    [InlineData("ground")]
    [InlineData("cont")]
    [InlineData("pack")]
    public void N01_MemoryMovedOffTheWearer_IsTakenOffOnce_AndLivesOn(string where)
    {
        var (e, w, c, t, _) = Setup();
        var removes = new List<(Character, Item)>();
        Character.OnSpellEffectRemove = (owner, _, memory, _) => { removes.Add((owner, memory)); return TriggerResult.Default; };

        e.ApplyDirectEffect(c, t, SpellType.Strength, 1000);
        var mem = Mem(t, SpellType.Strength)!;
        Assert.Equal(70, Str(t));
        var box = GroundBox(w);

        switch (where)
        {
            case "ground": Assert.True(w.PlaceItem(mem, new Point3D(104, 100, 0, 0))); break;
            case "cont": Assert.True(mem.TrySetProperty("CONT", $"0{box.Uid.Value:X}")); break;
            case "pack": Assert.True(box.TryAddItem(mem)); break;
        }

        Assert.Equal(50, Str(t));
        Assert.DoesNotContain(mem, t.Memories);
        Assert.Equal(1, CountRemoves(removes, mem));
        Assert.False(mem.IsDeleted);
        Assert.False(mem.IsEquipped);
        if (where == "ground")
            Assert.False(mem.ContainedIn.IsValid);
        else
            Assert.Contains(mem, box.Contents);

        // Its timer coming due later finds no effect to end, and deleting it later
        // removes nothing twice.
        e.ProcessExpirations(Environment.TickCount64 + 120_000);
        w.DeleteObject(mem);
        Assert.Equal(50, Str(t));
        Assert.Equal(1, CountRemoves(removes, mem));
    }

    /// <summary>N01: handed to another character, the memory comes off the first one
    /// (stat back, removal once) and stays alive on the second.</summary>
    [Fact]
    public void N01_MemoryEquippedOnAnotherCharacter_LeavesTheFirstOnce()
    {
        var (e, w, c, t, _) = Setup();
        int removes = 0;
        Character.OnSpellEffectRemove = (_, _, _, _) => { removes++; return TriggerResult.Default; };
        e.ApplyDirectEffect(c, t, SpellType.Strength, 1000);
        var mem = Mem(t, SpellType.Strength)!;
        var other = Char(w, 102);

        Assert.True(other.Equip(mem, mem.EquipLayer));

        Assert.Equal(50, Str(t));
        Assert.Equal(1, removes);
        Assert.False(mem.IsDeleted);
        Assert.Equal(other.Uid, mem.ContainedIn);
        Assert.Null(Mem(t, SpellType.Strength));
        w.DeleteObject(mem);
        Assert.Equal(1, removes);
    }

    /// <summary>N01: a removal hook that moves the memory itself (into a box) while the
    /// memory is being dropped to the ground: one removal, one undo, and the move in
    /// progress decides where it lands - nothing is left listed in two places.</summary>
    [Fact]
    public void N01_ReentrantMoveFromTheRemoveHook_RemovesOnce()
    {
        var (e, w, c, t, _) = Setup();
        var box = GroundBox(w);
        int removes = 0;
        Character.OnSpellEffectRemove = (_, _, memory, _) =>
        {
            removes++;
            box.TryAddItem(memory);
            return TriggerResult.Default;
        };
        e.ApplyDirectEffect(c, t, SpellType.Strength, 1000);
        var mem = Mem(t, SpellType.Strength)!;

        Assert.True(w.PlaceItem(mem, new Point3D(104, 100, 0, 0)));

        Assert.Equal(1, removes);
        Assert.Equal(50, Str(t));
        Assert.False(mem.IsDeleted);
        Assert.False(mem.ContainedIn.IsValid);
        Assert.DoesNotContain(mem, box.Contents);
        Assert.DoesNotContain(mem, t.Memories);
    }

    /// <summary>N01: a removal hook's RETURN 0 on the take-off keeps the undo from running,
    /// as on any removal (CCharSpell.cpp:558-567) - the memory still leaves.</summary>
    [Fact]
    public void N01_TakeOffRemoveReturnZero_SkipsTheUndo()
    {
        var (e, w, c, t, _) = Setup();
        Character.OnSpellEffectRemove = (_, _, _, _) => TriggerResult.False;
        e.ApplyDirectEffect(c, t, SpellType.Strength, 1000);
        var mem = Mem(t, SpellType.Strength)!;

        Assert.True(w.PlaceItem(mem, new Point3D(104, 100, 0, 0)));

        Assert.Equal(70, Str(t));
        Assert.DoesNotContain(mem, t.Memories);
        Character.OnSpellEffectRemove = null;
        e.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(70, Str(t));                                   // nothing removes it again
    }

    /// <summary>N01: the stat that came back with the take-off is what a save keeps - the
    /// reloaded character is at 50, with the memory in the box and no effect on it.</summary>
    [Fact]
    public void N01_TakenOffMemory_SavesAndLoadsWithoutTheBonus()
    {
        var (e, w, c, t, r) = Setup();
        e.ApplyDirectEffect(c, t, SpellType.Strength, 1000);
        var mem = Mem(t, SpellType.Strength)!;
        var box = GroundBox(w);
        Assert.True(box.TryAddItem(mem));

        var (next, engine) = RoundTrip(w, r);
        var reloaded = next.FindChar(t.Uid)!;

        Assert.Equal(50, Str(reloaded));
        Assert.Null(Mem(reloaded, SpellType.Strength));
        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(50, Str(reloaded));
    }

    // ========================================================= N02: poison stages

    private static string PoisonScript(string? addVerdict = null, string? removeVerdict = null) =>
        "[EVENTS e_magic]\n" +
        "ON=@SpellEffectAdd\nTAG.CHAR_ADD=<EVAL <TAG0.CHAR_ADD>+1>\nTAG.CHAR_ADD_ARGO=<ARGO.UID>\nTAG.CHAR_ADD_SRC=<SRC.UID>\nTAG.CHAR_ADD_N1=<ARGN1>\n" +
        (addVerdict != null ? $"RETURN {addVerdict}\n" : "") +
        "ON=@SpellEffectRemove\nTAG.CHAR_REMOVE=<EVAL <TAG0.CHAR_REMOVE>+1>\nTAG.CHAR_REMOVE_ARGO=<ARGO.UID>\nTAG.CHAR_REMOVE_SRC=<SRC.UID>\n" +
        (removeVerdict != null ? $"RETURN {removeVerdict}\n" : "") +
        "[SPELL 20]\n" +
        "ON=@EffectAdd\nTAG.FX_ADD=<EVAL <TAG0.FX_ADD>+1>\n" +
        "ON=@EffectRemove\nTAG.FX_REMOVE=<EVAL <TAG0.FX_REMOVE>+1>\n";

    private (GameWorld W, Character Victim, Character Poisoner) PoisonSetup(string? addVerdict = null, string? removeVerdict = null)
    {
        var (e, w, poisoner, victim, _) = Setup();
        WireHost(e, w, Script(PoisonScript(addVerdict, removeVerdict), victim));
        return (w, victim, poisoner);
    }

    /// <summary>N02: poison runs the common add order - the character's @SpellEffectAdd
    /// (ARGO the memory, SRC the poisoner, ARGN1 20) and then [SPELL 20] @EffectAdd - and
    /// on cure the common removal: @SpellEffectRemove then @EffectRemove, once each. The
    /// character stages never ran for poison.</summary>
    [Fact]
    public void N02_Poison_RunsCharacterAndSpellStages_OnAddAndCure()
    {
        var (_, victim, poisoner) = PoisonSetup();

        Assert.True(victim.SetPoison(600, 12, poisoner));
        var mem = victim.Poison.Memory!;

        Assert.Equal("1", Tag(victim, "CHAR_ADD"));
        Assert.Equal("1", Tag(victim, "FX_ADD"));
        Assert.Equal(mem.Uid.Value, (uint)Num(Tag(victim, "CHAR_ADD_ARGO")));
        Assert.Equal(poisoner.Uid.Value, (uint)Num(Tag(victim, "CHAR_ADD_SRC")));
        Assert.Equal("20", Tag(victim, "CHAR_ADD_N1"));
        Assert.True(victim.IsStatFlag(StatFlag.Poisoned));

        victim.Poison.Cure();

        Assert.Equal("1", Tag(victim, "CHAR_REMOVE"));
        Assert.Equal("1", Tag(victim, "FX_REMOVE"));
        Assert.Equal(mem.Uid.Value, (uint)Num(Tag(victim, "CHAR_REMOVE_ARGO")));
        Assert.Equal(poisoner.Uid.Value, (uint)Num(Tag(victim, "CHAR_REMOVE_SRC")));
        Assert.False(victim.IsStatFlag(StatFlag.Poisoned));
        Assert.True(mem.IsDeleted);
    }

    /// <summary>N02: @SpellEffectAdd RETURN 1 deletes the memory before it is ever on -
    /// no poison, and no removal stage for something that never was (:989-993).</summary>
    [Fact]
    public void N02_CharacterAddReturnOne_NoPoison_NoRemoval()
    {
        var (_, victim, poisoner) = PoisonSetup(addVerdict: "1");

        Assert.False(victim.SetPoison(600, 12, poisoner));

        Assert.Null(victim.Poison.Memory);
        Assert.False(victim.IsStatFlag(StatFlag.Poisoned));
        Assert.Equal("missing", Tag(victim, "FX_ADD"));      // RETURN 1 stops the chain
        Assert.Equal("missing", Tag(victim, "CHAR_REMOVE"));
        Assert.Equal("missing", Tag(victim, "FX_REMOVE"));
    }

    /// <summary>N02: @SpellEffectRemove RETURN 0 lets the memory go but keeps
    /// STATF_POISONED, and stops before [SPELL 20] @EffectRemove (:563-564).</summary>
    [Fact]
    public void N02_CharacterRemoveReturnZero_KeepsTheFlag()
    {
        var (_, victim, poisoner) = PoisonSetup(removeVerdict: "0");
        Assert.True(victim.SetPoison(600, 12, poisoner));
        var mem = victim.Poison.Memory!;

        victim.Poison.Cure();

        Assert.True(mem.IsDeleted);
        Assert.Equal("1", Tag(victim, "CHAR_REMOVE"));
        Assert.Equal("missing", Tag(victim, "FX_REMOVE"));
        Assert.True(victim.IsStatFlag(StatFlag.Poisoned));
    }

    /// <summary>N02: a spent poison (its last tick) and a death take the same removal,
    /// once - as does a poison memory moved off the character.</summary>
    [Theory]
    [InlineData("expiry")]
    [InlineData("delete")]
    [InlineData("move")]
    public void N02_PoisonRemoval_RunsTheStagesOnce(string how)
    {
        var (w, victim, poisoner) = PoisonSetup();
        Assert.True(victim.SetPoison(600, 1, poisoner));
        var mem = victim.Poison.Memory!;

        switch (how)
        {
            case "expiry":
                mem.More2 = 0;
                victim.Poison.EquipTick(mem);
                break;
            case "delete":
                w.DeleteObject(mem);
                break;
            case "move":
                Assert.True(w.PlaceItem(mem, new Point3D(104, 100, 0, 0)));
                break;
        }

        Assert.Equal("1", Tag(victim, "CHAR_REMOVE"));
        Assert.Equal("1", Tag(victim, "FX_REMOVE"));
        Assert.False(victim.IsStatFlag(StatFlag.Poisoned));
        Assert.Null(victim.Poison.Memory);
        if (!mem.IsDeleted)
            w.DeleteObject(mem);
        Assert.Equal("1", Tag(victim, "CHAR_REMOVE"));
    }

    /// <summary>N02: a saved poison memory loads as before - worn, flagged, no add stage
    /// run again - and its cure runs the removal stages.</summary>
    [Fact]
    public void N02_SavedPoison_LoadsWithoutAddStages_AndCuresWithThem()
    {
        var (e, w, poisoner, victim, r) = Setup();
        Assert.True(victim.SetPoison(600, 12, poisoner));

        var (next, engine) = RoundTrip(w, r);
        var reloaded = next.FindChar(victim.Uid)!;
        Assert.NotNull(reloaded.Poison.Memory);
        Assert.True(reloaded.IsStatFlag(StatFlag.Poisoned));

        WireHost(engine, next, Script(PoisonScript(), reloaded));
        Assert.Equal("missing", Tag(reloaded, "CHAR_ADD"));
        reloaded.Poison.Cure();

        Assert.Equal("1", Tag(reloaded, "CHAR_REMOVE"));
        Assert.Equal("1", Tag(reloaded, "FX_REMOVE"));
        Assert.False(reloaded.IsStatFlag(StatFlag.Poisoned));
    }

    // ======================================================== N03: summon memory

    private (SpellEngine E, GameWorld W, Character Caster, SpellRegistry R) SummonSetup(params SpellDef[] extra)
    {
        Character.ReagentsRequiredEnabled = false;
        Character.SpellbookRequiredEnabled = false;
        var defs = new List<SpellDef> { SummonDef() };
        defs.AddRange(extra);
        var (e, w, c, _, r) = Setup(defs.ToArray());
        c.SetSkill(SkillType.Magery, 1000);
        return (e, w, c, r);
    }

    private static Character CastSummon(SpellEngine e, GameWorld w, Character caster)
    {
        var before = w.GetAllObjects().OfType<Character>().Where(x => !x.IsDeleted).ToHashSet();
        Assert.True(e.CastStart(caster, SpellType.SummonCreature, caster.Uid, caster.Position) >= 0);
        Assert.True(e.CastDone(caster));
        return w.GetAllObjects().OfType<Character>().Single(x => !x.IsDeleted && !before.Contains(x));
    }

    /// <summary>N03: the summoned creature wears the generic SPELL_Summon memory on
    /// LAYER_SPELL_Summon (41), conjured, with [SPELL 40] @EffectAdd run on it - the way
    /// Spell_Summon_Place ends (CCharSpell.cpp:368, :1217). The tag lifetime stays.</summary>
    [Fact]
    public void N03_SummonedCreature_WearsTheSummonMemory_WithItsAddStage()
    {
        var (e, w, caster, _) = SummonSetup();
        e.TriggerDispatcher = Script("[SPELL 40]\nON=@EffectAdd\nTAG.SUMMON_ADDED=<ARGO.UID>\n");

        var npc = CastSummon(e, w, caster);

        var mem = npc.FindLayer(SpellLayers.Summon);
        Assert.NotNull(mem);
        Assert.Equal((int)SpellType.SummonCreature, mem!.MoreP.X);
        Assert.Equal(caster.Uid, mem.Link);
        Assert.True(mem.Timeout > Environment.TickCount64);
        Assert.Equal(mem.Uid.Value, (uint)Num(Tag(npc, "SUMMON_ADDED")));
        Assert.True(npc.IsStatFlag(StatFlag.Conjured));
        Assert.True(npc.IsSummoned);
    }

    /// <summary>N03: the summoning running out, or being dispelled, deletes the creature
    /// (CCharSpell.cpp:589-600) - once.</summary>
    [Theory]
    [InlineData("expiry")]
    [InlineData("dispel")]
    [InlineData("remove")]
    public void N03_SummonMemoryRemoval_DeletesTheCreatureOnce(string how)
    {
        var (e, w, caster, _) = SummonSetup(Def(SpellType.Dispel, 0, 0, SpellFlag.TargChar));
        var npc = CastSummon(e, w, caster);
        var mem = npc.FindLayer(SpellLayers.Summon)!;
        int deletions = 0;
        w.ObjectDeleting += o => { if (ReferenceEquals(o, npc)) deletions++; };

        switch (how)
        {
            case "expiry": e.ProcessExpirations(mem.Timeout + 1); break;
            case "dispel": e.ApplyDirectEffect(caster, npc, SpellType.Dispel, 1000); break;
            case "remove": Assert.True(e.RemoveEffectByMemory(mem)); break;
        }

        Assert.True(npc.IsDeleted);
        Assert.True(mem.IsDeleted);
        Assert.Equal(1, deletions);
    }

    /// <summary>N03: [SPELL 40] @EffectAdd RETURN 1 takes the memory away before it is
    /// worn - the creature stays, on its SUMMON_* tag lifetime. RETURN 0 from
    /// @EffectRemove keeps it alive when the memory goes (:568-576).</summary>
    [Theory]
    [InlineData("add1")]
    [InlineData("remove0")]
    public void N03_SummonStageVerdicts(string verdict)
    {
        var (e, w, caster, _) = SummonSetup();
        e.TriggerDispatcher = verdict == "add1"
            ? Script("[SPELL 40]\nON=@EffectAdd\nRETURN 1\n")
            : Script("[SPELL 40]\nON=@EffectRemove\nRETURN 0\n");

        var npc = CastSummon(e, w, caster);

        if (verdict == "add1")
        {
            Assert.Null(npc.FindLayer(SpellLayers.Summon));
            Assert.False(npc.IsDeleted);
            Assert.True(npc.IsSummoned);
            return;
        }
        var mem = npc.FindLayer(SpellLayers.Summon)!;
        e.ProcessExpirations(mem.Timeout + 1);
        Assert.True(mem.IsDeleted);
        Assert.False(npc.IsDeleted);
    }

    /// <summary>N03: a summon deleted by any other road (its tag lifetime, a GM remove)
    /// takes its memory with it without deleting the creature a second time.</summary>
    [Fact]
    public void N03_CreatureDeletedFirst_DoesNotDeleteTwice()
    {
        var (e, w, caster, _) = SummonSetup();
        var npc = CastSummon(e, w, caster);
        var mem = npc.FindLayer(SpellLayers.Summon)!;
        int deletions = 0;
        w.ObjectDeleting += o => { if (ReferenceEquals(o, npc)) deletions++; };

        w.DeleteObject(npc);

        Assert.True(npc.IsDeleted);
        Assert.True(mem.IsDeleted);
        Assert.Equal(1, deletions);
        e.ProcessExpirations(Environment.TickCount64 + 600_000);
        Assert.Equal(1, deletions);
    }

    /// <summary>N03: a summon dying keeps its body for the corpse - the death dispel takes
    /// the memory from a DEAD creature, which is not deleted again (:592-593).</summary>
    [Fact]
    public void N03_DeathDispel_DoesNotDeleteTheDeadCreature()
    {
        var (e, w, caster, _) = SummonSetup();
        var npc = CastSummon(e, w, caster);
        var mem = npc.FindLayer(SpellLayers.Summon)!;

        npc.Kill();
        e.ClearAllEffectsOnDeath(npc);

        Assert.True(mem.IsDeleted);
        Assert.False(npc.IsDeleted);
    }

    /// <summary>N03: the summoning memory saves with the creature and, loaded, still ends
    /// it - the loaded memory joins the lifecycle and its expiry deletes the creature.</summary>
    [Fact]
    public void N03_LoadedSummonMemory_StillEndsTheCreature()
    {
        var (e, w, caster, r) = SummonSetup();
        var npc = CastSummon(e, w, caster);
        Assert.NotNull(npc.FindLayer(SpellLayers.Summon));

        var (next, engine) = RoundTrip(w, r);
        var reloaded = next.FindChar(npc.Uid)!;
        var mem = reloaded.FindLayer(SpellLayers.Summon);
        Assert.NotNull(mem);
        Assert.True(reloaded.IsSummoned);                           // the tag lifetime too

        engine.ProcessExpirations(Math.Max(mem!.Timeout, Environment.TickCount64) + 1);

        Assert.True(reloaded.IsDeleted);
    }
}
