using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The per-spell coverage matrix, made executable (port plan İŞ-39 / PLAN-601).
///
/// The status report used to carry one blanket line - "Bushido/Ninjitsu/Mysticism/
/// Spellweaving enum-only" - which the measurement contradicts in both directions:
/// Spellweaving and Mysticism are mostly castable, while Bard Masteries (not named
/// at all) are entirely dead. docs/BUYU_MATRISI_TR.md now holds the counts, and this
/// pins them so the document cannot quietly drift away from the pack.
///
/// Classification is the engine's own (SpellEngine.IsInertSchoolSpell): a spell is
/// refused only when it is outside the Magery/Necromancy id space, has no native
/// handler, carries none of the actionable flags, and the pack scripts no stage for
/// it. Refusing happens at CastStart, before any cost - the wave's acceptance
/// criterion is that an unsupported spell must not appear to succeed.
/// </summary>
public sealed class SpellCoverageGuardrailTests
{
    private readonly ITestOutputHelper _out;
    public SpellCoverageGuardrailTests(ITestOutputHelper output) => _out = output;

    private const string LivePack = @"C:\sphereNetServer\scripts";

    /// <summary>Flags SpellEngine.HasActionableFlags accepts, by their pack names.</summary>
    private static readonly string[] ActionableFlagNames =
    [
        "spellflag_dam", "spellflag_harm", "spellflag_heal", "spellflag_bless",
        "spellflag_curse", "spellflag_field", "spellflag_summon", "spellflag_area",
    ];

    private static readonly (string Name, int Lo, int Hi, int Castable, int Refused)[] Expected =
    [
        ("Magery",          1,   64, 64, 0),
        ("Necromancy",    101,  117, 17, 0),
        ("Chivalry",      201,  210,  8, 2),
        ("Bushido",       401,  406,  0, 6),
        ("Ninjitsu",      501,  508,  0, 8),
        ("Spellweaving",  601,  616, 14, 2),
        ("Mysticism",     678,  693, 11, 5),
        ("Bard Masteries",701,  706,  0, 6),
        ("Skill Masteries",707, 999,  0, 0),   // the pack defines none at all
        ("Sphere custom",1000, 1026, 23, 1),
    ];

    [Fact]
    public void TheLivePacksSpellCoverageIsWhatTheMatrixSays()
    {
        if (!Directory.Exists(LivePack))
        {
            _out.WriteLine("SKIP: live script pack not found");
            return;
        }

        var defs = ReadSpellBlocks(LivePack);
        Assert.NotEmpty(defs);

        foreach (var (name, lo, hi, castable, refused) in Expected)
        {
            var ids = defs.Keys.Where(i => i >= lo && i <= hi).OrderBy(i => i).ToList();
            var refusedIds = ids.Where(i => IsRefused(i, defs[i])).ToList();
            int castableCount = ids.Count - refusedIds.Count;

            _out.WriteLine($"{name}: defined={ids.Count} castable={castableCount} " +
                           $"refused={refusedIds.Count} [{string.Join(",", refusedIds)}]");

            Assert.Equal(castable, castableCount);
            Assert.Equal(refused, refusedIds.Count);
        }
    }

    [Fact]
    public void EverySpellTheEngineRefusesIsOneThePackStillOffers()
    {
        // The point of the matrix: these are not missing definitions, they are
        // definitions the engine has nothing to do with. A player can pick them off
        // a spellbook, so the refusal has to be visible rather than a silent no-op.
        if (!Directory.Exists(LivePack))
        {
            _out.WriteLine("SKIP: live script pack not found");
            return;
        }

        var defs = ReadSpellBlocks(LivePack);
        var refused = defs.Keys.Where(i => IsRefused(i, defs[i])).OrderBy(i => i).ToList();

        _out.WriteLine($"refused overall: {refused.Count} -> {string.Join(",", refused)}");
        Assert.Equal(30, refused.Count);
    }

    // ---- the refusal costs nothing ---------------------------------------

    [Fact]
    public void RefusingAnUnsupportedSpellTakesNoManaAndStartsNoCast()
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new SpellRegistry();
        // A Bushido def exactly as a bare pack entry leaves it: no actionable flag,
        // no scripted stage. IsInertSchoolSpell calls this refused.
        registry.Register(new SpellDef
        {
            Id = SpellType.Confidence,
            Name = "Confidence",
            Flags = SpellFlag.TargChar,
            ManaCost = 20,
            CastTimeBase = 1,
        });
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.MaxMana = 100;
        caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        string? said = null;
        var savedMsg = engine.OnSysMessage;
        try
        {
            engine.OnSysMessage = (_, text) => said = text;

            int result = engine.CastStart(caster, SpellType.Confidence, caster.Uid, caster.Position);

            Assert.True(result < 0);              // refused
            Assert.Equal(100, caster.Mana);       // and it cost nothing
            Assert.False(caster.IsCasting);       // and started no cast
            Assert.NotNull(said);                 // and said so
        }
        finally
        {
            engine.OnSysMessage = savedMsg;
        }
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>Native school/custom handlers, exactly the ids
    /// SpellEngine.HasNativeSchoolHandler / HasNativeCustomHandler name.</summary>
    private static readonly HashSet<int> NativeHandled =
    [
        (int)SpellType.CleanseByFire, (int)SpellType.CloseWounds, (int)SpellType.DispelEvil,
        (int)SpellType.DivineFury, (int)SpellType.HolyLight, (int)SpellType.NobleSacrifice,
        (int)SpellType.RemoveCurse, (int)SpellType.SacredJourney, (int)SpellType.GiftOfRenewal,
        (int)SpellType.ReaperForm, (int)SpellType.StoneForm,
        (int)SpellType.Light, (int)SpellType.Hallucination, (int)SpellType.Stone,
        (int)SpellType.Shrink, (int)SpellType.Refresh, (int)SpellType.Restore,
        (int)SpellType.Mana, (int)SpellType.Sustenance, (int)SpellType.GenderSwap,
        (int)SpellType.Trance, (int)SpellType.ParticleForm, (int)SpellType.Shield,
        (int)SpellType.Steelskin, (int)SpellType.Stoneskin, (int)SpellType.Regenerate,
        (int)SpellType.Ale, (int)SpellType.Wine, (int)SpellType.Liquor,
        (int)SpellType.Chameleon, (int)SpellType.BeastForm, (int)SpellType.MonsterForm,
        (int)SpellType.SummonUndead, (int)SpellType.AnimateDead,
        (int)SpellType.BoneArmor, (int)SpellType.FireBolt,
    ];

    private static bool IsRefused(int id, List<string> body)
    {
        if (id < 201) return false;               // Magery / Necromancy id space
        if (NativeHandled.Contains(id)) return false;

        bool actionable = false, scripted = false;
        foreach (var line in body)
        {
            if (Regex.IsMatch(line, @"^ON\s*=\s*@", RegexOptions.IgnoreCase))
                scripted = true;
            var m = Regex.Match(line, @"^FLAGS\s*=\s*(.*)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string flags = m.Groups[1].Value.ToLowerInvariant();
                if (ActionableFlagNames.Any(flags.Contains))
                    actionable = true;
            }
        }
        return !actionable && !scripted;
    }

    private static Dictionary<int, List<string>> ReadSpellBlocks(string packDir)
    {
        var blocks = new Dictionary<int, List<string>>();
        foreach (var file in Directory.EnumerateFiles(packDir, "*.scp", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            if (!text.Contains("[SPELL ", StringComparison.OrdinalIgnoreCase))
                continue;

            List<string>? current = null;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                var header = Regex.Match(line, @"^\[SPELL\s+(\d+)\]", RegexOptions.IgnoreCase);
                if (header.Success)
                {
                    int id = int.Parse(header.Groups[1].Value);
                    if (!blocks.TryGetValue(id, out current))
                        blocks[id] = current = [];
                    continue;
                }
                if (line.StartsWith('[')) current = null;
                current?.Add(line);
            }
        }
        return blocks;
    }
}
