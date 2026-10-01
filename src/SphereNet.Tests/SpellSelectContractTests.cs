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

/// <summary>The Spell_CanCast contract (Source-X CCharSpell.cpp:2325-2541) shared by the
/// cast start (fTest = true) and the completion's consumption (Spell_CastDone :3010):
/// the [SPELL] @Select / @SpellSelect arguments and read-back, the player-state and
/// disabled-definition refusals, and the GM resource exemption.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellSelectContractTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly Microsoft.Extensions.Logging.ILoggerFactory _logs = TestHarness.CreateLoggerFactory();
        private readonly string? _scriptPath;
        public readonly GameWorld World = TestHarness.CreateWorld();
        public readonly Character Player;
        public readonly SpellEngine Engine;
        public readonly TriggerDispatcher Triggers;
        public readonly SpellDef Heal = new()
        {
            Id = SpellType.Heal, ManaCost = 40, CastTimeBase = 5,
            Flags = SpellFlag.Heal | SpellFlag.TargChar,
        };

        public Fixture(string? spellScript = null)
        {
            Character.MagicFlags = 0;
            Player = World.CreateCharacter();
            Player.IsPlayer = true;
            Player.MaxMana = 100;
            Player.Mana = 100;
            Player.SetSkill(SkillType.Magery, 1000);
            World.PlaceCharacter(Player, new Point3D(100, 100));
            var pack = World.CreateItem(); pack.ItemType = ItemType.Container;
            Player.Equip(pack, Layer.Pack);
            var book = World.CreateItem(); book.ItemType = ItemType.Spellbook;
            pack.AddItem(book); book.TryLearnSpell((int)SpellType.Heal);

            var registry = new SpellRegistry(); registry.Register(Heal);
            if (spellScript != null)
            {
                _scriptPath = Path.Combine(Path.GetTempPath(), $"spell-select-{Guid.NewGuid():N}.scp");
                File.WriteAllText(_scriptPath, spellScript);
                var stack = ScriptTestBootstrap.CreateRuntimeStack();
                stack.Resources.LoadResourceFile(_scriptPath);
                Triggers = stack.Dispatcher;
            }
            else
                Triggers = new TriggerDispatcher();
            Engine = new SpellEngine(World, registry) { TriggerDispatcher = Triggers };
            Engine.OnSysMessage = (_, text) => Message = text;
        }

        public string? Message;

        public int Start() => Engine.CastStart(Player, SpellType.Heal, Player.Uid, Player.Position);

        /// <summary>Complete the cast past its skill roll, so only Spell_CanCast decides.</summary>
        public bool Done()
        {
            Player.CastSkillSucceeded = true;
            return Engine.CastDone(Player);
        }

        public Item Wand(int charges)
        {
            var wand = World.CreateItem();
            wand.ItemType = ItemType.Wand;
            wand.MoreP = new Point3D((short)SpellType.Heal, 0, 0, 0);
            wand.More2 = (uint)charges;
            Player.Backpack!.AddItem(wand);
            Player.SetTag("WAND_UID", wand.Uid.Value.ToString());
            return wand;
        }

        public void Dispose()
        {
            if (_scriptPath != null) File.Delete(_scriptPath);
            _logs.Dispose();
        }
    }

    private const string SelectSetsManaFive = "[SPELL 4]\nON=@Select\nTAG.RUNS=<eval <TAG0.RUNS>+1>\nARGN2=5\n";

    // ---- B07: the @Select / @SpellSelect contract ----

    [Fact]
    public void SelectManaCostWrittenToArgn2IsWhatTheCastPays()
    {
        // CCharSpell.cpp:2405 - uiManaUse = ARGN2 after the stages; :2537 consumes it.
        using var f = new Fixture(SelectSetsManaFive);
        Assert.True(f.Start() > 0);
        Assert.Equal(100, f.Player.Mana);           // the start phase only tests
        Assert.True(f.Done());
        Assert.Equal(95, f.Player.Mana);            // 5, not MANAUSE 40
    }

    [Fact]
    public void SelectManaCostAlsoGovernsTheStartCheck()
    {
        // The start-phase check compares against the same ARGN2 (:2530).
        using var f = new Fixture(SelectSetsManaFive);
        f.Player.Mana = 10;                         // short of 40, enough for 5
        Assert.True(f.Start() > 0);
        Assert.True(f.Done());
        Assert.Equal(5, f.Player.Mana);
    }

    [Fact]
    public void SpellSelectSeesSourcePhaseBitsAndTheSelectBill()
    {
        // :2368-2372 - Init(spell, mana, 0, pSrc), ARGN3 |= 1 (fTest) | 2 (fFailMsg).
        using var f = new Fixture(SelectSetsManaFive);
        var seen = new List<(long N1, long N2, long N3, object? O)>();
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellSelect", (_, a) =>
        { seen.Add((a.N1, a.N2, a.N3, a.O1)); return TriggerResult.Default; });

        Assert.True(f.Start() > 0);
        Assert.True(f.Done());

        Assert.Equal(2, seen.Count);                // start test + completion consume
        Assert.Equal(((long)SpellType.Heal, 5L, 3L), (seen[0].N1, seen[0].N2, seen[0].N3));
        Assert.Equal(((long)SpellType.Heal, 5L, 2L), (seen[1].N1, seen[1].N2, seen[1].N3));
        Assert.Same(f.Player, seen[0].O);           // a raw cast's source is the caster
        Assert.Same(f.Player, seen[1].O);
    }

    [Fact]
    public void SelectArgoIsTheWandForAWandCast()
    {
        using var f = new Fixture();
        var wand = f.Wand(3);
        var sources = new List<object?>();
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellSelect", (_, a) =>
        { sources.Add(a.O1); return TriggerResult.Default; });

        Assert.True(f.Start() > 0);
        Assert.True(f.Done());

        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.Same(wand, s));
        Assert.Equal(2u, wand.More2);               // a player pays the charge once
    }

    [Fact]
    public void SelectRunsInBothTheStartAndTheCompletionPhase()
    {
        using var f = new Fixture(SelectSetsManaFive);
        Assert.True(f.Start() > 0);
        Assert.True(f.Player.TryGetTag("RUNS", out var afterStart));
        Assert.Equal("1", afterStart);
        Assert.True(f.Done());
        Assert.True(f.Player.TryGetTag("RUNS", out var afterDone));
        Assert.Equal("2", afterDone);
    }

    [Fact]
    public void ClientCastRunsSelectOnceBeforeTheCursorAndOnceAtCompletion()
    {
        // Cmd_Skill_Magery tests before the cursor (CClientUse.cpp:1004); a
        // non-precast Spell_CastStart does not test again (CCharSpell.cpp:3461);
        // Spell_CastDone consumes (:3010).
        using var f = new Fixture(SelectSetsManaFive);
        using var logs = TestHarness.CreateLoggerFactory();
        var client = TestHarness.CreateClient(logs, f.World, new AccountManager(logs), 19761);
        TestHarness.AttachCharacter(client, f.Player);
        client.SetEngines(spellEngine: f.Engine, triggerDispatcher: f.Triggers);

        client.HandleCastSpell(SpellType.Heal, f.Player.Uid.Value);
        Assert.True(f.Player.IsCasting);
        Assert.True(f.Player.TryGetTag("RUNS", out var afterStart));
        Assert.Equal("1", afterStart);
        Assert.True(f.Done());
        Assert.True(f.Player.TryGetTag("RUNS", out var afterDone));
        Assert.Equal("2", afterDone);
        Assert.Equal(95, f.Player.Mana);
    }

    [Fact]
    public void SelectReturnZeroAcceptsWithoutTheRemainingChecksOrCosts()
    {
        // :2381 - TRIGRET_RET_FALSE returns true before the mana check and consumption.
        using var f = new Fixture("[SPELL 4]\nON=@Select\nRETURN 0\n");
        f.Player.Mana = 0;                          // MANAUSE 40 would refuse
        Assert.True(f.Start() > 0);
        Assert.True(f.Done());
        Assert.Equal(0, f.Player.Mana);
    }

    [Fact]
    public void SelectReturnOneAtCompletionRefusesAndPaysNothing()
    {
        // A refusal from the consume-phase Spell_CanCast fails the cast (:3010).
        using var f = new Fixture("[SPELL 4]\nON=@Select\nIF ((<ARGN3>&1)==0)\nRETURN 1\nENDIF\n");
        Assert.True(f.Start() > 0);
        Assert.False(f.Done());
        Assert.Equal(100, f.Player.Mana);
    }

    [Fact]
    public void DirectEngineCastFiresSpellSelect()
    {
        // NPC_FightCast and other non-client casts run Spell_CanCast too
        // (CCharNPCAct_Magic.cpp:301), so @SpellSelect is not a client-only hook.
        using var f = new Fixture();
        int fired = 0;
        f.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellSelect", (_, a) =>
        { fired++; Assert.Equal(3, a.N3); return TriggerResult.Default; });
        Assert.True(f.Start() > 0);
        Assert.Equal(1, fired);
    }

    // ---- B10: player state and a definition disabled mid-cast ----

    [Theory]
    [InlineData(StatFlag.Stone)]
    [InlineData(StatFlag.Sleeping)]
    public void APlayerTurnedToStoneOrAsleepCannotStartACast(StatFlag state)
    {
        // CCharSpell.cpp:2341 and :3440 - STATF_DEAD | STATF_SLEEPING | STATF_STONE.
        using var f = new Fixture();
        f.Player.SetStatFlag(state);
        Assert.Equal(-1, f.Start());
        Assert.False(f.Player.IsCasting);

        // The gate is for players below GM only (:2339).
        f.Player.PrivLevel = PrivLevel.GM;
        Assert.True(f.Start() > 0);
    }

    [Fact]
    public void ADefinitionDisabledMidCastRefusesAtCompletion()
    {
        // Spell_CastDone re-enters Spell_CanCast (:3010), whose first test is
        // SPELLFLAG_DISABLED (:2336).
        using var f = new Fixture();
        f.Player.MaxHits = 100; f.Player.Hits = 50;
        Assert.True(f.Start() > 0);
        f.Heal.Flags |= SpellFlag.Disabled;
        Assert.False(f.Done());
        Assert.Equal(100, f.Player.Mana);
        Assert.Equal(50, f.Player.Hits);
    }

    // ---- B24: GM resource exemptions ----

    [Fact]
    public void AGmCastsARawSpellWithoutMana()
    {
        // :2461 - a raw GM cast returns true before the mana check (:2530).
        using var f = new Fixture();
        f.Player.PrivLevel = PrivLevel.GM;
        f.Player.Mana = 0;
        Assert.True(f.Start() > 0);
        Assert.True(f.Done());
        Assert.Equal(0, f.Player.Mana);
    }

    [Fact]
    public void AGmWandCastKeepsItsCharges()
    {
        // :2430 - after the magic / ownership checks a GM returns before the charge.
        using var f = new Fixture();
        f.Player.PrivLevel = PrivLevel.GM;
        var wand = f.Wand(3);
        Assert.True(f.Start() > 0);
        Assert.True(f.Done());
        Assert.Equal(3u, wand.More2);
        Assert.False(f.Player.TryGetTag("WAND_UID", out _));
    }

    [Fact]
    public void AGmScrollCastKeepsTheScroll()
    {
        using var f = new Fixture();
        f.Player.PrivLevel = PrivLevel.GM;
        var scroll = f.World.CreateItem();
        scroll.ItemType = ItemType.Scroll;
        scroll.MoreP = new Point3D((short)SpellType.Heal, 0, 0, 0);
        f.Player.Backpack!.AddItem(scroll);
        f.Player.SetTag("SCROLL_UID", scroll.Uid.Value.ToString());
        Assert.True(f.Start() > 0);
        Assert.True(f.Done());
        Assert.False(scroll.IsDeleted);
    }

    [Fact]
    public void APlayerWandCastStillNeedsAndSpendsACharge()
    {
        using var f = new Fixture();
        var empty = f.Wand(0);
        Assert.Equal(-1, f.Start());                // DEFMSG_SPELL_WAND_NOCHARGE (:2436)
        var wand = f.Wand(3);
        f.Player.Mana = 0;                          // a wand costs no mana (Calc_SpellManaCost)
        Assert.True(f.Start() > 0);
        Assert.True(f.Done());
        Assert.Equal(2u, wand.More2);
        Assert.Equal(0u, empty.More2);
    }
}
