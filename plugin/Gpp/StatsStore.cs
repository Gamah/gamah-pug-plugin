using Microsoft.Data.Sqlite;

namespace Gpp;

// The stats database: the schema and the three statements that write it, with no CounterStrikeSharp in
// sight so that Gpp.Tests can run them against a real SQLite file. SQL typos and a missing parameter look
// exactly like working code until a pug ends.
internal static class StatsStore
{
    internal record PlayerRow(
        ulong SteamId,
        string Name,
        string Team,
        int Kills,
        int Deaths,
        int Assists,
        int Damage,
        int Headshots,
        int Mvps);

    // Opened per write: a pug server writes twice an hour, so there is nothing to gain from holding a
    // connection open, and a crashed plugin cannot leave a locked file behind.
    public static SqliteConnection Open(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        EnsureSchema(connection);
        return connection;
    }

    public static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // WAL so a reader (a stats page, sqlite3 on the box) can read while a match is being written.
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS gpp_matches (
                matchid    INTEGER PRIMARY KEY,
                map        TEXT    NOT NULL,
                team1      TEXT    NOT NULL,
                team2      TEXT    NOT NULL,
                score1     INTEGER NOT NULL DEFAULT 0,
                score2     INTEGER NOT NULL DEFAULT 0,
                winner     TEXT    NOT NULL DEFAULT '',
                started_at TEXT    NOT NULL,
                ended_at   TEXT
            );
            CREATE TABLE IF NOT EXISTS gpp_players (
                matchid   INTEGER NOT NULL,
                steamid64 INTEGER NOT NULL,
                name      TEXT    NOT NULL,
                team      TEXT    NOT NULL DEFAULT '',
                kills     INTEGER NOT NULL DEFAULT 0,
                deaths    INTEGER NOT NULL DEFAULT 0,
                assists   INTEGER NOT NULL DEFAULT 0,
                damage    INTEGER NOT NULL DEFAULT 0,
                headshots INTEGER NOT NULL DEFAULT 0,
                mvps      INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (matchid, steamid64)
            );
            """;
        command.ExecuteNonQuery();
    }

    // Written when the match goes live, so a match that never finishes still leaves a row saying it ran.
    public static void StartMatch(SqliteConnection connection, long matchId, string map, string team1,
        string team2, DateTime startedAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO gpp_matches
                (matchid, map, team1, team2, score1, score2, winner, started_at, ended_at)
            VALUES ($matchid, $map, $team1, $team2, 0, 0, '', $started, NULL)
            """;
        command.Parameters.AddWithValue("$matchid", matchId);
        command.Parameters.AddWithValue("$map", map);
        command.Parameters.AddWithValue("$team1", team1);
        command.Parameters.AddWithValue("$team2", team2);
        command.Parameters.AddWithValue("$started", Stamp(startedAt));
        command.ExecuteNonQuery();
    }

    // The scores, and every player who did something while the match was live, in one transaction: half a
    // scoreboard is worse than none.
    public static void EndMatch(SqliteConnection connection, long matchId, int score1, int score2,
        string winner, DateTime endedAt, IEnumerable<PlayerRow> players)
    {
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE gpp_matches
                   SET score1 = $score1, score2 = $score2, winner = $winner, ended_at = $ended
                 WHERE matchid = $matchid
                """;
            command.Parameters.AddWithValue("$score1", score1);
            command.Parameters.AddWithValue("$score2", score2);
            command.Parameters.AddWithValue("$winner", winner);
            command.Parameters.AddWithValue("$ended", Stamp(endedAt));
            command.Parameters.AddWithValue("$matchid", matchId);
            command.ExecuteNonQuery();
        }

        foreach (var player in players)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO gpp_players
                    (matchid, steamid64, name, team, kills, deaths, assists, damage, headshots, mvps)
                VALUES ($matchid, $steamid, $name, $team, $kills, $deaths, $assists, $damage, $headshots, $mvps)
                """;
            command.Parameters.AddWithValue("$matchid", matchId);
            // SQLite has no unsigned integer, and a SteamID64 does not fit a signed one as a number anyone
            // would read; it is stored as the same bit pattern and read back with the same cast.
            command.Parameters.AddWithValue("$steamid", unchecked((long)player.SteamId));
            command.Parameters.AddWithValue("$name", player.Name);
            command.Parameters.AddWithValue("$team", player.Team);
            command.Parameters.AddWithValue("$kills", player.Kills);
            command.Parameters.AddWithValue("$deaths", player.Deaths);
            command.Parameters.AddWithValue("$assists", player.Assists);
            command.Parameters.AddWithValue("$damage", player.Damage);
            command.Parameters.AddWithValue("$headshots", player.Headshots);
            command.Parameters.AddWithValue("$mvps", player.Mvps);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // Seconds precision, UTC, sortable as text - which is the only thing SQLite can do with it.
    private static string Stamp(DateTime when) => when.ToUniversalTime().ToString("s");
}
