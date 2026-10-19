using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// A server nobody has been on for hours does not behave like one that has just started. The warmup has
// been held open that whole time, the round has been restarted by every phase that came and went, and the
// first people to connect get a round that does odd things - which is what gets complained about, and
// what a map load or a restart fixes outright.
//
// It asks rather than acts. Reloading the map under whoever just connected, or restarting the server on
// them, is the same complaint from the other side - so the panel stays up until somebody picks:
//
//   !reload    load this map again - keeps everyone connected
//   !restart   restart the server  - everyone reconnects, clears everything
//   !skip      leave it alone      - until the next time the server has sat empty
//
// Anyone can pick. None of the three does anything unless the panel is up, so !restart is not a command
// players have; it is one of three answers to a question the server asked.
public partial class Gpp
{
    // When the server last had a real player on it, or null while it has one. Starts at "empty now", so a
    // plugin load is not mistaken for ten hours of idling.
    private DateTime? _emptySince = DateTime.UtcNow;

    // Occupancy is only a question about an idle server, so it is not asked while a match is on - and not
    // every tick either. The threshold is half an hour; five-second resolution is plenty, and it keeps a
    // per-second walk of the player list off a server that is busy doing something that matters.
    private const int IdleCheckSeconds = 5;
    private int _idleCheckTicks;

    // How long the server had been empty when somebody turned up, while the panel is asking about it.
    private TimeSpan _idleWas;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _idlePromptTimer;

    internal bool IdlePromptUp => _idlePromptTimer != null;

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

        if (Config.IdleReloadMinutes <= 0) return;
        if (idle.TotalMinutes < Config.IdleReloadMinutes) return;
        // Nothing is offered over the top of a pug.
        if (MatchRunning || _phase != PugPhase.Idle || CaptainOptInOpen || RestartPending) return;

        OpenIdlePrompt(idle);
    }

    private void OpenIdlePrompt(TimeSpan idle)
    {
        _idleWas = idle;
        Logger.LogInformation("Server was empty for {Hours:F1}h - offering reload/restart/skip.", idle.TotalHours);
        Announce($"Server has been idle for {Describe(idle)}, which makes the first round behave oddly.");
        Prompt("Fix it:", "!reload / !restart / !skip", Everybody);

        _idlePromptTimer?.Kill();
        _idlePromptTimer = AddTimer(1.0f, IdlePromptTick, TimerFlags.REPEAT);
        IdlePromptPanel();
    }

    private void CloseIdlePrompt()
    {
        _idlePromptTimer?.Kill();
        _idlePromptTimer = null;
        Clear();
    }

    // The panel stays up until one of the three is picked. The only thing that takes it down by itself is
    // a pug starting, which answers the question a different way.
    private void IdlePromptTick()
    {
        if (_phase != PugPhase.Idle || MatchRunning || CaptainOptInOpen)
        {
            AfterTick(CloseIdlePrompt);
            return;
        }
        if (!HumanPlayers().Any(player => !player.IsBot))
        {
            // Everybody left again. The server is idling once more, so the question will be asked afresh.
            AfterTick(CloseIdlePrompt);
            return;
        }
        IdlePromptPanel();
    }

    private void IdlePromptPanel()
    {
        if (!Config.Hud) return;
        BumpPaintTick();

        // The alert box explains; the lower panel carries what to type. Three lines up there because the
        // sentence does not fit two and the clock is the part people care about.
        string alert = Alert("IDLE SERVER FIX", "server has been idle for", Describe(_idleWas));
        var panel = Columns(
        [
            $"!reload - load {Server.MapName} again",
            "!restart - restart the server",
            "!skip - leave it",
        ]);

        foreach (var player in HumanPlayers()) Paint(player, alert, panel);
    }

    // --- the three answers ------------------------------------------------------------------------

    [ConsoleCommand("css_reload", "Reload the current map (offered when the server has been idle)")]
    public void OnReloadCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => AnswerIdlePrompt(Caller(player), "reload"));

    [ConsoleCommand("css_restart", "Restart the server (offered when the server has been idle)")]
    public void OnRestartServerCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => AnswerIdlePrompt(Caller(player), "restart"));

    [ConsoleCommand("css_skip", "Leave an idle server as it is")]
    public void OnSkipCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => AnswerIdlePrompt(Caller(player), "skip"));

    internal void AnswerIdlePrompt(CCSPlayerController? player, string answer)
    {
        if (!IdlePromptUp)
        {
            // Not a standing command. Without the panel up there is nothing being asked, and !restart in
            // particular is not something anybody gets to type at a server mid-pug.
            Tell(player, "Nothing to answer - that is offered when the server has been sitting empty.");
            return;
        }

        string who = player != null && player.IsValid ? player.PlayerName : "An admin";
        CloseIdlePrompt();

        switch (answer)
        {
            case "reload":
                Announce($"{Colorize("{green}")}{who}{Colorize("{default}")} picked a map reload - loading "
                    + $"{Server.MapName} again.");
                AfterTick(ReloadThisMap);
                break;

            case "restart":
                // Ten seconds rather than the usual thirty: whoever asked for it is standing there
                // waiting, and anyone else who has just connected is still loading in anyway.
                AskForRestart($"{who} asked for a restart on an idle server", 10);
                break;

            default:
                Announce($"{who} left it as it is. Ask again next time the server has been empty a while.");
                break;
        }
    }

    // The map that is running, loaded again. A workshop map is addressed by its id, so the id comes from
    // the pool rather than from Server.MapName, which is the map's own name and not something
    // host_workshop_map will take.
    private void ReloadThisMap()
    {
        string running = Server.MapName;
        var pooled = Config.MapPool.FirstOrDefault(map =>
            map.IsWorkshop && map.Name.Equals(running, StringComparison.OrdinalIgnoreCase));

        if (pooled != null)
        {
            Logger.LogInformation("Reloading workshop map {Id} ({Name})", pooled.WorkshopId, running);
            Server.ExecuteCommand($"host_workshop_map \"{pooled.WorkshopId}\"");
            return;
        }

        Logger.LogInformation("Reloading map {Map}", running);
        Server.ExecuteCommand($"changelevel \"{running}\"");
    }

    // "10 hours", "45 minutes" - the number people would use when complaining about it.
    private static string Describe(TimeSpan idle)
    {
        if (idle.TotalHours >= 2) return $"{idle.TotalHours:F0} hours";
        if (idle.TotalHours >= 1) return "over an hour";
        return $"{idle.TotalMinutes:F0} minutes";
    }
}
