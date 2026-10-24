using Gpp;
using Xunit;

namespace Gpp.Tests;

// The two bits of .smokes that are not engine calls: what a bare ".smokes" means, and the line the
// thrower gets back.
public class SmokesTests
{
    [Fact]
    public void ABareCommandToggles()
    {
        Assert.True(Smokes.SmokesWanted("", alreadyOn: false));
        Assert.False(Smokes.SmokesWanted("", alreadyOn: true));
    }

    [Fact]
    public void OnAndOffAreNotToggles()
    {
        Assert.True(Smokes.SmokesWanted("on", alreadyOn: true));
        Assert.False(Smokes.SmokesWanted("off", alreadyOn: false));
    }

    [Fact]
    public void AnythingElseIsAToggle() => Assert.True(Smokes.SmokesWanted("pls", alreadyOn: false));

    // Angles carry two decimals because a lineup is made of them; positions carry one.
    [Fact]
    public void TheThrowLineKeepsTheAngles()
    {
        Assert.Equal("Threw from -1134.5 2276.0 -103.9  looking -12.34 178.42",
            Smokes.SmokeThrowLine(-1134.47f, 2276.0f, -103.875f, -12.344f, 178.421f));
    }

    [Fact]
    public void ThePoppedLineIsTheLandingSpot()
    {
        Assert.Equal("Popped at 100.0 -500.5 64.0", Smokes.SmokePoppedLine(100f, -500.46f, 64f));
    }
}
