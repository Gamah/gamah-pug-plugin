using Microsoft.Data.Sqlite;
using Xunit;

namespace Gpp.Tests;

// The read side, against a database StatsStore wrote: the pairing is the thing being tested, so nothing
// here creates a table by hand. A query that compiles and returns the wrong rows looks like a working
// site until somebody checks a scoreboard against what they remember.
public class StatsQueriesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gpp-read-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static readonly DateTime Started = new(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc);

    private const ulong Asta = 76561197960265728UL;
    private const ulong Baz = 76561199999999999UL;

    // Two finished matches and one that never ended, which is what the database looks like after a pug
    // night with a crash in it.
    private void Seed()
    {
        using var db = StatsStore.Open(_path);

        StatsStore.StartMatch(db, 1, "de_mirage", "team_Ada", "team_Baz", Started);
        StatsStore.EndMatch(db, 1, 13, 11, "team_Ada", Started.AddMinutes(48), new[]
        {
            new StatsStore.PlayerRow(Asta, "asta", "team_Ada", 25, 18, 4, 3100, 9, 5),
            new StatsStore.PlayerRow(Baz, "baz", "team_Baz", 19, 22, 6, 2600, 4, 3),
        });

        StatsStore.StartMatch(db, 2, "de_nuke", "team_Cyd", "team_Ada", Started.AddHours(1));
        StatsStore.EndMatch(db, 2, 8, 13, "team_Ada", Started.AddHours(2), new[]
        {
            new StatsStore.PlayerRow(Asta, "asta_v2", "team_Cyd", 11, 20, 2, 1800, 3, 1),
            new StatsStore.PlayerRow(Baz, "baz", "team_Ada", 24, 14, 7, 3300, 11, 6),
        });

        // Live: a row with no end, no winner and no players.
        StatsStore.StartMatch(db, 3, "cs_office", "team_Dee", "team_Eve", Started.AddHours(3));
    }

    [Fact]
    public void RecentMatchesIsNewestFirstAndCountsPlayers()
    {
        Seed();
        using var db = StatsQueries.OpenReadOnly(_path);

        var rows = StatsQueries.RecentMatches(db, 10);

        Assert.Equal(new long[] { 3, 2, 1 }, rows.Select(r => r.MatchId));
        Assert.Equal(new[] { 0, 2, 2 }, rows.Select(r => r.Players));
        Assert.False(rows[0].Finished);
        Assert.True(rows[1].Finished);
    }

    [Fact]
    public void RecentMatchesPages()
    {
        Seed();
        using var db = StatsQueries.OpenReadOnly(_path);

        Assert.Equal(new long[] { 3, 2 }, StatsQueries.RecentMatches(db, 2).Select(r => r.MatchId));
        Assert.Equal(new long[] { 1 }, StatsQueries.RecentMatches(db, 2, 2).Select(r => r.MatchId));
    }

    [Fact]
    public void MatchCarriesTheScoreAndTheWinner()
    {
        Seed();
        using var db = StatsQueries.OpenReadOnly(_path);

        var match = StatsQueries.Match(db, 1);

        Assert.NotNull(match);
        Assert.Equal("de_mirage", match!.Map);
        Assert.Equal(13, match.Score1);
        Assert.Equal(11, match.Score2);
        Assert.Equal("team_Ada", match.Winner);
        Assert.Null(StatsQueries.Match(db, 99));
    }

    [Fact]
    public void ScoreboardIsByKillsThenFewestDeaths()
    {
        Seed();
        using var db = StatsQueries.OpenReadOnly(_path);

        var rows = StatsQueries.Scoreboard(db, 1);

        Assert.Equal(new[] { "asta", "baz" }, rows.Select(r => r.Name));
        Assert.Equal(Asta, rows[0].SteamId);
        Assert.Equal(3100, rows[0].Damage);
        Assert.Empty(StatsQueries.Scoreboard(db, 3));
    }

    // A SteamID64 does not fit a signed 64-bit integer as a readable number; it survives the round trip
    // because both sides use the same unchecked cast.
    [Fact]
    public void SteamIdSurvivesTheRoundTrip()
    {
        using (var db = StatsStore.Open(_path))
        {
            StatsStore.StartMatch(db, 1, "de_dust2", "team_Ada", "team_Baz", Started);
            StatsStore.EndMatch(db, 1, 13, 2, "team_Ada", Started.AddMinutes(30), new[]
            {
                new StatsStore.PlayerRow(ulong.MaxValue, "edge", "team_Ada", 1, 0, 0, 100, 0, 0),
            });
        }
        using var read = StatsQueries.OpenReadOnly(_path);

        Assert.Equal(ulong.MaxValue, StatsQueries.Scoreboard(read, 1).Single().SteamId);
        Assert.Equal(ulong.MaxValue, StatsQueries.Totals(read, ulong.MaxValue)!.SteamId);
    }

    [Fact]
    public void TotalsSumEveryMatchAndCountWinsByTeamName()
    {
        Seed();
        using var db = StatsQueries.OpenReadOnly(_path);

        var asta = StatsQueries.Totals(db, Asta)!;
        var baz = StatsQueries.Totals(db, Baz)!;

        Assert.Equal(2, asta.Matches);
        Assert.Equal(1, asta.Wins);            // won match 1 with team_Ada, lost match 2 with team_Cyd
        Assert.Equal(36, asta.Kills);
        Assert.Equal(38, asta.Deaths);
        Assert.Equal(6, asta.Mvps);
        Assert.Equal(1, baz.Wins);             // on team_Ada in match 2
        Assert.Null(StatsQueries.Totals(db, 1234UL));
    }

    // The name shown is the one from the most recent match, not whichever row SQLite happened to group on.
    [Fact]
    public void TotalsUseTheLatestName()
    {
        Seed();
        using var db = StatsQueries.OpenReadOnly(_path);

        Assert.Equal("asta_v2", StatsQueries.Totals(db, Asta)!.Name);
    }

    [Fact]
    public void LeaderboardIsByMatchesPlayed()
    {
        Seed();
        using var db = StatsQueries.OpenReadOnly(_path);

        var rows = StatsQueries.Leaderboard(db, 10);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(2, r.Matches));
    }

    [Fact]
    public void PlayerMatchesPairsEachMatchWithThatPlayersLine()
    {
        Seed();
        using var db = StatsQueries.OpenReadOnly(_path);

        var rows = StatsQueries.PlayerMatches(db, Asta, 10);

        Assert.Equal(new long[] { 2, 1 }, rows.Select(r => r.Match.MatchId));
        Assert.Equal("team_Cyd", rows[0].Score.Team);
        Assert.Equal(25, rows[1].Score.Kills);
    }

    // The service runs while a match is being written, which is the whole reason the plugin turns WAL on.
    [Fact]
    public void ReadsWhileTheWriterHoldsTheDatabase()
    {
        Seed();
        using var writer = StatsStore.Open(_path);
        StatsStore.StartMatch(writer, 4, "de_ancient", "team_Fay", "team_Gus", Started.AddHours(4));

        using var read = StatsQueries.OpenReadOnly(_path);

        Assert.Equal(4, StatsQueries.RecentMatches(read, 10).Count);
        Assert.Throws<SqliteException>(() =>
        {
            using var command = read.CreateCommand();
            command.CommandText = "DELETE FROM gpp_matches";
            command.ExecuteNonQuery();
        });
    }
}
