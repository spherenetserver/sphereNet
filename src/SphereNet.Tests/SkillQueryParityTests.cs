using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// SKILLCHECK, SKILLADJUSTED and SKILLTEST (port plan İŞ-68 / PLAN-304).
///
/// PLAN-304 asks for the SKILL* queries with their Source-X argument AND SIDE-EFFECT
/// contract, and the emphasis is the point: these three sit next to each other in the
/// same switch and behave quite differently.
///
/// <list type="bullet">
/// <item>SKILLCHECK ROLLS (CChar.cpp:2652). Its own comment calls it "an odd way to
/// get skills checking into the triggers". It awards no experience and fires no
/// trigger, so unlike SKILLUSEQUICK a script may ask it freely.</item>
/// <item>SKILLADJUSTED only reads - but it reads the number the engine actually rolls
/// against, and it comes back as TEXT: "%hu.%hu" (CChar.cpp:2665).</item>
/// <item>SKILLTEST asks a whole requirement list at once, skills and items together
/// (CChar.cpp:2822), through the same SkillResourceTest a craft runs.</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SkillQueryParityTests
{
    private readonly ITestOutputHelper _out;
    public SkillQueryParityTests(ITestOutputHelper output) => _out = output;

    private const string Script = """
        [SKILL 7]
        KEY=Blacksmithing
        TITLE=Smith
        BONUS_STR=100
        BONUS_STATS=20

        [TYPEDEFS]
        t_ingot=52

        [ITEMDEF i_ingot_iron]
        ID=01BF2
        NAME=iron ingot
        TYPE=t_ingot

        [ITEMDEF i_hammer]
        ID=013E3
        NAME=smith hammer
        """;

    // ---- SKILLCHECK ------------------------------------------------------

    [Fact]
    public void AnImpossibleCheckFails()
    {
        var ch = Smith();
        ch.SetSkill(SkillType.Blacksmithing, 0);
        ch.PrivLevel = PrivLevel.Player;

        Assert.True(ch.TryGetProperty("SKILLCHECK.Blacksmithing,100", out string v));
        _out.WriteLine($"skill 0 vs difficulty 100 -> {v}");

        // Difficulty is 0-100 and upstream scales it by ten before the curve, so 100 is
        // the top of the range against a skill of zero.
        Assert.Equal("0", v);
    }

    [Fact]
    public void ANegativeDifficultyIsAnAutomaticFailure()
    {
        var ch = Smith();
        ch.SetSkill(SkillType.Blacksmithing, 1000);
        ch.PrivLevel = PrivLevel.Player;

        Assert.True(ch.TryGetProperty("SKILLCHECK.Blacksmithing,-1", out string v));

        // CCharSkill.cpp:527 - "auto failure". A grandmaster still fails a negative
        // difficulty, which is how a script says "never".
        Assert.Equal("0", v);
    }

    [Fact]
    public void AGmSucceedsAtEverythingExceptParrying()
    {
        var ch = Smith();
        ch.SetSkill(SkillType.Blacksmithing, 0);
        ch.SetSkill(SkillType.Parrying, 0);
        ch.PrivLevel = PrivLevel.GM;

        Assert.True(ch.TryGetProperty("SKILLCHECK.Blacksmithing,100", out string smith));
        _out.WriteLine($"GM smith -> {smith}");

        // The carve-out is upstream's own (CCharSkill.cpp:524): a GM who always parried
        // would take no damage in combat even without being set invulnerable, so staff
        // could not test a fight.
        Assert.Equal("1", smith);
    }

    [Fact]
    public void AnUnknownSkillLeavesTheKeyUnanswered()
    {
        var ch = Smith();

        // Not "0". Upstream returns false and the caller sees the raw text - the
        // difference between "you fail" and "there is no such skill", which is the only
        // way a typo in a script is ever noticed.
        Assert.False(ch.TryGetProperty("SKILLCHECK.NotASkill,10", out string v));
        Assert.Equal("", v);
        Assert.False(ch.TryGetProperty("SKILLCHECK.Blacksmithing", out _));
    }

    [Fact]
    public void CheckingASkillNeitherRaisesItNorSpendsAnything()
    {
        var ch = Smith();
        ch.SetSkill(SkillType.Blacksmithing, 500);
        ch.PrivLevel = PrivLevel.Player;
        int before = ch.GetSkill(SkillType.Blacksmithing);

        for (int i = 0; i < 50; i++)
            Assert.True(ch.TryGetProperty("SKILLCHECK.Blacksmithing,10", out _));

        _out.WriteLine($"skill after 50 checks: {ch.GetSkill(SkillType.Blacksmithing)}");

        // Skill_CheckSuccess is the one that does NOT give experience - its header says
        // so. This is what separates it from SKILLUSEQUICK next door, and why a script
        // can poll it in a loop without quietly training the character.
        Assert.Equal(before, ch.GetSkill(SkillType.Blacksmithing));
    }

    // ---- SKILLADJUSTED ---------------------------------------------------

    [Fact]
    public void TheAdjustedSkillCarriesTheStatBonusAndComesBackAsText()
    {
        var ch = Smith();
        ch.SetSkill(SkillType.Blacksmithing, 500);
        ch.Str = 100;

        Assert.True(ch.TryGetProperty("SKILLADJUSTED.Blacksmithing", out string adj));
        Assert.True(ch.TryGetProperty("BLACKSMITHING", out string raw));
        _out.WriteLine($"raw={raw} adjusted={adj}");

        // The definition gives 20% of its bonus from STR, so 50.0 of skill on 100 STR
        // reads higher than the bare skill does - and it is the adjusted number the
        // engine rolls against. Formatted "%hu.%hu" (CChar.cpp:2672), so 520 comes back
        // as "52.0" - text with a dot in it, unlike every other skill read.
        Assert.Contains('.', adj);
        Assert.Equal("500", raw);
        Assert.Equal("52.0", adj);
    }

    [Fact]
    public void AnUnknownSkillNameIsNotAnAdjustedZero()
    {
        var ch = Smith();
        Assert.False(ch.TryGetProperty("SKILLADJUSTED.NotASkill", out _));
    }

    // ---- SKILLTEST -------------------------------------------------------

    [Fact]
    public void ATestAsksForTheSkillsAndTheItemsTogether()
    {
        var (ch, pack) = SmithWithPack();
        ch.SetSkill(SkillType.Blacksmithing, 600);

        Assert.True(ch.TryGetProperty("SKILLTEST Blacksmithing 50.0,5 i_ingot_iron", out string missing));
        _out.WriteLine($"without ingots -> {missing}");
        Assert.Equal("0", missing);

        var ingots = NewItem(pack, 0x1BF2);
        ingots.ItemType = ItemType.Ingot;
        ingots.Amount = 5;

        Assert.True(ch.TryGetProperty("SKILLTEST Blacksmithing 50.0,5 i_ingot_iron", out string ok));
        _out.WriteLine($"with five ingots -> {ok}");

        // One question, two kinds of entry - and they are genuinely different
        // questions: the skill entry is a level, the item entry a count. A list that
        // treated them alike would read "Blacksmithing 50.0" as fifty hammers.
        Assert.Equal("1", ok);
    }

    [Fact]
    public void TheSkillHalfComparesALevelNotACount()
    {
        var (ch, _) = SmithWithPack();
        ch.SetSkill(SkillType.Blacksmithing, 400);

        Assert.True(ch.TryGetProperty("SKILLTEST Blacksmithing 50.0", out string under));
        ch.SetSkill(SkillType.Blacksmithing, 500);
        Assert.True(ch.TryGetProperty("SKILLTEST Blacksmithing 50.0", out string exact));

        _out.WriteLine($"40.0 -> {under}; 50.0 -> {exact}");

        // ">=", not ">": at exactly the stated level the requirement is met
        // (CCharStatus.cpp:33 fails only when the skill is BELOW the amount).
        Assert.Equal("0", under);
        Assert.Equal("1", exact);
    }

    [Fact]
    public void AnEmptyListIsFalseRatherThanVacuouslyTrue()
    {
        var (ch, _) = SmithWithPack();

        Assert.True(ch.TryGetProperty("SKILLTEST ", out string empty));
        _out.WriteLine($"empty list -> {empty}");

        // Upstream requires Resources.Load to have produced at least one entry before
        // the test can pass (CChar.cpp:2827). "Nothing is required" answering yes would
        // let a mis-typed list open a gate it was written to close.
        Assert.Equal("0", empty);
    }

    [Fact]
    public void AResourceTheCharacterCannotHoldIsFalseNotUnanswered()
    {
        var (ch, _) = SmithWithPack();

        Assert.True(ch.TryGetProperty("SKILLTEST 1 i_not_defined_anywhere", out string v));

        // A name that resolves to nothing is a failed match, and the query still
        // answers. SKILLTEST is a yes/no by construction (SetValTrue/SetValFalse).
        Assert.Equal("0", v);
    }

    // ---- fixture ---------------------------------------------------------

    private static Item NewItem(Item pack, ushort id)
    {
        var world = Item.ResolveWorld!.Invoke();
        var it = world.CreateItem();
        it.BaseId = id;
        pack.AddItem(it);
        return it;
    }

    private Character Smith() => SmithWithPack().Character;

    private (Character Character, Item Pack) SmithWithPack()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        string path = Path.Combine(Path.GetTempPath(), $"spherenet_skillq_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, Script);
        try
        {
            var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
            {
                ScpBaseDir = Path.GetDirectoryName(path) ?? ""
            };
            resources.LoadResourceFile(path);
            new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

            var world = new GameWorld(loggerFactory);
            world.InitMap(0, 1024, 1024);
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;

            // The possession half goes through the crafting engine's stock search, the
            // way the server wires it - so the query and a craft cannot disagree about
            // what is reachable in the pack.
            Character.OnResourcePossessionCheck =
                (c, rid, amount) => CraftingEngine.CountStock(c, rid) >= amount;

            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.BaseId = 0x0190;
            ch.Str = 50; ch.Dex = 50; ch.Int = 50;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            ch.Equip(pack, Layer.Pack);
            return (ch, pack);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
