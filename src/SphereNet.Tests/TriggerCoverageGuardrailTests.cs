using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SphereNet.Core.Enums;
using Xunit;

namespace SphereNet.Tests;

// Guardrail locking the trigger-coverage matrix so the "defined but not fired"
// backlog cannot silently drift. Every CharTrigger / ItemTrigger value falls into
// exactly one category, derived from the engine source itself:
//
//   * LiterallyFired  — referenced as a `CharTrigger.X` / `ItemTrigger.X` literal
//                       somewhere in the engine (a real or computed fire site).
//   * CrossFiredMirror— Char triggers fired indirectly by NAME via the dispatcher
//                       cross-fire path (`"char" + name`, `"item" + name`,
//                       TriggerDispatcher.cs) rather than a literal. These are the
//                       `Char*` / `item*` mirror families.
//   * ResourceFired   — item triggers fired through FireResourceTrigger by string
//                       name (ResourceGather / ResourceTest), not the item path.
//   * NotFired        — the documented backlog below.
//
// The not-fired set is recomputed from source on every run and asserted against
// the documented constant: wiring a backlog trigger (or adding a brand-new enum
// value) shifts the computed set and fails this test until the backlog is
// updated, forcing the change to be acknowledged. Source is located via
// CallerFilePath (compile-time path; build and test share a machine in CI).
public class TriggerCoverageGuardrailTests
{
    private static string SrcRoot([CallerFilePath] string thisFile = "")
        // src/SphereNet.Tests/<file>.cs -> src
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

    private static readonly Regex NameMapArm = new("=>\\s*\"", RegexOptions.Compiled);

    // All `prefix.Name` literals referenced across the engine, excluding the enum
    // definition file and the name-mapping switch arms (`CharTrigger.X => "X"`),
    // which reference every value and would mask the unwired ones.
    private static HashSet<string> ReferencedLiterals(string prefix)
    {
        var rx = new Regex(Regex.Escape(prefix) + "\\.([A-Za-z_]\\w*)", RegexOptions.Compiled);
        var found = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(SrcRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.EndsWith("TriggerTypes.cs", StringComparison.Ordinal)) continue;
            if (file.Contains("SphereNet.Tests")) continue;

            foreach (var line in File.ReadLines(file))
            {
                if (NameMapArm.IsMatch(line)) continue;
                foreach (Match m in rx.Matches(line))
                    found.Add(m.Groups[1].Value);
            }
        }
        return found;
    }

    private static List<string> Members<TEnum>() where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>().Where(n => n != "Qty").ToList();

    // Char triggers reached only via the dispatcher cross-fire-by-name path.
    private static bool IsCharCrossFiredMirror(string name) =>
        name.StartsWith("item", StringComparison.Ordinal) ||
        name.StartsWith("Char", StringComparison.Ordinal);

    // Item triggers fired through the resource path (FireResourceTrigger), not as
    // ordinary item triggers.
    private static readonly HashSet<string> ResourceFiredItemTriggers =
        new() { "ResourceGather", "ResourceTest" };

    // ---- Documented "defined but not fired" backlog (single source of truth) ----
    //
    // Each trigger is tagged with a wiring priority by shard impact:
    //   P0 — commonly scripted, breaks content when missing (combat, notoriety,
    //        NPC behaviour, skill/stat change).
    //   P1 — moderate (spell/pet/exp/party/skill-menu/jail/guards/environment).
    //   P2 — low (modern-client User* buttons, custom-house design, ship/candle/
    //        region/redeed item hooks, cosmetic tooltips).

    private static readonly HashSet<string> CharNotFiredP0 = new()
    {
        "HitIgnore",
        "NPCSeeWantItem", "NPCSeeNewPlayer",
        // Wired (now fired): MurderMark, KarmaChange, FameChange (DeathEngine +
        // Character.On*); SkillChange, StatChange (SkillEngine gain hooks);
        // CombatAdd, CombatDelete, CombatEnd (Character attacker-list hooks);
        // NPCRefuseItem (drop-on-NPC accept gate), NPCSpecialAction (breath/throw);
        // MurderDecay (Character notoriety-decay hook); NotoSend (ComputeNotoriety
        // via the IsTrigUsed gate — installed only when a script hooks @NotoSend).
        // Still deferred (need infrastructure):
        //   HitIgnore     — AttackerRecord has no "ignore" flag + no NPC ignore-scan.
        //   NPCSeeNewPlayer — no per-NPC seen-player memory to detect a NEW sighting.
        //   NPCSeeWantItem  — NPCs only scan corpses; no ground-item "want" logic.
    };

    private static readonly HashSet<string> CharNotFiredP1 = new()
    {
        "ExpChange", "ExpLevelChange",
        "SkillUseQuick", "CallGuards",
        "EnvironChange",
        // Wired (now fired): Eat (food/booze use-item gate), SkillMenu (skill
        // selection menu), SkillWait (per-tick skill-in-progress, IsTrigUsed-gated);
        // Follow (pet "follow me"/"come" command), PartyDisband (party drops to 0);
        // SpellSelect (cast request, pre-checks), SpellBook (spellbook open);
        // PersonalSpace (movement shove), EffectAdd (spell effect applied, gated);
        // PetDesert (loyalty hits zero in TickPetOwnershipTimers, RETURN 1 cancels);
        // Jail (the GM JAIL command, on the jailed character, N1 = sentence minutes).
        // Still deferred (need infrastructure / semantics):
        //   SkillUseQuick — UseQuick is atomic (check+gain); a pre-roll cancel hook
        //                   needs the check/gain split first.
        //   CallGuards    — no "guards" speech keyword / guard-summon system.
        //   ExpChange/ExpLevelChange — no runtime experience/level system (the Exp
        //                   and Level fields are persistence-only).
        //   EnvironChange — needs per-character light/weather state to fire only on
        //                   an actual change as the char moves between regions.
    };

    private static readonly HashSet<string> CharNotFiredP2 = new()
    {
        "UserBugReport", "UserExWalkLimit", "UserGlobalChatButton", "UserKRToolbar",
        "UserMailBag", "UserQuestArrowClick", "UserSpecialMove", "UserUltimaStoreButton", "UserVirtue",
        "HouseDesignCommit", "HouseDesignExit",
        "ToolTip", "Targon_Cancel", "NPCLostTeleport",
    };

    private static readonly HashSet<string> CharNotFired =
        new(CharNotFiredP0.Concat(CharNotFiredP1).Concat(CharNotFiredP2));

    // All currently-unwired item triggers are P2 (ship/candle/region/redeed hooks
    // and cosmetic tooltips — none are core gameplay gates today).
    private static readonly HashSet<string> ItemNotFiredP2 = new()
    {
        "SpellEffect",
        "RegionEnter", "RegionLeave",
        "Smelt", "Start", "Stop", "Level", "Complete",
        "AddRedCandle", "AddWhiteCandle", "DelRedCandle", "DelWhiteCandle",
        "PickupSelf", "PickupStack", "Tooltip",
        // Wired (now fired): ShipMove/ShipStop/ShipTurn (ShipEngine hooks),
        // Redeed (House.OnRedeed at deed creation), MemoryEquip (Memory_CreateObj
        // via Character.OnMemoryEquip, installed only when hooked — item IsTrigUsed gate).
        // Still deferred (need infrastructure): champion-spawn candles
        // (AddRed/WhiteCandle, DelRed/WhiteCandle — no altar system), item
        // leveling (Level/Complete), item region tracking (RegionEnter/Leave),
        // Smelt (no ore->ingot completion hook), Start/Stop (no item timer
        // start/stop event), SpellEffect (no spell-on-item path),
        // PickupSelf/PickupStack (ambiguous vs the existing PickupGround/PickupPack
        // fires), Tooltip (covered by ClientTooltip 0xD6).
    };

    private static readonly HashSet<string> ItemNotFired = new(ItemNotFiredP2);

    [Fact]
    public void CharTriggers_NotFired_MatchesDocumentedBacklog()
    {
        var referenced = ReferencedLiterals("CharTrigger");
        var computed = Members<CharTrigger>()
            .Where(n => !referenced.Contains(n) && !IsCharCrossFiredMirror(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(CharNotFired.OrderBy(n => n, StringComparer.Ordinal).ToList(), computed);
    }

    [Fact]
    public void ItemTriggers_NotFired_MatchesDocumentedBacklog()
    {
        var referenced = ReferencedLiterals("ItemTrigger");
        var computed = Members<ItemTrigger>()
            .Where(n => !referenced.Contains(n) && !ResourceFiredItemTriggers.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(ItemNotFired.OrderBy(n => n, StringComparer.Ordinal).ToList(), computed);
    }

    [Fact]
    public void CharBacklog_PriorityBuckets_AreDisjointAndCoverBacklog()
    {
        // P0/P1/P2 must partition the char backlog exactly: no overlap, no gaps.
        int sum = CharNotFiredP0.Count + CharNotFiredP1.Count + CharNotFiredP2.Count;
        Assert.Equal(CharNotFired.Count, sum); // disjoint (no value counted twice)

        var union = new HashSet<string>(CharNotFiredP0);
        union.UnionWith(CharNotFiredP1);
        union.UnionWith(CharNotFiredP2);
        Assert.Equal(CharNotFired.OrderBy(n => n, StringComparer.Ordinal),
            union.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void DocumentedBacklog_OnlyReferencesRealEnumValues()
    {
        var charNames = Members<CharTrigger>().ToHashSet();
        var itemNames = Members<ItemTrigger>().ToHashSet();

        Assert.All(CharNotFired, n => Assert.Contains(n, charNames));
        Assert.All(ItemNotFired, n => Assert.Contains(n, itemNames));
        Assert.All(ResourceFiredItemTriggers, n => Assert.Contains(n, itemNames));
    }
}
