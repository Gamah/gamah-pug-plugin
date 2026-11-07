using Gpp;
using Xunit;

namespace Gpp.Tests;

// The two parts of .kick and of the audit that have no engine in them: how the argument reads, and what
// counts as the same command arriving twice.
public class KickTests
{
    [Fact]
    public void ATargetOnItsOwnIsNoReason()
    {
        Assert.Equal(("4", ""), Kicks.Split("4"));
        Assert.Equal(("asta", ""), Kicks.Split("  asta  "));
    }

    // Everything after the first token is the reason, kept as typed - including its spaces, because that
    // is what gets read back out of the log.
    [Fact]
    public void TheRestIsTheReason()
    {
        Assert.Equal(("4", "shooting the team"), Kicks.Split("4 shooting the team"));
        Assert.Equal(("asta", "AFK all half"), Kicks.Split("asta   AFK all half"));
    }

    [Fact]
    public void NothingIsNoTarget() => Assert.Equal(("", ""), Kicks.Split("   "));

    [Fact]
    public void TheThreeRoutesToOneCommandNormaliseTheSame()
    {
        Assert.Equal("kick 4", Audits.Normalise(".kick 4"));
        Assert.Equal("kick 4", Audits.Normalise("!kick 4"));
        Assert.Equal("kick 4", Audits.Normalise("css_kick  4 "));
        Assert.Equal("kick 4", Audits.Normalise("CSS_Kick 4"));
    }

    [Fact]
    public void DifferentCommandsDoNotNormaliseTogether()
    {
        Assert.NotEqual(Audits.Normalise(".kick 4"), Audits.Normalise(".kick 5"));
        Assert.NotEqual(Audits.Normalise(".kick 4"), Audits.Normalise(".unkick 4"));
    }
}
