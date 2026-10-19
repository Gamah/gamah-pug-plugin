using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// A server nobody has been on for ten hours does not behave like one that has just started. The warmup
// has been held open that whole time, the round has been restarted by every phase that came and went, and
// the first people to connect get a round that does odd things - which is what gets complained about, and
// what a map load fixes outright.
//
// So: the moment somebody arrives on a server that has been empty longer than idle_reload_minutes, say
// what is about to happen, give them half a minute to read it, and load the map fresh. The warning is the
// point. Reloading the map under someone with no notice is the same bug from their side.
public partial class Gpp
{
    // When the server last had a real player on it, or null while it has one. Starts at "empty now", so a
    // plugin load is not mistaken for ten hours of idling.
    private DateTime? _emptySince = DateTime.UtcNow;

    private int _idleReloadSecondsLeft;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _idleReloadTimer;

    // Occupancy is only a question about an idle server, so it is not asked while a match is on - and not
    // every tick either. The threshold is half an hour; five-second resolution is plenty, and it keeps a
    // per-second walk of the player list off a server that is busy doing something that matters.
    private const int IdleCheckSeconds = 5;
    private int _idleCheckTicks;

    internal bool IdleReloadPending => _idleReloadTimer != null;

    // Called once a second from the warmup watch. The transition is followed here rather than from the
    // connect hook because the two race: by the time a connect is handled, the tick has already seen the
    // player and forgotten how long the server was empty.
    private void TrackIdleTime()
    {
        // A match means the server is occupied by definition. Clearing the stamp here is also what stops
        // a long match from reading as a long idle spell the moment it ends.
        if (MatchRunning)
        {
            _emptySince = null;
            return;
        }
        if (++_idleCheckTicks < IdleCheckSeconds) return;
        _idleCheckTicks = 0;

        bool anybody = HumanPlayers().Any(player => !player.IsBot);

        if (!anybody)
        {
            _emptySince ??= DateTime.UtcNow;
            return;
        }

        if (_emptySince == null) return;

        var idle = DateTime.UtcNow - _emptySince.Value;
        _emptySince = null;

        if (Config.IdleRestartHours > 0 && idle.TotalHours >= Config.IdleRestartHours)
        {
            // Long enough that a fresh map is not obviously enough. A restart costs whoever just
            // connected a reconnect, which is why it is not what half an hour of idling gets.
            AskForRestart($"the server had been idle for {Describe(idle)}");
            return;
        }

        if (Config.IdleReloadMinutes <= 0) return;
        if (idle.TotalMinutes < Config.IdleReloadMinutes) return;
        // Nothing is reloaded out from under a pug. An empty server cannot have had one running, but a
        // reload armed and then cancelled leaves this the only guard that matters.
        if (MatchRunning || _phase != PugPhase.Idle || CaptainOptInOpen) return;

        ArmIdleReload(idle);
    }

    private void ArmIdleReload(TimeSpan idle)
    {
        _idleReloadSecondsLeft = Math.Max(5, Config.IdleReloadWarningSeconds);
        Logger.LogInformation("Server was empty for {Hours:F1}h - reloading {Map} in {Seconds}s.",
            idle.TotalHours, Config.IdleReloadMap, _idleReloadSecondsLeft);
        Announce($"Server has been idle for {Describe(idle)} - reloading "
            + $"{Colorize("{green}")}{Config.IdleReloadMap}{Colorize("{default}")} in {_idleReloadSecondsLeft}s "
            + "so the first round behaves.");

        _idleReloadTimer?.Kill();
        _idleReloadTimer = AddTimer(1.0f, IdleReloadTick, TimerFlags.REPEAT);
        IdleReloadPanel();
    }

    internal void CancelIdleReload(string why)
    {
        if (_idleReloadTimer == null) return;
        _idleReloadTimer?.Kill();
        _idleReloadTimer = null;
        _idleReloadSecondsLeft = 0;
        Clear();
        Announce($"Map reload cancelled. {why}");
    }

    private void IdleReloadTick()
    {
        // An admin who starts a pug inside the warning window means it now, not after a map change.
        if (_phase != PugPhase.Idle || MatchRunning || CaptainOptInOpen)
        {
            AfterTick(() => CancelIdleReload("A pug is starting."));
            return;
        }

        if (--_idleReloadSecondsLeft > 0)
        {
            if (_idleReloadSecondsLeft % 10 == 0 || _idleReloadSecondsLeft == 5)
            {
                Announce($"Reloading {Config.IdleReloadMap} in {_idleReloadSecondsLeft}s.");
            }
            IdleReloadPanel();
            return;
        }

        AfterTick(() =>
        {
            _idleReloadTimer?.Kill();
            _idleReloadTimer = null;
            Clear();

            string map = Config.IdleReloadMap;
            Logger.LogInformation("Reloading the map after an idle spell: {Map}", map);
            // A workshop map is addressed by id, the same as the vote does it.
            if (long.TryParse(map, out _)) Server.ExecuteCommand($"host_workshop_map \"{map}\"");
            else Server.ExecuteCommand($"changelevel \"{map}\"");
        });
    }

    private void IdleReloadPanel()
    {
        if (!Config.Hud) return;
        BumpPaintTick();

        foreach (var player in HumanPlayers())
        {
            Paint(player, TwoLine("SERVER WAS IDLE", $"reloading in {_idleReloadSecondsLeft}s"),
            [
                $"loading {Config.IdleReloadMap} fresh - an idle server plays a strange first round",
                "you will reconnect on your own",
            ]);
        }
    }

    // "10 hours", "45 minutes" - the number people would use when complaining about it.
    private static string Describe(TimeSpan idle)
    {
        if (idle.TotalHours >= 2) return $"{idle.TotalHours:F0} hours";
        if (idle.TotalHours >= 1) return "over an hour";
        return $"{idle.TotalMinutes:F0} minutes";
    }
}
