using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// Half a minute of held freeze time at the start of round one, telling the side that lost the knife to
// get into their Discord channel. The Discord bot can move people on its own, and this stays anyway: the
// bot cannot move somebody who has not joined voice, and round one going live while half the server is
// still dragging themselves between channels is the thing being prevented.
//
// The losing side moves because the winners picked the side they wanted and get to stay put.
public partial class Gpp
{
    // The side that has to move. Set when the knife round is decided; without a knife round it is
    // whichever side team2 ended up on, so somebody is always named.
    private CsTeam _moveSide = CsTeam.None;

    private bool _openingPause;
    private int _openingSecondsLeft;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _openingTimer;

    internal bool OpeningPauseRunning => _openingTimer != null;

    // Called on the first round start after going live.
    private void StartOpeningPause()
    {
        if (Config.Match.OpeningPauseSeconds <= 0) return;
        if (OpeningPauseRunning) return;

        _openingPause = true;
        _openingSecondsLeft = Config.Match.OpeningPauseSeconds;
        Server.ExecuteCommand("mp_pause_match");
        Logger.LogInformation("Opening pause: {Seconds}s for {Side} to move in Discord.",
            _openingSecondsLeft, _moveSide == CsTeam.CounterTerrorist ? "CT" : "T");
        Announce($"{Colorize("{green}")}{SideWord(_moveSide)}{Colorize("{default}")} - move into your "
            + $"Discord channel. Live in {_openingSecondsLeft}s.");

        _openingTimer?.Kill();
        _openingTimer = AddTimer(1.0f, OpeningTick, TimerFlags.REPEAT);
        OpeningPanel();
    }

    // An admin can cut it short; everybody is already where they need to be more often than not.
    internal void EndOpeningPause(string why)
    {
        if (!OpeningPauseRunning) return;
        _openingTimer?.Kill();
        _openingTimer = null;
        _openingPause = false;
        _openingSecondsLeft = 0;
        Clear();
        Server.ExecuteCommand("mp_unpause_match");
        Announce($"Here we go. {why}");
    }

    private void OpeningTick()
    {
        if (_matchPhase != MatchPhase.Live)
        {
            AfterTick(() => EndOpeningPause("The match is not live any more."));
            return;
        }

        if (--_openingSecondsLeft > 0)
        {
            OpeningPanel();
            return;
        }

        AfterTick(() => EndOpeningPause("Time is up."));
    }

    private void OpeningPanel()
    {
        if (!Config.Hud) return;
        BumpPaintTick();

        // Their words, in the box people actually read. The lower panel carries the clock and who it is
        // aimed at, so the alert box can be the three lines and nothing else.
        var lines = MatchRules.OpeningLines((int)_moveSide);
        string alert = Alert(lines[0], lines[1], lines[2]);

        foreach (var player in HumanPlayers())
        {
            bool yours = player.Team == _moveSide;
            Paint(player, alert,
            [
                yours ? "that means you - your team channel" : $"waiting on {SideName(_moveSide)}",
                $"live in {_openingSecondsLeft}s",
            ]);
        }
    }

    private static string SideWord(CsTeam side) => MatchRules.OpeningLines((int)side)[0];
}
