using Microsoft.Data.Sqlite;

namespace Gpp;

// Every read the stats site does, as SQL with nothing web-shaped around it, so that Gpp.Tests can run it
// against a database StatsStore wrote. The writer is the plugin; this side never creates a table, never
// migrates and never writes - a schema it does not recognise is a bug in the pairing, not something to
// fix up at read time.
public static class StatsQueries
{
    // Read-only, and WAL means a reader sees committed rows while a match is still being played. The
    // connection string is here rather than in the web host because the tests open the same way the
    // service does; `immutable=1` would read a stale snapshot that silently omits anything still in the
    // -wal file, so it is not an option.
    public static SqliteConnection OpenReadOnly(string path) =>
        Open($"Data Source={path};Mode=ReadOnly");

    private static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public record MatchRow(
        long MatchId,
        string Map,
        string Team1,
        string Team2,
        int Score1,
        int Score2,
        string Winner,
        string StartedAt,
        string? EndedAt,
        int Players)
    {
        // A row exists from the moment a match goes live, so an unfinished pug is a real row with no
        // end: the site says so rather than showing it as a 0-0 loss.
        public bool Finished => !string.IsNullOrEmpty(EndedAt);
    }

    public record ScoreRow(
        ulong SteamId,
        string Name,
        string Team,
        int Kills,
        int Deaths,
        int Assists,
        int Damage,
        int Headshots,
        int Mvps);

    public record PlayerTotals(
        ulong SteamId,
        string Name,
        int Matches,
        int Wins,
        int Kills,
        int Deaths,
        int Assists,
        int Damage,
        int Headshots,
        int Mvps);

    private const string MatchColumns = """
        m.matchid, m.map, m.team1, m.team2, m.score1, m.score2, m.winner, m.started_at, m.ended_at,
        (SELECT COUNT(*) FROM gpp_players p WHERE p.matchid = m.matchid)
        """;

    // Newest first by id: matchid is assigned in order and started_at is only second-precision, so two
    // matches in the same second would otherwise come back in whatever order SQLite liked.
    public static List<MatchRow> RecentMatches(SqliteConnection connection, int limit, int offset = 0)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {MatchColumns}
              FROM gpp_matches m
             ORDER BY m.matchid DESC
             LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        return ReadMatches(command);
    }

    public static MatchRow? Match(SqliteConnection connection, long matchId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {MatchColumns}
              FROM gpp_matches m
             WHERE m.matchid = $matchid
            """;
        command.Parameters.AddWithValue("$matchid", matchId);
        return ReadMatches(command).FirstOrDefault();
    }

    // The scoreboard as the server would have shown it: by kills, then by fewest deaths.
    public static List<ScoreRow> Scoreboard(SqliteConnection connection, long matchId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT steamid64, name, team, kills, deaths, assists, damage, headshots, mvps
              FROM gpp_players
             WHERE matchid = $matchid
             ORDER BY kills DESC, deaths ASC, name ASC
            """;
        command.Parameters.AddWithValue("$matchid", matchId);

        var rows = new List<ScoreRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ScoreRow(
                SteamId: SteamId(reader, 0),
                Name: reader.GetString(1),
                Team: reader.GetString(2),
                Kills: reader.GetInt32(3),
                Deaths: reader.GetInt32(4),
                Assists: reader.GetInt32(5),
                Damage: reader.GetInt32(6),
                Headshots: reader.GetInt32(7),
                Mvps: reader.GetInt32(8)));
        }
        return rows;
    }

    // A win is this player's team name matching the winner the plugin wrote. An unfinished match has an
    // empty winner, so it counts as a match played and not as a loss.
    private const string TotalsSelect = """
        SELECT p.steamid64,
               (SELECT p2.name FROM gpp_players p2
                 WHERE p2.steamid64 = p.steamid64 ORDER BY p2.matchid DESC LIMIT 1) AS name,
               COUNT(*) AS matches,
               SUM(CASE WHEN m.winner <> '' AND m.winner = p.team THEN 1 ELSE 0 END) AS wins,
               SUM(p.kills), SUM(p.deaths), SUM(p.assists),
               SUM(p.damage), SUM(p.headshots), SUM(p.mvps)
          FROM gpp_players p
          JOIN gpp_matches m ON m.matchid = p.matchid
        """;

    public static PlayerTotals? Totals(SqliteConnection connection, ulong steamId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            {TotalsSelect}
             WHERE p.steamid64 = $steamid
             GROUP BY p.steamid64
            """;
        command.Parameters.AddWithValue("$steamid", unchecked((long)steamId));
        return ReadTotals(command).FirstOrDefault();
    }

    public static List<PlayerTotals> Leaderboard(SqliteConnection connection, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            {TotalsSelect}
             GROUP BY p.steamid64
             ORDER BY matches DESC, SUM(p.kills) DESC
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        return ReadTotals(command);
    }

    // The matches one player appeared in, newest first, with their line in each.
    public static List<(MatchRow Match, ScoreRow Score)> PlayerMatches(
        SqliteConnection connection, ulong steamId, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {MatchColumns},
                   p.steamid64, p.name, p.team, p.kills, p.deaths, p.assists, p.damage, p.headshots,
                   p.mvps
              FROM gpp_players p
              JOIN gpp_matches m ON m.matchid = p.matchid
             WHERE p.steamid64 = $steamid
             ORDER BY m.matchid DESC
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$steamid", unchecked((long)steamId));
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<(MatchRow, ScoreRow)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((ReadMatch(reader), new ScoreRow(
                SteamId: SteamId(reader, 10),
                Name: reader.GetString(11),
                Team: reader.GetString(12),
                Kills: reader.GetInt32(13),
                Deaths: reader.GetInt32(14),
                Assists: reader.GetInt32(15),
                Damage: reader.GetInt32(16),
                Headshots: reader.GetInt32(17),
                Mvps: reader.GetInt32(18))));
        }
        return rows;
    }

    private static List<MatchRow> ReadMatches(SqliteCommand command)
    {
        var rows = new List<MatchRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(ReadMatch(reader));
        return rows;
    }

    private static MatchRow ReadMatch(SqliteDataReader reader) => new(
        MatchId: reader.GetInt64(0),
        Map: reader.GetString(1),
        Team1: reader.GetString(2),
        Team2: reader.GetString(3),
        Score1: reader.GetInt32(4),
        Score2: reader.GetInt32(5),
        Winner: reader.GetString(6),
        StartedAt: reader.GetString(7),
        EndedAt: reader.IsDBNull(8) ? null : reader.GetString(8),
        Players: reader.GetInt32(9));

    private static List<PlayerTotals> ReadTotals(SqliteCommand command)
    {
        var rows = new List<PlayerTotals>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PlayerTotals(
                SteamId: SteamId(reader, 0),
                Name: reader.GetString(1),
                Matches: reader.GetInt32(2),
                Wins: reader.GetInt32(3),
                Kills: reader.GetInt32(4),
                Deaths: reader.GetInt32(5),
                Assists: reader.GetInt32(6),
                Damage: reader.GetInt32(7),
                Headshots: reader.GetInt32(8),
                Mvps: reader.GetInt32(9)));
        }
        return rows;
    }

    // SQLite has no unsigned integer, so the plugin stores a SteamID64's bit pattern in a signed column;
    // reading it back is the same cast the other way.
    private static ulong SteamId(SqliteDataReader reader, int ordinal) =>
        unchecked((ulong)reader.GetInt64(ordinal));
}
