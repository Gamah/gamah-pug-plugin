using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace Gpp;

// Match and player stats in a SQLite file of our own, written twice per pug: a row when the match goes
// live, the scores and the player rows when it ends. MatchZy's tables carried forty columns of Get5
// bookkeeping (entry duels, utility successes, 1vN counts) off events GPP does not follow; this keeps the
// handful a pug actually gets asked about, read off the engine's own counters at the end of a round.
//
// Nothing here runs while a round is live. The writes take a snapshot of plain numbers
// and hand it to a task, because a disk hitch inside a round is a visible stutter - and because a
// CCSPlayerController must never be touched off-thread.
public partial class Gpp
{
    private class PlayerStat
    {
        public string Name = "";
        public string Team = "";
        public int Kills;
        public int Deaths;
        public int Assists;
        public int Damage;
        public int Headshots;
        public int Mvps;
    }

    private readonly Dictionary<ulong, PlayerStat> _stats = [];

    // What the engine had counted for each player when the match went live.
    private readonly Dictionary<ulong, PlayerStat> _baseline = [];

    // Read off the engine at the end of a round, not counted off kills and hurts as they happen. The
    // engine keeps the same figures the scoreboard shows, so nothing of ours has to run while a round
    // is live - and intra-round detail, which was never stored anyway, is what a demo is for.
    private void HookMatchStats()
    {
        RegisterEventHandler<EventRoundEnd>((_, _) =>
        {
            if (Config.Match.StatsEnabled && _matchPhase == MatchPhase.Live) AfterTick(CollectStats);
            return HookResult.Continue;
        });
    }

    // MatchStats is cumulative and outlives a round, so what the match went live on is subtracted:
    // warmup and the knife round are not stats. A player who turns up mid-match baselines on the first
    // round end that sees them, which is the same rule - nothing before they arrived is theirs.
    private void CaptureStatsBaseline()
    {
        _baseline.Clear();
        if (!Config.Match.StatsEnabled) return;
        foreach (var player in HumanPlayers()) Baseline(player);
    }

    private PlayerStat? Baseline(CCSPlayerController player)
    {
        if (player.IsBot || player.IsHLTV || !player.IsValid) return null;
        var tracking = player.ActionTrackingServices;
        if (tracking == null) return null;

        ulong id = Id(player);
        if (_baseline.TryGetValue(id, out var known)) return known;

        var stats = tracking.MatchStats;
        var from = new PlayerStat
        {
            Kills = stats.Kills,
            Deaths = stats.Deaths,
            Assists = stats.Assists,
            Damage = stats.Damage,
            Headshots = stats.HeadShotKills,
            Mvps = player.MVPs,
        };
        _baseline[id] = from;
        return from;
    }

    private void CollectStats()
    {
        if (!Config.Match.StatsEnabled) return;

        foreach (var player in HumanPlayers())
        {
            var from = Baseline(player);
            if (from == null) continue;

            var stats = player.ActionTrackingServices!.MatchStats;
            var stat = Stat(player);
            stat.Kills = stats.Kills - from.Kills;
            stat.Deaths = stats.Deaths - from.Deaths;
            stat.Assists = stats.Assists - from.Assists;
            stat.Damage = stats.Damage - from.Damage;
            stat.Headshots = stats.HeadShotKills - from.Headshots;
            stat.Mvps = player.MVPs - from.Mvps;
        }
    }

    // Bots have no SteamID, so they are counted into nothing: a test run's numbers are not worth a row.
    private PlayerStat Stat(CCSPlayerController player)
    {
        ulong id = Id(player);
        if (!_stats.TryGetValue(id, out var stat))
        {
            stat = new PlayerStat();
            _stats[id] = stat;
        }
        stat.Name = player.PlayerName;
        stat.Team = _team1.Contains(id) ? _team1Name : _team2.Contains(id) ? _team2Name : "guest";
        return stat;
    }

    private void ClearStats()
    {
        _stats.Clear();
        _baseline.Clear();
    }

    private string DatabasePath()
    {
        string file = Config.Match.StatsDatabase;
        return Path.IsPathRooted(file) ? file : Path.Join(ModuleDirectory, file);
    }

    private void RecordMatchStart()
    {
        if (!Config.Match.StatsEnabled) return;

        long matchId = _matchId;
        string map = Server.MapName;
        string team1 = _team1Name;
        string team2 = _team2Name;
        DateTime startedAt = _matchStartedAt;
        string path = DatabasePath();

        Write(path, connection => StatsStore.StartMatch(connection, matchId, map, team1, team2, startedAt));
    }

    private void RecordMatchEnd(int score1, int score2, string winner)
    {
        if (!Config.Match.StatsEnabled) return;

        long matchId = _matchId;
        string path = DatabasePath();
        DateTime endedAt = DateTime.UtcNow;

        // Snapshot first: everything below runs off the game thread, where a CCSPlayerController must not
        // be touched. Bots have no SteamID and no row.
        var rows = _stats
            .Where(entry => !IsBotId(entry.Key))
            .Select(entry => new StatsStore.PlayerRow(entry.Key, entry.Value.Name, entry.Value.Team,
                entry.Value.Kills, entry.Value.Deaths, entry.Value.Assists, entry.Value.Damage,
                entry.Value.Headshots, entry.Value.Mvps))
            .ToList();

        Write(path, connection =>
            StatsStore.EndMatch(connection, matchId, score1, score2, winner, endedAt, rows));
    }

    // A disk hitch inside a round is a visible stutter, so the write goes to a task. A pug is not worth
    // failing over stats either: a broken database (or a SQLite native library that will not load at all)
    // is one line in the log and the match carries on.
    private void Write(string path, Action<SqliteConnection> work)
    {
        Task.Run(() =>
        {
            try
            {
                using var connection = StatsStore.Open(path);
                work(connection);
            }
            catch (Exception exception)
            {
                Logger.LogError("Could not write stats to {Path}: {Message}", path, exception.Message);
            }
        });
    }
}
