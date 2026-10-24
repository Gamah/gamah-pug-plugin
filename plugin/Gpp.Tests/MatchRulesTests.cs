using Gpp;
using Xunit;

namespace Gpp.Tests;

public class ReadyThresholdTests
{
    [Fact]
    public void ZeroMeansEverybody() => Assert.Equal(10, MatchRules.ReadyNeeded(roster: 10, minReady: 0));

    [Fact]
    public void ThresholdCapsAtTheRoster() => Assert.Equal(4, MatchRules.ReadyNeeded(roster: 4, minReady: 10));

    [Fact]
    public void ThresholdIsUsedWhenItFits() => Assert.Equal(2, MatchRules.ReadyNeeded(roster: 10, minReady: 2));

    // A roster of nobody still needs one go-ahead, or the match starts itself.
    [Fact]
    public void EmptyRosterNeedsOne() => Assert.Equal(1, MatchRules.ReadyNeeded(roster: 0, minReady: 0));
}

public class TechPauseTests
{
    [Fact]
    public void UnlimitedWhenMaxIsZero() => Assert.True(MatchRules.CanCallTechPause(used: 99, max: 0));

    [Fact]
    public void AllowedUpToTheCap()
    {
        Assert.True(MatchRules.CanCallTechPause(used: 1, max: 2));
        Assert.False(MatchRules.CanCallTechPause(used: 2, max: 2));
    }

    [Fact]
    public void TacticalPauseAlwaysNeedsBothSides()
    {
        Assert.False(MatchRules.AnyoneCanUnpause(technical: false, secondsElapsed: 600, timeLimit: 30));
    }

    [Fact]
    public void TechnicalPauseOpensUpAfterItsLimit()
    {
        Assert.False(MatchRules.AnyoneCanUnpause(technical: true, secondsElapsed: 29, timeLimit: 30));
        Assert.True(MatchRules.AnyoneCanUnpause(technical: true, secondsElapsed: 30, timeLimit: 30));
    }

    // -1 is "the pause has not taken effect yet", which must not read as 30 seconds already served.
    [Fact]
    public void PauseThatHasNotLandedHasNotStarted()
    {
        Assert.False(MatchRules.AnyoneCanUnpause(technical: true, secondsElapsed: -1, timeLimit: 30));
        Assert.Equal(30, MatchRules.SecondsUntilAnyoneCanUnpause(technical: true, secondsElapsed: -1, timeLimit: 30));
    }

    [Fact]
    public void NoLimitNeverOpensUp()
    {
        Assert.False(MatchRules.AnyoneCanUnpause(technical: true, secondsElapsed: 9999, timeLimit: 0));
        Assert.Equal(-1, MatchRules.SecondsUntilAnyoneCanUnpause(technical: true, secondsElapsed: 9999, timeLimit: 0));
    }
}

public class SideSwapTests
{
    private static bool Swap(int roundsPlayed) =>
        MatchRules.IsSideSwapRound(roundsPlayed, maxRounds: 24, overtimeMaxRounds: 6, halftimeEnabled: true);

    [Fact]
    public void SwapsAtTheHalf()
    {
        Assert.False(Swap(11));
        Assert.True(Swap(12));
        Assert.False(Swap(13));
    }

    [Fact]
    public void NoSwapForTheRestOfRegulation()
    {
        for (int round = 13; round <= 24; round++) Assert.False(Swap(round));
    }

    // MR12 with MR3 overtime: 24 regulation rounds, then each overtime is six rounds with the swap in the
    // middle of it - rounds 27 and 33, not 30, which is where one overtime ends and the next begins.
    [Fact]
    public void SwapsAtEveryOvertimeHalf()
    {
        Assert.True(Swap(27));
        Assert.False(Swap(28));
        Assert.False(Swap(30));
        Assert.True(Swap(33));
        Assert.False(Swap(36));
        Assert.True(Swap(39));
    }

    [Fact]
    public void NothingSwapsWithHalftimeOff()
    {
        Assert.False(MatchRules.IsSideSwapRound(12, 24, 6, halftimeEnabled: false));
    }

    // A server with mp_maxrounds 0 (or an odd one) must not divide by it or swap every round.
    [Fact]
    public void NonsenseRoundCountsDoNotSwap()
    {
        Assert.False(MatchRules.IsSideSwapRound(0, 0, 6, true));
        Assert.False(MatchRules.IsSideSwapRound(5, 1, 6, true));
    }
}

public class KnifeWinnerTests
{
    [Fact]
    public void MorePlayersStandingWins()
    {
        Assert.Equal(MatchRules.CtTeam, MatchRules.KnifeWinner(ctAlive: 3, ctHealth: 50, tAlive: 1, tHealth: 300));
        Assert.Equal(MatchRules.TTeam, MatchRules.KnifeWinner(ctAlive: 1, ctHealth: 300, tAlive: 3, tHealth: 50));
    }

    [Fact]
    public void EvenNumbersGoOnHealth()
    {
        Assert.Equal(MatchRules.CtTeam, MatchRules.KnifeWinner(2, 150, 2, 149));
        Assert.Equal(MatchRules.TTeam, MatchRules.KnifeWinner(2, 149, 2, 150));
    }

    // Nobody alive on either side - a knife round that timed out with a double wipe - is a coin flip, not
    // a win for CT because CT is listed first.
    [Fact]
    public void DeadHeatIsACoinFlip()
    {
        Assert.Equal(0, MatchRules.KnifeWinner(0, 0, 0, 0));
        Assert.Equal(0, MatchRules.KnifeWinner(2, 200, 2, 200));
    }
}

public class DemoNameTests
{
    private static readonly DateTime When = new(2026, 10, 19, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public void FillsEveryToken()
    {
        string name = MatchRules.DemoFileName("{TIME}_{MATCHID}_{MAP}_{TEAM1}_vs_{TEAM2}", When, 17,
            "de_mirage", "team_Ada", "team_Baz");
        Assert.Equal("20261019-040506_17_de_mirage_team_Ada_vs_team_Baz", name);
    }

    // Player names end up in here, and they contain quotes, slashes, dots and worse; a demo path is not
    // the place to find out.
    [Fact]
    public void StripsAnythingAFilenameShouldNotCarry()
    {
        string name = MatchRules.DemoFileName("{TEAM1}", When, 1, "m", "../../etc/pa;ssw\"d", "t2");
        Assert.Equal("....etcpasswd", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(";", name);
    }

    [Fact]
    public void FallsBackWhenNothingIsLeft()
    {
        Assert.Equal("gpp-42", MatchRules.DemoFileName("{TEAM1}", When, 42, "m", "☆☆☆", "t"));
    }
}

public class TeamNameTests
{
    [Fact]
    public void KeepsLettersAndDigits() => Assert.Equal("team_Ada99", MatchRules.TeamName("Ada 99"));

    [Fact]
    public void DropsEverythingThatCouldEndAConsoleCommand()
    {
        string name = MatchRules.TeamName("ad\"a;quit");
        Assert.Equal("team_adaquit", name);
    }

    [Fact]
    public void ClipsLongNames() => Assert.Equal("team_abcdefghijkl", MatchRules.TeamName("abcdefghijklmnopqrstuvwxyz"));

    [Fact]
    public void NameWithNothingUsableStillHasAName() => Assert.Equal("team", MatchRules.TeamName("☆☆"));
}

// Round one's thirty seconds of held freeze time, telling the side that lost the knife to get into
// their Discord channel. Which side that is survives a .switch, which moves both teams across.
public class OpeningPauseTests
{
    [Fact]
    public void TheKnifeLoserMoves()
    {
        Assert.Equal(MatchRules.TTeam, MatchRules.MovingSide(MatchRules.CtTeam));
        Assert.Equal(MatchRules.CtTeam, MatchRules.MovingSide(MatchRules.TTeam));
    }

    // A knife round that was never played leaves no winner; nobody is named rather than the wrong side.
    [Fact]
    public void NoKnifeWinnerNamesNobody() => Assert.Equal(0, MatchRules.MovingSide(0));

    [Fact]
    public void SwitchingSidesMovesTheSideThatHasToMove()
    {
        int moving = MatchRules.MovingSide(MatchRules.CtTeam);      // T lost, T moves
        Assert.Equal(MatchRules.CtTeam, MatchRules.OtherSide(moving));
        Assert.Equal(moving, MatchRules.OtherSide(MatchRules.OtherSide(moving)));
    }

    [Fact]
    public void ThePanelIsThreeShoutedLines()
    {
        var ct = MatchRules.OpeningLines(MatchRules.CtTeam);
        var t = MatchRules.OpeningLines(MatchRules.TTeam);

        Assert.Equal(["COUNTER-TERRORISTS", "STFU AND MOVE", "IN DISCORD"], ct);
        Assert.Equal(["TERRORISTS", "STFU AND MOVE", "IN DISCORD"], t);
        Assert.All(ct, line => Assert.Equal(line.ToUpperInvariant(), line));
    }

    // The alert box is the thing it is drawn in: three lines, each inside the character cap.
    [Fact]
    public void ThePanelFitsTheAlertBox()
    {
        foreach (int side in new[] { MatchRules.CtTeam, MatchRules.TTeam, 0 })
        {
            var lines = MatchRules.OpeningLines(side);
            string alert = HudLayout.Alert(lines[0], lines[1], lines[2]);

            Assert.Equal(3, alert.Split('\n').Length);
            Assert.All(alert.Split('\n'),
                line => Assert.True(line.Length <= HudLayout.AlertWidth, line));
        }
    }

    // The draw vote has one round's notice, and only ever at 12-11 or its overtime equivalents.
    [Theory]
    [InlineData(12, 11, 12)]
    [InlineData(11, 12, 12)]
    [InlineData(11, 11, 0)]
    [InlineData(12, 12, 0)]
    [InlineData(10, 9, 0)]
    [InlineData(15, 14, 15)]
    [InlineData(14, 15, 15)]
    [InlineData(15, 15, 0)]
    [InlineData(18, 17, 18)]
    [InlineData(13, 12, 0)]
    [InlineData(16, 15, 0)]
    public void ADrawIsOneRoundAway(int score1, int score2, int expected)
    {
        Assert.Equal(expected, MatchRules.DrawNextRound(score1, score2, maxRounds: 24, overtimeMaxRounds: 6));
    }

    // No overtime configured: regulation can still be drawn, and nothing after it can.
    [Fact]
    public void WithoutOvertimeOnlyRegulationDraws()
    {
        Assert.Equal(12, MatchRules.DrawNextRound(12, 11, 24, 0));
        Assert.Equal(0, MatchRules.DrawNextRound(15, 14, 24, 0));
        Assert.Equal(0, MatchRules.DrawNextRound(12, 11, 0, 6));
    }
}

// Who gets an armband when a captain does not come back. Three tiers, and the tie-break between equals
// is the caller's shuffle, so what is under test is only which group gets asked.
public class CaptainCandidateTests
{
    private static readonly ulong[] Here = [10, 11, 12, 13];

    [Fact]
    public void ALeftoverVolunteerComesFirst()
    {
        var picked = MatchRules.CaptainCandidates(
            Here, other: 13, volunteers: new HashSet<ulong> { 12 },
            votedTheMapIn: new HashSet<ulong> { 10, 11, 12 });
        Assert.Equal([12UL], picked);
    }

    [Fact]
    public void TheOtherCaptainIsNeverACandidate()
    {
        var picked = MatchRules.CaptainCandidates(
            Here, other: 12, volunteers: new HashSet<ulong> { 12 },
            votedTheMapIn: new HashSet<ulong> { 10 });
        Assert.Equal([10UL], picked);
    }

    [Fact]
    public void WithNoVolunteerLeftTheRoomThatVotedIsAsked()
    {
        var picked = MatchRules.CaptainCandidates(
            Here, other: 13, volunteers: new HashSet<ulong>(), votedTheMapIn: new HashSet<ulong> { 11, 12 });
        Assert.Equal([11UL, 12UL], picked);
    }

    [Fact]
    public void WithNobodyLeftFromThatRoomAnybodyPresentWillDo()
    {
        var picked = MatchRules.CaptainCandidates(
            Here, other: 13, volunteers: new HashSet<ulong>(), votedTheMapIn: new HashSet<ulong>());
        Assert.Equal([10UL, 11UL, 12UL], picked);
    }

    [Fact]
    public void AnEmptyRoomHasNobodyToAsk()
    {
        Assert.Empty(MatchRules.CaptainCandidates([], other: 0, volunteers: new HashSet<ulong>(), votedTheMapIn: new HashSet<ulong>()));
    }
}
