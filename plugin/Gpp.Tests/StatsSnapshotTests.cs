using Microsoft.Data.Sqlite;
using Xunit;

namespace Gpp.Tests;

// The snapshot exists for one reason: the stats site could not open the live database at all, because a
// WAL database needs its "-shm" sidecar created in the database's own directory and the site has no write
// access there. So these tests check the properties that make the copy readable without that access -
// non-WAL, no sidecars, same rows - not merely that a file appeared.
public class StatsSnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"gpp-snap-{Guid.NewGuid():N}");

    private string Live => Path.Combine(_directory, "live", "gpp.db");
    private string Copy => Path.Combine(_directory, "copy", "gpp.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static readonly DateTime Started = new(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc);
    private const ulong Asta = 76561197960265728UL;

    private void Seed(SqliteConnection db)
    {
        StatsStore.StartMatch(db, 1, "de_aztec", "team_Ada", "team_Baz", Started);
        StatsStore.EndMatch(db, 1, 13, 7, "team_Ada", Started.AddMinutes(40), new[]
        {
            new StatsStore.PlayerRow(Asta, "asta", "team_Ada", 22, 12, 5, 2900, 8, 4),
        });
    }

    private static string JournalMode(string path)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public void LiveDatabaseIsWalAndTheSnapshotIsNot()
    {
        using var db = StatsStore.Open(Live);
        Seed(db);
        StatsStore.Snapshot(db, Copy);

        // The premise: if the live file ever stops being WAL, the snapshot has no reason to exist.
        Assert.Equal("wal", JournalMode(Live));
        Assert.NotEqual("wal", JournalMode(Copy));
    }

    [Fact]
    public void SnapshotLeavesNoSidecarsToCreate()
    {
        using var db = StatsStore.Open(Live);
        Seed(db);
        StatsStore.Snapshot(db, Copy);

        // -shm is the file the site could not create. -wal and the staging copy are gone too, so the
        // directory holds exactly one file and reading it asks for nothing.
        Assert.Equal(new[] { "gpp.db" },
            Directory.GetFiles(Path.GetDirectoryName(Copy)!).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void SnapshotCarriesTheRowsTheSiteReads()
    {
        using var db = StatsStore.Open(Live);
        Seed(db);
        StatsStore.Snapshot(db, Copy);

        using var read = StatsQueries.OpenReadOnly(Copy);
        var matches = StatsQueries.RecentMatches(read, 10);

        Assert.Equal(new long[] { 1 }, matches.Select(m => m.MatchId));
        Assert.Equal("de_aztec", matches[0].Map);
        Assert.Equal(1, matches[0].Players);
    }

    [Fact]
    public void SnapshotOverwritesTheOlderCopy()
    {
        using var db = StatsStore.Open(Live);
        Seed(db);
        StatsStore.Snapshot(db, Copy);

        StatsStore.StartMatch(db, 2, "de_cpl_mill", "team_Cyd", "team_Dee", Started.AddHours(1));
        StatsStore.Snapshot(db, Copy);

        using var read = StatsQueries.OpenReadOnly(Copy);
        Assert.Equal(new long[] { 2, 1 }, StatsQueries.RecentMatches(read, 10).Select(m => m.MatchId));
    }

    [Fact]
    public void SnapshotSurvivesStagingLeftOverFromACrash()
    {
        using var db = StatsStore.Open(Live);
        Seed(db);

        Directory.CreateDirectory(Path.GetDirectoryName(Copy)!);
        File.WriteAllText(Copy + ".tmp", "half a database from the write that died");

        StatsStore.Snapshot(db, Copy);

        using var read = StatsQueries.OpenReadOnly(Copy);
        Assert.Single(StatsQueries.RecentMatches(read, 10));
    }

    [Fact]
    public void SnapshotCreatesItsDirectory()
    {
        using var db = StatsStore.Open(Live);
        Seed(db);

        string nested = Path.Combine(_directory, "not", "there", "yet", "gpp.db");
        StatsStore.Snapshot(db, nested);

        Assert.True(File.Exists(nested));
    }
}
