using SphereNet.Core.Diagnostics;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The build stamp answers "is this binary the commit I think it is?". The value
/// of the answer is entirely in its honesty: an unstamped build must say it does
/// not know rather than report a mismatch, or the check becomes noise that gets
/// ignored - which is the same place we started from.
/// </summary>
public sealed class BuildInfoTests
{
    private readonly ITestOutputHelper _out;
    public BuildInfoTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void AnUnknownExpectationIsNotAMismatch()
    {
        Assert.Null(BuildInfo.Matches(null));
        Assert.Null(BuildInfo.Matches(""));
        Assert.Null(BuildInfo.Matches("   "));
    }

    [Fact]
    public void AnAmbiguouslyShortPrefixIsRefusedRatherThanGuessed()
    {
        // Six hex characters is roughly one in sixteen million, which sounds safe
        // until it answers "yes" for the wrong build once.
        Assert.Null(BuildInfo.Matches("abc"));
        Assert.Null(BuildInfo.Matches("abcdef"));
    }

    [Fact]
    public void TheShortCommitNeverComesBackEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.ShortCommit));
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.AssemblyVersion));
    }

    [Fact]
    public void AnUntoldBuildDoesNotClaimToBeClean()
    {
        // Three-valued on purpose. A binary built from a modified tree matches no
        // commit exactly, and a build that was never told must not answer "clean" -
        // that is the reading that turns the whole check into false reassurance.
        if (BuildInfo.Commit.Length == 0)
            Assert.Null(BuildInfo.Dirty);
    }

    [Fact]
    public void AStampedBuildAnswersAboutItself()
    {
        // The test host is built by the same props file, so when git was present
        // this is a real end-to-end check of the stamp. Without it there is nothing
        // to check, and the run says so rather than reporting a pass it did not earn.
        if (Gate.Missing(_out, "build stamp", BuildInfo.Commit.Length == 0))
            return;

        Assert.Equal(40, BuildInfo.Commit.Length);
        Assert.True(BuildInfo.Matches(BuildInfo.Commit));
        Assert.True(BuildInfo.Matches(BuildInfo.Commit[..12]));
        Assert.False(BuildInfo.Matches("0123456789abcdef0123456789abcdef01234567"));
    }
}
