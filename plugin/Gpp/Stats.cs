using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace Gpp;

// Match and player stats in a SQLite file of our own, written twice per pug: a row when the match goes
// live, the scores and the player rows when it ends. MatchZy's tables carried forty columns of Get5
// bookkeeping (entry duels, utility successes, 1vN counts) off events GPP does not follow; this keeps the
// handful a pug actually gets asked about and nothing that cannot be read straight off a kill or a hurt.
//
// Nothing here runs on the game thread except the counting. The writes take a snapshot of plain numbers
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

    private void HookMatchStats()
    {
        RegisterEventHandler<EventPlayerDeath>((@event, _) =>
        {
            if (!CountingStats()) return HookResult.Continue;

            var victim = @event.Userid;
            var attacker = @event.Attacker;
            var assister = @event.Assister;

            if (victim != null && victim.IsValid) Stat(victim).Deaths++;
            // Team kills count as a death for the victim and nothing for the killer, as the scoreboard
            // does - minus one kill is how the engine scores it, and a negative pug stat reads as a bug.
            if (attacker != null && attacker.IsValid && victim != null && attacker.Team != victim.Team)
            {
                var stat = Stat(attacker);
                stat.Kills++;
                if (@event.Headshot) stat.Headshots++;
            }
            if (assister != null && assister.IsValid && victim != null && assister.Team != victim.Team)
            {
                Stat(assister).Assists++;
            }
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerHurt>((@event, _) =>
        {
            if (!CountingStats()) return HookResult.Continue;

            var victim = @event.Userid;
            var attacker = @event.Attacker;
            if (attacker == null || !attacker.IsValid || victim == null || !victim.IsValid) return HookResult.Continue;
            if (attacker.Team == victim.Team) return HookResult.Continue;

            // DmgHealth is already capped at the health the victim had, so this is damage dealt rather
            // than damage swung.
            Stat(attacker).Damage += @event.DmgHealth;
            return HookResult.Continue;
        });

        RegisterEventHandler<EventRoundMvp>((@event, _) =>
        {
            if (!CountingStats()) return HookResult.Continue;
            var player = @event.Userid;
            if (player != null && player.IsValid) Stat(player).Mvps++;
            return HookResult.Continue;
        });
    }

    // Only a live, unpaused match counts. Warmup kills and knife-round kills are not stats.
    private bool CountingStats()
    {
        return Config.Match.StatsEnabled && _matchPhase == MatchPhase.Live && !_paused && !InWarmupPeriod();
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

    private void ClearStats() => _stats.Clear();

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
