using Microsoft.Data.Sqlite;
using Gpp;
using Xunit;

namespace Gpp.Tests;

// Against a real SQLite file, because the point is to prove the SQL rather than the code around it.
public class StatsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gpp-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static readonly DateTime Started = new(2026, 10, 19, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ended = new(2026, 10, 19, 4, 48, 0, DateTimeKind.Utc);

    [Fact]
    public void SchemaIsCreatedAndIsIdempotent()
    {
        using var first = StatsStore.Open(_path);
        StatsStore.EnsureSchema(first);
        StatsStore.EnsureSchema(first);

        Assert.Equal(1L, Scalar(first, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'gpp_matches'"));
        Assert.Equal(1L, Scalar(first, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'gpp_players'"));
    }

    // A match that goes live has a row from that moment, so a pug that never finished is still in there.
    [Fact]
    public void StartMatchWritesTheRowBeforeThereIsAScore()
    {
        using var connection = StatsStore.Open(_path);
        StatsStore.StartMatch(connection, 17, "de_mirage", "team_Ada", "team_Baz", Started);

        Assert.Equal("de_mirage", Scalar(connection, "SELECT map FROM gpp_matches WHERE matchid = 17"));
        Assert.Equal(0L, Scalar(connection, "SELECT score1 FROM gpp_matches WHERE matchid = 17"));
        Assert.Equal("2026-10-19T04:00:00", Scalar(connection, "SELECT started_at FROM gpp_matches WHERE matchid = 17"));
        Assert.Null(Scalar(connection, "SELECT ended_at FROM gpp_matches WHERE matchid = 17"));
    }

    [Fact]
    public void EndMatchWritesScoresAndPlayers()
    {
        using var connection = StatsStore.Open(_path);
        StatsStore.StartMatch(connection, 17, "de_mirage", "team_Ada", "team_Baz", Started);
        StatsStore.EndMatch(connection, 17, 13, 9, "team_Ada", Ended,
        [
            new StatsStore.PlayerRow(76561198121804652, "Ada", "team_Ada", 24, 15, 4, 2800, 11, 5),
            new StatsStore.PlayerRow(76561198012274010, "Baz", "team_Baz", 15, 22, 2, 1900, 3, 1),
        ]);

        Assert.Equal(13L, Scalar(connection, "SELECT score1 FROM gpp_matches WHERE matchid = 17"));
        Assert.Equal(9L, Scalar(connection, "SELECT score2 FROM gpp_matches WHERE matchid = 17"));
        Assert.Equal("team_Ada", Scalar(connection, "SELECT winner FROM gpp_matches WHERE matchid = 17"));
        Assert.Equal("2026-10-19T04:48:00", Scalar(connection, "SELECT ended_at FROM gpp_matches WHERE matchid = 17"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM gpp_players WHERE matchid = 17"));
        Assert.Equal(2800L, Scalar(connection,
            "SELECT damage FROM gpp_players WHERE matchid = 17 AND name = 'Ada'"));
    }

    // SQLite integers are signed and a SteamID64 is not, so the only thing that matters is that what goes
    // in comes back out as the same number.
    [Fact]
    public void SteamIdSurvivesTheRoundTrip()
    {
        const ulong steamId = 76561199092051251;

        using var connection = StatsStore.Open(_path);
        StatsStore.StartMatch(connection, 1, "de_nuke", "a", "b", Started);
        StatsStore.EndMatch(connection, 1, 1, 0, "a", Ended,
            [new StatsStore.PlayerRow(steamId, "Z", "a", 1, 0, 0, 100, 0, 0)]);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT steamid64 FROM gpp_players WHERE matchid = 1";
        long stored = (long)command.ExecuteScalar()!;
        Assert.Equal(steamId, unchecked((ulong)stored));
    }

    // A reload mid-match, or a second pug with the same id, must not throw on the primary keys.
    [Fact]
    public void WritingTheSameMatchTwiceReplacesIt()
    {
        using var connection = StatsStore.Open(_path);
        StatsStore.StartMatch(connection, 5, "de_nuke", "a", "b", Started);
        StatsStore.StartMatch(connection, 5, "de_train", "c", "d", Started);
        StatsStore.EndMatch(connection, 5, 5, 13, "d", Ended,
            [new StatsStore.PlayerRow(76561198003730347, "C", "c", 1, 2, 3, 400, 1, 0)]);
        StatsStore.EndMatch(connection, 5, 6, 13, "d", Ended,
            [new StatsStore.PlayerRow(76561198003730347, "C", "c", 2, 2, 3, 500, 1, 0)]);

        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM gpp_matches"));
        Assert.Equal("de_train", Scalar(connection, "SELECT map FROM gpp_matches WHERE matchid = 5"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM gpp_players WHERE matchid = 5"));
        Assert.Equal(500L, Scalar(connection, "SELECT damage FROM gpp_players WHERE matchid = 5"));
    }

    // Nobody got a kill, or stats were on with an empty server: the match row still has to be finished.
    [Fact]
    public void EndMatchWithNoPlayersStillScoresTheMatch()
    {
        using var connection = StatsStore.Open(_path);
        StatsStore.StartMatch(connection, 9, "de_ancient", "a", "b", Started);
        StatsStore.EndMatch(connection, 9, 0, 0, "", Ended, []);

        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM gpp_players WHERE matchid = 9"));
        Assert.Equal("", Scalar(connection, "SELECT winner FROM gpp_matches WHERE matchid = 9"));
        Assert.NotNull(Scalar(connection, "SELECT ended_at FROM gpp_matches WHERE matchid = 9"));
    }

    [Fact]
    public void OpenCreatesTheDirectory()
    {
        string nested = Path.Combine(Path.GetTempPath(), $"gpp-test-{Guid.NewGuid():N}", "stats", "gpp.db");
        try
        {
            using var connection = StatsStore.Open(nested);
            Assert.True(File.Exists(nested));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string? directory = Path.GetDirectoryName(Path.GetDirectoryName(nested));
            if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }
}
