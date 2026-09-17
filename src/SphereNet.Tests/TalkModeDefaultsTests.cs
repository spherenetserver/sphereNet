using SphereNet.Game.Messages;

namespace SphereNet.Tests;

/// <summary>
/// The default hue and font a pack sets per talkmode.
///
/// Upstream picks the pair by talkmode and reads it out of the defname table on every
/// line it sends - SMSG / EMOTE / SAY / CMSG / IMSG, each with _DEF_COLOR and
/// _DEF_FONT (addBarkParse, CClientMsg.cpp:730-790). This engine loaded the SMSG pair
/// into ServerMessages.DefaultColor and then read it NOWHERE: every send site carried
/// the hue as a literal. The live pack sets SMSG_DEF_COLOR to 946 and its system
/// messages came out in the engine's own 0x35 regardless; the reference distribution
/// sets all fifteen and none of them did anything.
/// </summary>
public sealed class TalkModeDefaultsTests
{
    [Fact]
    public void TheEngineDefaultsAreTheOnesTheSendSitesUsedToCarry()
    {
        ServerMessages.ResetTalkDefaults();
        Assert.Equal(0x0035, ServerMessages.HueOf(ServerMessages.TalkDefault.System));
        Assert.Equal(0x0022, ServerMessages.HueOf(ServerMessages.TalkDefault.Emote));
        Assert.Equal(0x03B2, ServerMessages.HueOf(ServerMessages.TalkDefault.Say));
        Assert.Equal(3, ServerMessages.FontOf(ServerMessages.TalkDefault.System));
    }

    [Fact]
    public void APackValueWinsPerTalkmode()
    {
        ServerMessages.ResetTalkDefaults();
        ServerMessages.SetTalkDefault(ServerMessages.TalkDefault.System, 946, 3);

        Assert.Equal(946, ServerMessages.HueOf(ServerMessages.TalkDefault.System));
        // and only that talkmode
        Assert.Equal(0x0022, ServerMessages.HueOf(ServerMessages.TalkDefault.Emote));

        // The legacy pair stays in step, since it is the system talkmode under
        // another name.
        Assert.Equal(946, ServerMessages.DefaultColor);
    }

    /// <summary>A zero means the pack wrote no defname, and the engine default has to
    /// survive it - upstream's GetKeyNum answers 0 for an absent key the same way, and
    /// taking that literally would paint every message black.</summary>
    [Fact]
    public void AnAbsentValueLeavesTheDefaultStanding()
    {
        ServerMessages.ResetTalkDefaults();
        ServerMessages.SetTalkDefault(ServerMessages.TalkDefault.Emote, 0, 0);

        Assert.Equal(0x0022, ServerMessages.HueOf(ServerMessages.TalkDefault.Emote));
        Assert.Equal(3, ServerMessages.FontOf(ServerMessages.TalkDefault.Emote));
    }

    [Fact]
    public void ResetPutsEveryTalkmodeBack()
    {
        ServerMessages.SetTalkDefault(ServerMessages.TalkDefault.Say, 100, 7);
        ServerMessages.ResetTalkDefaults();

        Assert.Equal(0x03B2, ServerMessages.HueOf(ServerMessages.TalkDefault.Say));
        Assert.Equal(3, ServerMessages.FontOf(ServerMessages.TalkDefault.Say));
    }
}
