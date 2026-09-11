using System;
using SphereNet.Core.Enums;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Messages;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// How a finished piece is announced, and who gets their name on it
/// (port plan İŞ-26 / PLAN-404).
///
/// The reference names the quality band as it rolls it and tells the crafter
/// (DEFMSG_MAKESUCCESS_1..6, CCharSkill.cpp:758-788) - every band except the average
/// one, which stays quiet (:771). All six messages were in this engine's message table
/// and nothing ever sent one, so a shoddy dagger and a superior one read the same.
///
/// The maker's mark is gated on OF_NOITEMNAMING as well as on skill and quality
/// (:799). That bit was in the OptionFlags enum with nothing reading it - a shard
/// could switch the marks off and keep getting them.
/// </summary>
public sealed class CraftQualityMessageParityTests : IDisposable
{
    private readonly OptionFlags _savedFlags = GameClient.ServerOptionFlags;

    public void Dispose() => GameClient.ServerOptionFlags = _savedFlags;

    [Theory]
    // The band boundaries are the reference's own (CCharSkill.cpp:733-746).
    [InlineData(1, Msg.Makesuccess1)]
    [InlineData(25, Msg.Makesuccess1)]
    [InlineData(26, Msg.Makesuccess2)]
    [InlineData(50, Msg.Makesuccess2)]
    [InlineData(51, Msg.Makesuccess3)]
    [InlineData(75, Msg.Makesuccess3)]
    [InlineData(126, Msg.Makesuccess4)]
    [InlineData(150, Msg.Makesuccess4)]
    [InlineData(151, Msg.Makesuccess5)]
    [InlineData(175, Msg.Makesuccess5)]
    [InlineData(176, Msg.Makesuccess6)]
    [InlineData(200, Msg.Makesuccess6)]
    public void EachBandNamesItself(int quality, string expected)
    {
        Assert.Equal(expected, CraftingEngine.QualityMessageKey(quality));
    }

    [Theory]
    [InlineData(76)]
    [InlineData(100)]
    [InlineData(125)]
    public void TheAverageBandStaysQuiet(int quality)
    {
        Assert.Null(CraftingEngine.QualityMessageKey(quality));
    }

    // ---- the maker's mark ---------------------------------------------------

    [Fact]
    public void OnlyAGrandmastersBestPieceCarriesTheirName()
    {
        GameClient.ServerOptionFlags = OptionFlags.None;

        Assert.True(CraftingEngine.EarnsMakersMark(1000, 176));
        Assert.False(CraftingEngine.EarnsMakersMark(999, 176));   // not yet a grandmaster
        Assert.False(CraftingEngine.EarnsMakersMark(1000, 175));  // not good enough
    }

    [Fact]
    public void NoItemNamingSwitchesTheMarksOff()
    {
        GameClient.ServerOptionFlags = OptionFlags.NoItemNaming;

        Assert.False(CraftingEngine.EarnsMakersMark(1000, 200));
    }

    [Fact]
    public void EveryBandMessageResolvesToRealText()
    {
        // A key nothing can resolve would send an empty line to the player.
        foreach (int quality in new[] { 10, 40, 60, 130, 160, 190 })
        {
            string? key = CraftingEngine.QualityMessageKey(quality);
            Assert.NotNull(key);
            Assert.False(string.IsNullOrWhiteSpace(ServerMessages.Get(key!)),
                $"quality {quality} resolved key '{key}' to nothing");
        }
    }
}
