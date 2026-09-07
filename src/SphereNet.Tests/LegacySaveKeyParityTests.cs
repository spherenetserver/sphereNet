using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Keys a classic save writes that the engine has to understand (port plan İŞ-2 /
/// PLAN-106). The 56T shard dump parks whatever the loader cannot map in a SAVE.*
/// tag; every kind of key parked there is either an engine gap or a deliberate
/// exclusion, and this is where the ones classified as gaps are held to their
/// contract.
///
/// A pack renames a skill slot with the KEY of its [SKILL n] block, and Source-X looks
/// skills up by that key (CSkillDef "KEY="). 56T calls skill 54 Sailormanship, so its
/// characters carry "Sailormanship=70.0" - a name the built-in table does not have.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class LegacySaveKeyParityTests : IDisposable
{
    private readonly string _scriptPath;
    private readonly ResourceHolder _resources;

    // Skill 54 is Spellweaving to the engine; this pack calls it Sailormanship, the
    // way the 56T pack does.
    private const string Script = """
        [SKILL 54]
        DEFNAME=Skill_Sailormanship
        KEY=Sailormanship
        TITLE=Captain

        [SKILL 1]
        DEFNAME=Skill_Anatomy
        KEY=Anatomy

        [ITEMDEF 014ec]
        DEFNAME=i_map_test
        NAME=Treasure map
        TYPE=t_map

        [ITEMDEF 0fbd]
        DEFNAME=i_book_test
        NAME=Book
        TYPE=t_book
        """;

    public LegacySaveKeyParityTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"sphnet_skl_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, Script);
        _resources = new ResourceHolder(
            LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
    }

    public void Dispose() => File.Delete(_scriptPath);

    private GameWorld NewWorld()
    {
        _resources.LoadResourceFile(_scriptPath);
        new DefinitionLoader(_resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void ASkillWrittenUnderThePacksOwnNameIsRead()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();

        // What a classic save line looks like for this shard.
        Assert.True(ch.TrySetProperty("Sailormanship", "70.0"));

        Assert.Equal(700, ch.GetSkill(SkillType.Spellweaving));
    }

    [Fact]
    public void ThatSameNameReadsBackOut()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        ch.SetSkill(SkillType.Spellweaving, 700);

        Assert.True(ch.TryGetProperty("Sailormanship", out string value));
        Assert.Equal("700", value);
    }

    [Fact]
    public void TheEnginesOwnNameStillWorks()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();

        Assert.True(ch.TrySetProperty("Spellweaving", "42.0"));
        Assert.Equal(420, ch.GetSkill(SkillType.Spellweaving));
        Assert.True(ch.TryGetProperty("Spellweaving", out string value));
        Assert.Equal("420", value);
    }

    [Fact]
    public void APackNameThatMatchesNoSkillIsStillNotASkill()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();

        // Unknown key: it must not silently become skill 0.
        Assert.False(ch.TryGetProperty("Sailormanshi", out _));
    }

    [Fact]
    public void ASkillValueUnderThePackNameKeepsItsTenthsScaling()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();

        // The classic file writes tenths with a decimal point; a pack-named skill has
        // to go through the same normalisation the built-in names do.
        Assert.True(ch.TrySetProperty("Sailormanship", "12.5"));
        Assert.Equal(125, ch.GetSkill(SkillType.Spellweaving));
    }

    // ================================================================
    // A classic record carries no TYPE line: the type belongs to the ITEMDEF, and
    // upstream fixes the object's class from it at creation (CreateBase,
    // CItem.cpp:314) before reading a single key. Keys that only a map or a book
    // understands have to be read through that definition.

    [Fact]
    public void AMapTypedByItsDefinitionTakesItsPins()
    {
        var world = NewWorld();
        var map = world.CreateItem();
        map.BaseId = 0x14EC;                    // the itemdef says t_map; the record does not

        Assert.True(map.TrySetProperty("PIN", "150,150"));
        Assert.True(map.TrySetProperty("PIN", "160,170"));

        Assert.True(map.TryGetProperty("PINS", out string count));
        Assert.Equal("2", count);
        Assert.True(map.TryGetProperty("PIN.1", out string first));
        Assert.Equal("150,150", first);
        Assert.True(map.TryGetProperty("PIN.2", out string second));
        Assert.Equal("160,170", second);
    }

    [Fact]
    public void ABookTypedByItsDefinitionTakesItsPages()
    {
        var world = NewWorld();
        var book = world.CreateItem();
        book.BaseId = 0x0FBD;                   // t_book from the definition

        Assert.True(book.TrySetProperty("BODY.0", "first page"));
        Assert.True(book.TrySetProperty("BODY.1", "second page"));
        Assert.True(book.TrySetProperty("TITLE", "A title"));

        Assert.True(book.TryGetProperty("PAGES", out string pages));
        Assert.Equal("2", pages);
        Assert.Equal("A title", book.Name);
    }

    [Fact]
    public void AnInstanceTypeStillWinsOverItsDefinition()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        item.BaseId = 0x14EC;
        item.ItemType = ItemType.Normal;        // not a map any more, whatever the def says
        item.ItemType = ItemType.Container;

        Assert.False(item.TrySetProperty("PIN", "150,150"));
    }
}
