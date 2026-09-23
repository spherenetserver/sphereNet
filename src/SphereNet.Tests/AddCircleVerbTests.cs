using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// ADDCIRCLE fills a spellbook a circle at a time (port plan İŞ-15).
///
/// Upstream (CItem.cpp:3213) writes the eight spells of the circle it is given, and
/// every circle below it when the second argument is not zero; anything that is not a
/// spellbook is refused. The magery script in the pack here calls it nine times and got
/// nothing, because the verb did not exist.
/// </summary>
public sealed class AddCircleVerbTests
{
    private sealed class Console : ITextConsole
    {
        public void SysMessage(string text) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Player;
        public string GetName() => "console";
        public IScriptObj? GetSourceChar() => null;
    }

    private static Item Book()
    {
        var world = TestHarness.CreateWorld();
        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        return book;
    }

    private static bool Has(Item book, int spell)
    {
        Assert.True(book.TryGetProperty("MORE1", out string? m1));
        Assert.True(book.TryGetProperty("MORE2", out string? m2));
        uint more1 = uint.Parse(m1!, System.Globalization.NumberStyles.HexNumber);
        uint more2 = uint.Parse(m2!, System.Globalization.NumberStyles.HexNumber);
        int bit = spell - 1;                             // spell n is bit n-1 (CItem.cpp:4485)
        return bit < 32 ? (more1 & (1u << bit)) != 0 : (more2 & (1u << (bit - 32))) != 0;
    }

    /// <summary>Field report: i_full_spellbook (@Create: FOR c 1 8 / ADDCIRCLE c)
    /// came without its first spell - spell n was written at bit n, so Clumsy's bit 0
    /// never got set and the 64th spell was out of range.</summary>
    [Fact]
    public void EveryCircleGivesAFullBookFromClumsyToWaterElemental()
    {
        var book = Book();
        for (int circle = 1; circle <= 8; circle++)
            Assert.True(book.TryExecuteCommand("ADDCIRCLE", circle.ToString(), new Console()));

        Assert.Equal(uint.MaxValue, book.More1);
        Assert.Equal(uint.MaxValue, book.More2);
        Assert.True(book.ContainsSpell((int)SpellType.Clumsy));
        Assert.True(book.ContainsSpell(64));
    }

    [Fact]
    public void RemoveSpellUsesTheSameNumbering()
    {
        var book = Book();
        Assert.True(book.TryExecuteCommand("ADDCIRCLE", "1", new Console()));
        Assert.True(book.TryExecuteCommand("REMOVESPELL", "1", new Console()));
        Assert.False(Has(book, 1));
        Assert.True(Has(book, 2));
    }

    [Fact]
    public void OneCircleIsEightSpells()
    {
        var book = Book();

        Assert.True(book.TryExecuteCommand("ADDCIRCLE", "2", new Console()));

        for (int spell = 9; spell <= 16; spell++)         // circle 2 is spells 9..16
            Assert.True(Has(book, spell), $"spell {spell} should be in the book");
        Assert.False(Has(book, 8));                       // and nothing from circle 1
        Assert.False(Has(book, 17));
    }

    [Fact]
    public void TheSecondArgumentAddsEveryCircleBelow()
    {
        var book = Book();

        Assert.True(book.TryExecuteCommand("ADDCIRCLE", "3,1", new Console()));

        for (int spell = 1; spell <= 24; spell++)         // circles 1, 2 and 3
            Assert.True(Has(book, spell), $"spell {spell} should be in the book");
        Assert.False(Has(book, 25));
    }

    [Fact]
    public void AZeroSecondArgumentIsJustTheOneCircle()
    {
        var book = Book();

        Assert.True(book.TryExecuteCommand("ADDCIRCLE", "3,0", new Console()));

        Assert.False(Has(book, 1));
        for (int spell = 17; spell <= 24; spell++)
            Assert.True(Has(book, spell));
    }

    [Fact]
    public void AnythingThatIsNotASpellbookRefuses()
    {
        var world = TestHarness.CreateWorld();
        var rock = world.CreateItem();
        rock.ItemType = ItemType.Normal;

        Assert.False(rock.TryExecuteCommand("ADDCIRCLE", "1", new Console()));
    }

    [Fact]
    public void NoArgumentIsRefused()
    {
        Assert.False(Book().TryExecuteCommand("ADDCIRCLE", "", new Console()));
    }
}
