using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
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

    // Who has said they are in their channel. The clock is the backstop, not the gate: a side that has
    // sorted itself out in eight seconds should not stand around for the other twenty-two.
    private readonly HashSet<ulong> _openingReady = [];
    private CounterStrikeSharp.API.Modules.Timers.Timer? _openingTimer;

    internal bool OpeningPauseRunning => _openingTimer != null;

    // Called on the first round start after going live.
    private void StartOpeningPause()
    {
        if (Config.Match.OpeningPauseSeconds <= 0) return;
        if (OpeningPauseRunning) return;

        _openingPause = true;
        _openingReady.Clear();
        _openingSecondsLeft = Config.Match.OpeningPauseSeconds;
        Server.ExecuteCommand("mp_pause_match");
        Logger.LogInformation("Opening pause: {Seconds}s for {Side} to move in Discord.",
            _openingSecondsLeft, _moveSide == CsTeam.CounterTerrorist ? "CT" : "T");
        Announce($"{Colorize("{green}")}{SideWord(_moveSide)}{Colorize("{default}")} - move into your "
            + $"Discord channel. Live in {_openingSecondsLeft}s.");
        Prompt("In your channel?", ".ready", Everybody);

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

        // Play is actually starting, so this is the last thing in chat - which is the point of it.
        // Not when the hold was cut short because the match stopped being live.
        if (_matchPhase == MatchPhase.Live) AnnounceCreditLast();
    }

    // Everyone playing, which during a match is everyone on a side - not the drafted roster, because
    // anybody can walk in and take a slot once a pug is live.
    private List<CCSPlayerController> OpeningPlayers() => HumanPlayers()
        .Where(player => !player.IsBot
            && (player.Team == CsTeam.CounterTerrorist || player.Team == CsTeam.Terrorist))
        .ToList();

    // .ready during the opening pause. Returns false when there is no opening pause to answer.
    internal bool ReadyForOpening(CCSPlayerController? player)
    {
        if (!OpeningPauseRunning) return false;
        if (player == null || !player.IsValid) return true;

        if (_openingReady.Add(Id(player)))
        {
            var playing = OpeningPlayers();
            Announce($"{player.PlayerName} is in ({_openingReady.Count}/{playing.Count}).");
            if (playing.All(other => _openingReady.Contains(Id(other))))
            {
                AfterTick(() => EndOpeningPause("Everybody is in their channel."));
            }
        }
        return true;
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
        _repaint = OpeningPanel;
        WorldOpeningPanel();
    }

    private static string SideWord(CsTeam side) => MatchRules.OpeningLines((int)side)[0];
}
