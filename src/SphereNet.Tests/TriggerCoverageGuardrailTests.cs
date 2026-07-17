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
        // Wired (now fired): HitIgnore — AttackerRecord gained the Ignored
        // flag (script ATTACKER.n.IGNORE=1); a later hit from that attacker
        // fires @HitIgnore on the victim, RETURN 1 clears the flag.
        // Wired (now fired): MurderMark, KarmaChange, FameChange (DeathEngine +
        // Character.On*); SkillChange, StatChange (SkillEngine gain hooks);
        // CombatAdd, CombatDelete, CombatEnd (Character attacker-list hooks);
        // NPCRefuseItem (drop-on-NPC accept gate), NPCSpecialAction (breath/throw);
        // MurderDecay (Character notoriety-decay hook); NotoSend (ComputeNotoriety
        // via the IsTrigUsed gate); NPCSeeNewPlayer (new per-NPC seen-player memory:
        // Character.SeeNewPlayer fires it on a first sighting; NpcAI scans nearby
        // players, gated + throttled, installed only when hooked).
        // NPCSeeWantItem now fires from the native desire/ground-loot path,
        // immediately before movement to or pickup of the wanted item.
    };

    private static readonly HashSet<string> CharNotFiredP1 = new()
    {
        // Wired (now fired): ExpChange/ExpLevelChange (Character.ChangeExperience
        // pipeline — script EXP writes and DeathEngine kill awards);
        // Eat (food/booze use-item gate), SkillMenu (skill
        // selection menu), SkillWait (new action attempted while another is active);
        // Follow (pet "follow me"/"come" command), PartyDisband (party drops to 0);
        // SpellSelect (cast request, pre-checks), SpellBook (spellbook open);
        // PersonalSpace (movement shove), EffectAdd (spell effect applied, gated);
        // PetDesert (loyalty hits zero in TickPetOwnershipTimers, RETURN 1 cancels);
        // Jail (the GM JAIL command, on the jailed character, N1 = sentence minutes);
        // CallGuards (the "guards" keyword handler in Program.NpcServices fires it
        // per reported criminal, <argo> = the hostile, RETURN 1 cancels that one);
        // EnvironChange (Character.UpdateEnvironLight fires it on a surface/dungeon
        // light-level change at region transition, N1 = new light level);
        // SkillUseQuick (SkillEngine.UseQuick fires it before the check via the
        // Character.OnSkillUseQuick gate; N1 = skill, N2 = difficulty, RETURN 1 cancels).
        // Wired (now fired): Reveal (Character.ClearHiddenState central reveal
        // path, RETURN 1 keeps concealment); SpellEffectAdd / SpellEffectRemove
        // (SpellEngine timed-effect lifecycle: apply, expiry, re-cast refresh,
        // death cleanup — save-time revert/reapply deliberately silent);
        // DeathCorpse (DeathEngine.ProcessDeath after loot transfer, argo = corpse);
        // SpellEffectTick (CharacterPoisonState.ProcessTick via the gated
        // Character.OnSpellEffectTick bridge — ARGN1 = spell id, ARGN2 =
        // strength, ARGO = SpellMemoryShim, LOCAL.EFFECT/DELAY/CHARGES
        // seeded and read back through TriggerArgs.Locals, RETURN 1 cures).
    };

    private static readonly HashSet<string> CharNotFiredP2 =
    [
        // UserVirtue — the virtue-GUMP select path (Source-X Event_VirtueSelect,
        // 0xB1 dialog reply / CTRIG_UserVirtue), distinct from UserVirtueInvoke (the
        // 0x12/0xF4 hotkey invoke, which IS fired). SphereNet has no virtue gump yet,
        // so this has no fire site. (Wave 265 removed the incorrect 0xBF 0x2C firing —
        // that subcommand is the bandage macro, not virtue.)
        "UserVirtue",
        // Wired (now fired): HouseDesignCommit / HouseDesignExit
        // (GameClient.HandleEncodedCommand — 0xD7 Commit and Close paths);
        // UserKRToolbar (0xBF 0x24);
        // UserQuestArrowClick (0xBF 0x07), UserBugReport (0xF4 crash report),
        // UserUltimaStoreButton (0xFA), UserGlobalChatButton (0xB5),
        // UserExWalkLimit (walk token bucket dry, IsTrigUsed-gated),
        // ToolTip (single click, IsTrigUsed-gated), Targon_Cancel (target
        // cursor dismissed).
        // Wired (now fired): UserSpecialMove (0xD7 sub 0x19 combat ability,
        // N1 = ability index); NPCLostTeleport (severely lost NPC teleports
        // home from the serial ApplyDecision phase, RETURN 1 cancels).
        // Wired (now fired): UserMailBag (legacy 0xBB carrier; recipient trigger,
        // sender as SRC, RETURN 1 suppresses delivery notification).
    ];

    private static readonly HashSet<string> CharNotFired =
        new(CharNotFiredP0.Concat(CharNotFiredP1).Concat(CharNotFiredP2));

    // All currently-unwired item triggers are P2 (ship/candle/region/redeed hooks
    // and cosmetic tooltips — none are core gameplay gates today).
    private static readonly HashSet<string> ItemNotFiredP2 = new()
    {
        // Wired (now fired): the champion-altar family — Level, Complete,
        // AddRed/WhiteCandle and DelRed/WhiteCandle all fire from
        // ChampionComponent (the Source-X CCChampion port).
        // Wired (now fired): Tooltip (single click, IsTrigUsed-gated,
        // ahead of @Click in HandleSingleClick); Start/Stop (the spawner
        // START/STOP verbs, via Item.OnSpawnStartStop).
        // Wired (now fired): ShipMove/ShipStop/ShipTurn (ShipEngine hooks),
        // Redeed (House.OnRedeed at deed creation), MemoryEquip (Memory_CreateObj
        // via Character.OnMemoryEquip, installed only when hooked — item IsTrigUsed gate);
        // PickupSelf (item dragged off the picker's own equipment layers) and
        // PickupStack (a partial amount split out of a larger stack), selected by
        // SelectPickupTrigger in HandleItemPickup alongside the existing
        // PickupGround/PickupPack cases.
        // Wired (now fired): RegionEnter/RegionLeave — Source-X scopes these
        // to movable multis: ShipEngine.MoveDelta fires them on the ship
        // multi at a region boundary (ARGO = region, SRC = pilot, RETURN 1
        // blocks the step), via the gated OnShipRegionChange hook.
        // The item-trigger backlog is EMPTY — every ItemTrigger member has a
        // fire site. New enum members land here until they are wired.
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
