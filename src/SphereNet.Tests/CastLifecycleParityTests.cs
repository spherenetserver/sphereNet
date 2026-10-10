using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class CastLifecycleParityTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly Microsoft.Extensions.Logging.ILoggerFactory _logs = TestHarness.CreateLoggerFactory();
        public readonly GameWorld World = TestHarness.CreateWorld();
        public readonly Character Player;
        public readonly GameClient Client;
        public readonly SpellEngine Engine;
        public readonly TriggerDispatcher Triggers = new();
        public readonly SpellDef Heal = new() { Id = SpellType.Heal, ManaCost = 10, CastTimeBase = 5, Flags = SpellFlag.Heal | SpellFlag.TargChar };
        public Fixture()
        {
            Player = World.CreateCharacter(); Player.IsPlayer = true;
            Player.MaxMana = Player.Mana = 100;
            World.PlaceCharacter(Player, new Point3D(100, 100));
            var pack = World.CreateItem(); pack.ItemType = ItemType.Container;
            Player.Equip(pack, Layer.Pack);
            var registry = new SpellRegistry(); registry.Register(Heal);
            registry.Register(new SpellDef { Id = SpellType.Strength, ManaCost = 0, CastTimeBase = 5, Flags = SpellFlag.Good });
            Engine = new SpellEngine(World, registry) { TriggerDispatcher = Triggers };
            Client = TestHarness.CreateClient(_logs, World, new AccountManager(_logs), 19550);
            TestHarness.AttachCharacter(Client, Player);
            Client.SetEngines(spellEngine: Engine, triggerDispatcher: Triggers);
            Player.MaxMana = Player.Mana = 100;
            Character.MagicFlags = 0;
        }
        public Item Book()
        {
            var item = World.CreateItem(); item.ItemType = ItemType.Spellbook;
            Player.Backpack!.AddItem(item); item.TryLearnSpell((int)SpellType.Heal); return item;
        }
        public Item Wand()
        {
            var item = World.CreateItem(); item.ItemType = ItemType.Wand;
            Player.Equip(item, Layer.OneHanded);
            Player.MaxMana = Player.Mana = 100;
            return item;
        }
        public int Start() => Engine.CastStart(Player, SpellType.Heal, Player.Uid, Player.Position);
        public void Dispose() => _logs.Dispose();
    }

    [Theory]
    [InlineData("removed", false)]
    [InlineData("forgotten", false)]
    [InlineData("nested", false)]
    [InlineData("removed", true)]
    [InlineData("forgotten", true)]
    [InlineData("nested", true)]
    public void CompletionRevalidatesBookAndOnlyChargesConfiguredAbortCost(string change, bool abortLoss)
    {
        using var f = new Fixture(); var book = f.Book();
        Character.ManaLossAbort = abortLoss;
        Character.ManaLossPercent = 100;
        Assert.True(f.Start() > 0);
        if (change == "forgotten") book.More1 = 0;
        else if (change == "removed") f.World.RemoveItem(book);
        else
        {
            var bag = f.World.CreateItem(); bag.ItemType = ItemType.Container;
            f.Player.Backpack!.AddItem(bag); bag.AddItem(book);
        }
        int mana = f.Player.Mana;
        bool? resolved = null;
        f.Engine.OnCastResolved = (_, _, success) => resolved = success;
        Assert.False(f.Engine.CastDone(f.Player));
        Assert.Equal(false, resolved);
        Assert.Equal(abortLoss ? mana - f.Heal.ManaCost : mana, f.Player.Mana);
        Assert.False(f.Player.IsCasting);
        Assert.Null(f.Player.CastDifficulty);
    }

    [Theory]
    [InlineData("mana")]
    [InlineData("book")]
    [InlineData("reagent")]
    public void MerelyHoldingWandDoesNotExemptNormalCast(string missing)
    {
        using var f = new Fixture(); f.Wand();
        if (missing != "book") f.Book();
        if (missing == "mana") f.Player.Mana = 0;
        if (missing == "reagent") f.Heal.Reagents.Add(SphereNet.Game.Objects.Items.ResourceMatch.ForItemDef(0x7FFE), 1);
        Assert.Equal(-1, f.Start());
        Assert.False(f.Player.IsCasting);
    }

    [Fact]
    public void ActualWandSourceInPackUsesWandExemptions()
    {
        using var f = new Fixture(); var wand = f.Wand();
        f.Player.Unequip(Layer.OneHanded); f.Player.Backpack!.AddItem(wand);
        wand.More2 = 3; // a wand casts only with charges left (CCharSpell.cpp:2436)
        f.Player.Mana = 0; f.Heal.Reagents.Add(SphereNet.Game.Objects.Items.ResourceMatch.ForItemDef(0x7FFE), 1);
        f.Player.SetTag("WAND_UID", wand.Uid.Value.ToString());
        Assert.True(f.Start() > 0);
        Assert.Equal(1, f.Player.CastDifficulty);
    }

    [Fact]
    public void StaleSourceIsRejectedAtStart()
    {
        using var f = new Fixture(); f.Book();
        f.Player.SetTag("SCROLL_UID", "1234567");
        Assert.Equal(-1, f.Start());
        Assert.False(f.Player.TryGetTag("SCROLL_UID", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TriggerSpellDifficultyAndZeroWaitSurviveNormalAndPrecast(bool precast)
    {
        using var f = new Fixture(); f.Player.PrivLevel = PrivLevel.GM;
        if (precast) Character.MagicFlags = (int)MagicConfigFlags.Precast;
        int calls = 0;
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellCast", (_, args) =>
        {
            calls++; args.N1 = (int)SpellType.Strength; args.N2 = 42; args.N3 = 0;
            return TriggerResult.Default;
        });
        long before = Environment.TickCount64;
        f.Client.HandleCastSpell(SpellType.Heal, 0);
        // Without precast the skill starts once the target is picked
        // (OnTarg_Skill_Magery -> Skill_Start -> Spell_CastStart), and that is where
        // @SpellCast runs; with precast it runs before the cursor.
        if (!precast)
        {
            Assert.Equal(0, calls);
            f.Client.HandleTargetResponse(0, f.Client.ActiveTargetCursorId,
                f.Player.Uid.Value, 100, 100, 0, 0);
        }
        Assert.True(f.Player.TryGetCastingSpell(out var spell));
        Assert.Equal(SpellType.Strength, spell);
        Assert.Equal(42, f.Player.CastDifficulty);
        Assert.InRange(f.Player.CastTimerEnd, before, Environment.TickCount64 + 2);
        Assert.Equal(1, calls);
    }

    /// <summary>A pick with no cursor open is dropped (Event_Target, CClientEvent.cpp:2473):
    /// a Last Target pressed before any spell, or a click landing after the cursor
    /// closed, must never become the target of the next cast.</summary>
    [Fact]
    public void APickWithNoCursorOpenTargetsNothing()
    {
        using var f = new Fixture(); f.Player.PrivLevel = PrivLevel.GM;
        Assert.False(f.Client.Targets.CursorActive);
        f.Client.HandleTargetResponse(0, 12345, f.Player.Uid.Value, 100, 100, 0, 0);
        Assert.False(f.Player.IsCasting);

        // The next cast still asks for its target rather than reusing that pick.
        f.Client.HandleCastSpell(SpellType.Heal, 0);
        Assert.False(f.Player.IsCasting);
        Assert.True(f.Client.Targets.CursorActive);
    }

    /// <summary>A spell's cursor is the one CClient::addTarget sends
    /// (CClientMsg.cpp:1810): ground only for TARG_XYZ, object otherwise, flagged
    /// harmful for HARM. With a ground cursor on every spell the client's Last Target
    /// answered an object spell with the last ground point picked.</summary>
    [Theory]
    [InlineData(SpellFlag.TargChar, 0, 0)]
    [InlineData(SpellFlag.TargChar | SpellFlag.Harm, 0, 1)]
    [InlineData(SpellFlag.TargXYZ, 1, 0)]
    [InlineData(SpellFlag.TargObj | SpellFlag.TargXYZ | SpellFlag.Harm, 1, 1)]
    public void ASpellCursorAllowsGroundOnlyForTargXyzAndFlagsHarm(SpellFlag flags, int cursorType, int harmful)
    {
        using var f = new Fixture(); f.Player.PrivLevel = PrivLevel.GM;
        f.Heal.Flags = flags;
        TestHarness.GetQueuedPackets(f.Client.NetState);
        f.Client.HandleCastSpell(SpellType.Heal, 0);
        var cursor = TestHarness.GetQueuedPackets(f.Client.NetState)
            .Select(p => p.Span.ToArray()).Last(p => p[0] == 0x6C);
        Assert.Equal(cursorType, cursor[1]);
        Assert.Equal(harmful, cursor[6]);
    }

    /// <summary>@SpellCast belongs to Spell_CastStart (CCharSpell.cpp:3598), which a
    /// targeted spell reaches only after OnTarg_Skill_Magery: the trigger sees the
    /// point just picked. It ran on the double-click instead, so a pack's
    /// IF (&lt;dtargp.z&gt; &gt;= ...) Teleport height check read the previous cursor's
    /// point, before any target existed.</summary>
    [Fact]
    public void SpellCastRunsAfterTheTargetAndSeesTheChosenPoint()
    {
        using var f = new Fixture(); f.Player.PrivLevel = PrivLevel.GM;
        string? seenTargp = null;
        int calls = 0;
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellCast", (_, _) =>
        {
            calls++;
            f.Player.TryGetTag("TARGP", out seenTargp);
            return TriggerResult.Default;
        });
        f.Client.HandleCastSpell(SpellType.Heal, 0);
        Assert.Equal(0, calls);
        Assert.False(f.Player.IsCasting);

        f.Client.HandleTargetResponse(0, f.Client.ActiveTargetCursorId,
            f.Player.Uid.Value, 123, 456, 7, 0);

        Assert.Equal(1, calls);
        Assert.NotNull(seenTargp);
        Assert.StartsWith("123,456,7", seenTargp);
        Assert.True(f.Player.IsCasting);
    }

    [Fact]
    public void TargetReplyDoesNotFireCastTriggerAgain()
    {
        using var f = new Fixture(); f.Player.PrivLevel = PrivLevel.GM;
        int calls = 0;
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellCast", (_, args) =>
        { calls++; args.N2 = 17; return TriggerResult.Default; });
        f.Client.HandleCastSpell(SpellType.Heal, 0);
        f.Client.HandleTargetResponse(0, f.Client.ActiveTargetCursorId,
            f.Player.Uid.Value, 100, 100, 0, 0);
        Assert.Equal(1, calls);
        Assert.True(f.Player.IsCasting);
        Assert.Equal(17, f.Player.CastDifficulty);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99999)]
    public void CancelOrInvalidRewriteDoesNotStart(int value)
    {
        using var f = new Fixture();
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellCast", (_, args) =>
        { args.N1 = value; return value == 1 ? TriggerResult.True : TriggerResult.Default; });
        Assert.Equal(-1, f.Start());
        Assert.False(f.Player.IsCasting);
    }

    [Fact]
    public void ScriptDifficultyReachesCompletionAndIsCleared()
    {
        using var f = new Fixture(); f.Book();
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellCast", (_, args) =>
        { args.N2 = -1; return TriggerResult.Default; });
        Assert.True(f.Start() > 0);
        Assert.False(f.Engine.CastDone(f.Player));
        Assert.Null(f.Player.CastDifficulty);
    }

    [Fact]
    public void SpellSectionStartSharesArgumentsAndRunsOnce()
    {
        using var f = new Fixture(); f.Player.PrivLevel = PrivLevel.GM;
        string path = Path.Combine(Path.GetTempPath(), $"cast-start-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, "[SPELL 4]\nON=@Start\nTAG.RUNS=<eval <TAG0.RUNS>+1>\nARGN1=16\nARGN2=37\nARGN3=0\n");
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(path);
            f.Engine.TriggerDispatcher = stack.Dispatcher;
            Assert.Equal(1, f.Start());
            Assert.True(f.Player.TryGetCastingSpell(out var spell));
            Assert.Equal(SpellType.Strength, spell);
            Assert.Equal(37, f.Player.CastDifficulty);
            Assert.True(f.Player.TryGetTag("RUNS", out var runs));
            Assert.Equal("1", runs);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SpellSelectRewriteChoosesTheNewDefinition()
    {
        using var f = new Fixture(); f.Player.PrivLevel = PrivLevel.GM;
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellSelect", (_, args) =>
        { args.N1 = (int)SpellType.Strength; return TriggerResult.Default; });
        f.Client.HandleCastSpell(SpellType.Heal, 0);
        Assert.True(f.Player.TryGetCastingSpell(out var spell));
        Assert.Equal(SpellType.Strength, spell);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrecastRunsSkillSuccessBeforeCursorAndOnlyOnce(bool cancel)
    {
        using var f = new Fixture(); f.Player.PrivLevel = PrivLevel.GM;
        Character.MagicFlags = (int)MagicConfigFlags.Precast;
        int success = 0, abort = 0;
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SkillSuccess", (_, _) =>
        { success++; return TriggerResult.Default; });
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SkillAbort", (_, _) =>
        { abort++; return TriggerResult.Default; });
        f.Client.HandleCastSpell(SpellType.Heal, 0);
        Assert.Equal(0, success);
        f.Player.SetCastTimerEnd(Environment.TickCount64 - 1);
        f.Client.TickSpellCast();
        Assert.Equal(1, success);
        Assert.NotEqual(0u, f.Client.ActiveTargetCursorId);
        f.Client.HandleTargetResponse(0, f.Client.ActiveTargetCursorId,
            cancel ? 0u : f.Player.Uid.Value,
            cancel ? (short)-1 : (short)100, cancel ? (short)-1 : (short)100, 0, 0);
        Assert.Equal(1, success);
        Assert.Equal(cancel ? 1 : 0, abort);
        Assert.False(f.Player.IsCasting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreStartCanVetoBeforeSpellCast(bool skillSection)
    {
        using var f = new Fixture(); f.Player.PrivLevel = PrivLevel.GM;
        int casts = 0;
        string path = Path.Combine(Path.GetTempPath(), $"prestart-{Guid.NewGuid():N}.scp");
        try
        {
            if (skillSection)
            {
                File.WriteAllText(path, "[SKILL 25]\nON=@PreStart\nTAG.PRE=<ACTARG1>\nRETURN 1\n");
                var stack = ScriptTestBootstrap.CreateRuntimeStack();
                stack.Resources.LoadResourceFile(path); f.Engine.TriggerDispatcher = stack.Dispatcher;
            }
            else f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SkillPreStart", (_, _) => TriggerResult.True);
            f.Engine.TriggerDispatcher!.RegisterCharEvent("EVENTSPLAYER", "SpellCast", (_, _) =>
            { casts++; return TriggerResult.Default; });
            Assert.Equal(-1, f.Start());
            Assert.Equal(0, casts); Assert.False(f.Player.IsCasting);
            if (skillSection) { Assert.True(f.Player.TryGetTag("PRE", out var v)); Assert.Equal("4", v); }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("veto")]
    [InlineData("mana")]
    [InlineData("reagent")]
    [InlineData("success")]
    public void GainFollowsSuccessfulCompletionAndFailuresAbortOnce(string outcome)
    {
        using var f = new Fixture(); f.Book();
        Character.ReagentsRequiredEnabled = true;
        f.Player.SetSkill(SkillType.Magery, 1000);
        var events = new List<string>();
        SphereNet.Game.Skills.SkillEngine.OnSkillGainCheck =
            (Character ch, SkillType skill, ref int chance, ref int max) =>
            { events.Add("gain"); return true; };
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SkillSuccess", (_, _) =>
        {
            events.Add("success");
            if (outcome == "mana") f.Player.Mana = 0;
            if (outcome == "reagent") f.Heal.Reagents.Add(SphereNet.Game.Objects.Items.ResourceMatch.ForItemDef(0x7FFE), 1);
            return outcome == "veto" ? TriggerResult.True : TriggerResult.Default;
        });
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SkillAbort", (_, _) =>
        { events.Add("abort"); return TriggerResult.Default; });
        Assert.True(f.Start() > 0);
        f.Player.CastDifficulty = 0;
        Assert.Equal(outcome == "success", f.Engine.CastDone(f.Player));
        Assert.Equal(outcome == "success" ? new[] { "success", "gain" } : new[] { "success", "abort" }, events);
    }

    [Fact]
    public void PrecastTargetTimeoutClearsCastAndDispatchesCancellation()
    {
        using var f = new Fixture(); f.Player.PrivLevel = PrivLevel.GM;
        Character.MagicFlags = (int)MagicConfigFlags.Precast;
        ClientTargetingHandler.SpellTimeoutSeconds = 5;
        int cancels = 0;
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellTargetCancel", (_, _) =>
        { cancels++; return TriggerResult.Default; });
        f.Client.HandleCastSpell(SpellType.Heal, 0);
        f.Player.SetCastTimerEnd(Environment.TickCount64 - 1);
        f.Client.TickSpellCast();
        Assert.True(f.Client.Targets.TimeoutAtMs > Environment.TickCount64);
        f.Client.Targets.TimeoutAtMs = Environment.TickCount64 - 1;
        f.Client.Targeting.TickTargetTimeout();
        Assert.Equal(1, cancels);
        Assert.False(f.Player.IsCasting);
        Assert.False(f.Client.HasPendingTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrecastFizzleGrantsFailureCreditUnlessFailHookCancels(bool cancel)
    {
        using var f = new Fixture(); f.Book();
        int gains = 0;
        SphereNet.Game.Skills.SkillEngine.OnSkillGainCheck =
            (Character ch, SkillType skill, ref int chance, ref int max) =>
            { gains++; return true; };
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SkillFail", (_, _) =>
            cancel ? TriggerResult.True : TriggerResult.Default);
        Assert.True(f.Start() > 0); f.Player.CastDifficulty = 100000;
        Assert.False(f.Engine.CompletePrecastSkill(f.Player));
        Assert.Equal(cancel ? 0 : 1, gains);
        Assert.False(f.Player.IsCasting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateAbortHookCanVetoResourceLossBeforeStateCleanup(bool veto)
    {
        using var f = new Fixture(); f.Book();
        f.Player.SetSkill(SkillType.Magery, 1000);
        Character.ManaLossAbort = true; Character.ManaLossPercent = 100;
        Character.ReagentsRequiredEnabled = true;
        bool castingAtAbort = false; int manaAtAbort = -1;
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SkillSuccess", (_, _) =>
        { f.Heal.Reagents.Add(SphereNet.Game.Objects.Items.ResourceMatch.ForItemDef(0x7FFE), 1); return TriggerResult.Default; });
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SkillAbort", (_, _) =>
        {
            castingAtAbort = f.Player.IsCasting; manaAtAbort = f.Player.Mana;
            return veto ? TriggerResult.True : TriggerResult.Default;
        });
        Assert.True(f.Start() > 0); f.Player.CastDifficulty = 0;
        Assert.False(f.Engine.CastDone(f.Player));
        Assert.True(castingAtAbort); Assert.Equal(100, manaAtAbort);
        Assert.Equal(veto ? 100 : 90, f.Player.Mana);
        Assert.False(f.Player.IsCasting);
    }
}
