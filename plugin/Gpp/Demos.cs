using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Cvars;

namespace Gpp;

// GOTV demo per match. Three things make this less obvious than "tv_record on go-live":
//
//  - tv_record takes a path the engine resolves itself, and a relative one has landed in different places
//    on different builds, so the argument is always an absolute quoted path.
//  - going live runs mp_restartgame, and a recording started before that restart is thrown away with it.
//    So the demo is armed and started at the first round start after the restart, with a fallback timer in
//    case that round start never comes.
//  - at the end of a match GOTV is still sending the last round. Stopping the recording immediately
//    truncates it, so the stop waits out the broadcast delay (see FinishMatch, which stretches
//    mp_match_restart_delay to cover the same window).
//
// [SOURCE: MatchZy 0.9.1 DemoManagement.cs, read 2026-10-19]
public partial class Gpp
{
    private bool _recording;
    private string _demoFile = "";

    // Set while a demo is armed but the restart has not happened yet.
    private DateTime? _demoNotBefore;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _demoArmTimer;

    private void StartDemoAfterRestart(float restartSeconds)
    {
        if (!Config.Match.DemosEnabled) return;

        // tv_record does nothing at all without GOTV, and the first anyone would know is an empty demo
        // directory after a pug.
        if (!(ConVar.Find("tv_enable")?.GetPrimitiveValue<bool>() ?? false))
        {
            Logger.LogWarning("demos are on but tv_enable is 0 - no demo will be recorded. Set tv_enable 1 "
                + "in the server config (or turn demos off in Gpp.json).");
            return;
        }

        _demoArmTimer?.Kill();
        // The round start from mp_warmup_end comes right away; the one after the restart comes later.
        _demoNotBefore = DateTime.UtcNow.AddSeconds(Math.Max(0, restartSeconds - 0.25));
        _demoArmTimer = AddTimer(restartSeconds + 5.0f, () =>
        {
            _demoArmTimer = null;
            if (_demoNotBefore == null) return;
            Logger.LogInformation("No round start after the live restart - starting the demo now.");
            _demoNotBefore = null;
            if (_matchPhase == MatchPhase.Live) StartDemo();
        });
    }

    // Called on every round start; does nothing unless a demo is armed and the restart is behind us.
    private void StartPendingDemo()
    {
        if (_demoNotBefore == null || DateTime.UtcNow < _demoNotBefore) return;
        _demoNotBefore = null;
        _demoArmTimer?.Kill();
        _demoArmTimer = null;
        if (_matchPhase == MatchPhase.Live) StartDemo();
    }

    private void StartDemo()
    {
        if (_recording) return;

        string relative = Path.Join(Config.Match.DemoPath.Trim('/'), DemoName() + ".dem");
        string absolute = Path.Join(Server.GameDirectory, "csgo", relative).Replace('\\', '/');

        try
        {
            string? directory = Path.GetDirectoryName(absolute);
            if (directory != null) Directory.CreateDirectory(directory);
        }
        catch (Exception exception)
        {
            Logger.LogError("Could not create the demo directory for {Path}: {Message}", absolute, exception.Message);
            return;
        }

        // Writing as it records costs a little performance and loses far less of the demo if the server
        // goes down mid-match, which is the only way a pug's demo was ever lost here.
        if (ConVar.Find("tv_record_immediate") != null) Server.ExecuteCommand("tv_record_immediate 1");
        Server.ExecuteCommand($"tv_record \"{absolute}\"");
        _recording = true;
        _demoFile = absolute;
        Logger.LogInformation("Recording demo {Path}", absolute);

        // tv_record failing says so on the server console and nowhere else, so look for the file instead.
        AddTimer(5.0f, () =>
        {
            if (_recording && !File.Exists(absolute))
            {
                Logger.LogWarning("Demo file {Path} was not created - check the console for CDemoFile errors.", absolute);
            }
        });
    }

    private void StopDemo(float delaySeconds)
    {
        _demoNotBefore = null;
        _demoArmTimer?.Kill();
        _demoArmTimer = null;
        if (!_recording) return;

        string file = _demoFile;
        if (delaySeconds <= 0)
        {
            Server.ExecuteCommand("tv_stoprecord");
            _recording = false;
            Logger.LogInformation("Stopped recording {Path}", file);
            return;
        }

        AddTimer(delaySeconds, () =>
        {
            if (!_recording) return;
            Server.ExecuteCommand("tv_stoprecord");
            _recording = false;
            Logger.LogInformation("Stopped recording {Path}", file);
        });
    }

    private string DemoName() => MatchRules.DemoFileName(Config.Match.DemoNameFormat, DateTime.Now,
        _matchId, Server.MapName, _team1Name, _team2Name);
}
