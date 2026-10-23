namespace Gpp;

// The match rules that are arithmetic rather than engine state: the ready threshold, the pause limits, the
// rounds sides swap on, how a knife round is decided from who is left standing, and what a demo is called.
//
// They live in a class of their own, with nothing from CounterStrikeSharp in it, for the same reason
// MatchZy pulled its pause rules out into PauseLogic: none of this can be tried on a server without ten
// people and an hour, and all of it is the kind of off-by-one that only shows up in overtime. Gpp.Tests
// runs every one of them. Anything here that needs a cvar or a player takes it as an argument.
internal static class MatchRules
{
    // How many .ready are needed. 0 (or more than there are) means everybody on the roster; an empty
    // roster still needs somebody to say go, so it is never zero.
    public static int ReadyNeeded(int roster, int minReady)
    {
        if (roster <= 0) return 1;
        return minReady > 0 ? Math.Min(minReady, roster) : roster;
    }

    public static bool CanCallTechPause(int used, int max) => max <= 0 || used < max;

    // Whether one side alone may resume. A tactical pause always needs both; a technical one opens up once
    // it has run its time limit, counted in freeze-time seconds (-1 until it has taken effect at all).
    public static bool AnyoneCanUnpause(bool technical, int secondsElapsed, int timeLimit)
    {
        return technical && timeLimit > 0 && secondsElapsed >= timeLimit;
    }

    // Seconds until that happens, or -1 when it never will.
    public static int SecondsUntilAnyoneCanUnpause(bool technical, int secondsElapsed, int timeLimit)
    {
        if (!technical || timeLimit <= 0) return -1;
        return Math.Max(0, timeLimit - Math.Max(0, secondsElapsed));
    }

    // True when the round that just ended is one the engine swaps sides on: the half, and every overtime
    // half after it. roundsPlayed is the count *after* the round.
    // [SOURCE: MatchZy 0.9.1 Utility.cs IsTeamSwapRequired, from Get5, read 2026-10-19]
    public static bool IsSideSwapRound(int roundsPlayed, int maxRounds, int overtimeMaxRounds, bool halftimeEnabled)
    {
        if (!halftimeEnabled) return false;

        int perHalf = maxRounds / 2;
        if (perHalf <= 0) return false;
        if (roundsPlayed == perHalf) return true;

        int perOtHalf = overtimeMaxRounds / 2;
        if (roundsPlayed < 2 * perHalf || perOtHalf <= 0) return false;

        int otRound = roundsPlayed - (2 * perHalf);
        return (otRound + perOtHalf) % (2 * perOtHalf) == 0;
    }

    // Who won the knife round, as the engine's team numbers (3 = CT, 2 = T) or 0 for a dead heat the
    // caller has to flip a coin over. Alive players first, then the health they have between them - the
    // engine's own round winner is no use, because a knife round that runs out of time has none.
    public const int CtTeam = 3;
    public const int TTeam = 2;

    public static int KnifeWinner(int ctAlive, int ctHealth, int tAlive, int tHealth)
    {
        if (ctAlive != tAlive) return ctAlive > tAlive ? CtTeam : TTeam;
        if (ctHealth != tHealth) return ctHealth > tHealth ? CtTeam : TTeam;
        return 0;
    }

    // {TIME} {MATCHID} {MAP} {TEAM1} {TEAM2}, then everything a filename should not carry taken out: the
    // name comes from player names, which contain quotes, slashes and worse.
    public static string DemoFileName(string format, DateTime now, long matchId, string map,
        string team1, string team2)
    {
        string name = (format ?? "")
            .Replace("{TIME}", now.ToString("yyyyMMdd-HHmmss"))
            .Replace("{MATCHID}", matchId.ToString())
            .Replace("{MAP}", map)
            .Replace("{TEAM1}", team1)
            .Replace("{TEAM2}", team2)
            .Replace(' ', '_');

        var safe = name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray();
        return safe.Length > 0 ? new string(safe) : $"gpp-{matchId}";
    }

    // Who has to move in Discord at the start of round one: the side that lost the knife round. The
    // winners picked the side they wanted and stay where they are.
    public static int MovingSide(int knifeWinner) => knifeWinner == CtTeam ? TTeam
        : knifeWinner == TTeam ? CtTeam
        : 0;

    // .switch moves both teams across, so anything tracked as "this side" moves with them.
    public static int OtherSide(int side) => side == CtTeam ? TTeam : side == TTeam ? CtTeam : 0;

    // The three lines of the round-one panel, which are shouted on purpose.
    public static string[] OpeningLines(int side) =>
    [
        side == CtTeam ? "COUNTER-TERRORISTS" : side == TTeam ? "TERRORISTS" : "EVERYONE",
        "STFU AND MOVE",
        "IN DISCORD",
    ];

    // mp_teamname_1/2 take the raw rest of the console line, so a team name is letters, digits and the one
    // underscore and nothing else - a quote or a semicolon in a player's name would otherwise be part of
    // the command.
    public static string TeamName(string captainName)
    {
        string name = new((captainName ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (name.Length > 12) name = name[..12];
        return name.Length == 0 ? "team" : $"team_{name}";
    }
}
