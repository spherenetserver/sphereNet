using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Reading a creature's brain back.
///
/// Upstream answers `<NPC>` with the brain as a number (CCharNPC::r_WriteVal,
/// CCharNPC.cpp:168) using the NPCBRAIN_TYPE order from CChar.h:32. Only the WRITE
/// existed here: `NPC=brain_monster` in a chardef was applied, but nothing could read
/// it back. A script testing `IF (<NPC> == brain_monster)` therefore got nothing, the
/// PROPLIST surface advertised a key it could not answer, and .info on a creature
/// showed no brain at all - which is the one field worth seeing when a monster will
/// not attack.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcBrainPropertyTests
{
    private readonly ITestOutputHelper _out;
    public NpcBrainPropertyTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(NpcBrainType.None, "0")]
    [InlineData(NpcBrainType.Animal, "1")]
    [InlineData(NpcBrainType.Human, "2")]
    [InlineData(NpcBrainType.Guard, "4")]
    [InlineData(NpcBrainType.Vendor, "6")]
    [InlineData(NpcBrainType.Monster, "8")]
    [InlineData(NpcBrainType.Dragon, "10")]
    public void NpcReadsBackTheBrainNumber(NpcBrainType brain, string expected)
    {
        var ch = new Character { NpcBrain = brain };

        Assert.True(ch.TryGetProperty("NPC", out string value));
        _out.WriteLine($"{brain} -> <NPC> = {value}");
        Assert.Equal(expected, value);
    }

    [Fact]
    public void WhatWasWrittenIsWhatComesBack()
    {
        // The round trip a chardef makes: `NPC=brain_monster` in @Create, read later.
        var ch = new Character();
        Assert.True(ch.TrySetProperty("NPC", "brain_monster"));

        Assert.True(ch.TryGetProperty("NPC", out string value));
        Assert.Equal(((int)NpcBrainType.Monster).ToString(), value);
    }

    [Fact]
    public void TheOlderSpellingStillAnswers()
    {
        var ch = new Character { NpcBrain = NpcBrainType.Berserk };
        Assert.True(ch.TryGetProperty("NPCBRAIN", out string viaLongName));
        Assert.True(ch.TryGetProperty("NPC", out string viaUpstreamName));
        Assert.Equal(viaLongName, viaUpstreamName);
    }

    [Fact]
    public void ThePropListKeyIsAnswerable()
    {
        // PROPLIST names NPC; a key it lists has to be a key it can read, or the
        // diagnostic surface lies about what it knows.
        var ch = new Character { NpcBrain = NpcBrainType.Monster };
        Assert.True(ch.TryGetProperty("NPC", out _));
    }
}
