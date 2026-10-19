using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// Restarting the whole server from the plugin, which is a map reload's bigger brother: everything the
// engine has accumulated goes, and everyone has to reconnect.
//
// The mechanism is `quit` on the console and systemd starting it again. That needs Restart=always in the
// unit - the stock unit here was Restart=on-failure, and the only reason quit appeared to work was that
// CS2 segfaults on shutdown (status=139), which is not a thing to depend on. There is a drop-in on this
// server adding Restart=always; without it, quit leaves the server down.
// [SOURCE: measured on this server 2026-10-19 - quit exited 139 and systemd restarted it]
public partial class Gpp
{
    private int _restartSecondsLeft;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _restartTimer;
    private string _restartWhy = "";

    internal bool RestartPending => _restartTimer != null;

    internal void AskForRestart(string why, int seconds = 0)
    {
        if (RestartPending) return;

        _restartWhy = why;
        _restartSecondsLeft = Math.Max(5, seconds > 0 ? seconds : Config.RestartWarningSeconds);
        Logger.LogInformation("Restarting the server in {Seconds}s: {Why}", _restartSecondsLeft, why);
        Announce($"{Colorize("{red}")}Server restarting{Colorize("{default}")} in {_restartSecondsLeft}s "
            + $"- {why}. Reconnect in about a minute.");

        _restartTimer?.Kill();
        _restartTimer = AddTimer(1.0f, RestartTick, TimerFlags.REPEAT);
        RestartPanel();
    }

    internal void CancelRestart(string why)
    {
        if (_restartTimer == null) return;
        _restartTimer.Kill();
        _restartTimer = null;
        _restartSecondsLeft = 0;
        Clear();
        Announce($"Restart cancelled. {why}");
    }

    private void RestartTick()
    {
        if (--_restartSecondsLeft > 0)
        {
            if (_restartSecondsLeft % 10 == 0 || _restartSecondsLeft <= 5)
            {
                Announce($"Restarting in {_restartSecondsLeft}s.");
            }
            RestartPanel();
            return;
        }

        AfterTick(() =>
        {
            _restartTimer?.Kill();
            _restartTimer = null;
            Logger.LogInformation("Quitting for a restart: {Why}", _restartWhy);
            // systemd brings it back (Restart=always, RestartSec=10s). Nothing after this line runs.
            Server.ExecuteCommand("quit");
        });
    }

    private void RestartPanel()
    {
        if (!Config.Hud) return;
        _repaint = RestartPanel;
        if (WorldStyle) { WorldRestartPanel(); return; }

        foreach (var player in HumanPlayers())
        {
            Paint(player, TwoLine("SERVER RESTARTING", $"{_restartSecondsLeft}s"),
            [
                _restartWhy,
                "reconnect in about a minute - the server comes back on its own",
            ]);
        }
    }
}
