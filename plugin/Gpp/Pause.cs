using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// .pause / .tech / .unpause, kept to the shape MatchZy and Get5 have: a pause takes effect at the next
// freeze time rather than mid-round, both sides have to .unpause before play resumes, and a technical
// pause can be capped per side and can be unpaused by either side once it has run long enough.
//
// Two engine facts the rules are built on. mp_pause_match does not pause anything until the round ends,
// so the seconds a pause has lasted are only counted during freeze time - which is also when a pause the
// caller has changed their mind about can still be taken back for free. And unpausing out of halftime or
// the post-game panel does not come back, so both refuse the command outright.
// [SOURCE: MatchZy 0.9.1 Pausing.cs, PauseLogic.cs, Utility.cs PauseMatch/UnpauseMatch, read 2026-10-19]
public partial class Gpp
{
    private bool _paused;
    private bool _pauseIsTechnical;

    // The side that called it, and how long it has been in effect (-1 until freeze time).
    private CsTeam _pausingSide = CsTeam.None;
    private int _pauseSeconds = -1;

    // Technical pauses used this match, by side. Sides swap at the half; the count belongs to the team,
    // so it is keyed on team number (1/2) and looked up through the side map.
    private readonly int[] _techPausesUsed = new int[3];

    // Who has asked to unpause. Both sides have to ask, so one entry per side.
    private readonly HashSet<CsTeam> _unpauseAsked = [];

    private CounterStrikeSharp.API.Modules.Timers.Timer? _pauseTimer;

    private void ResetPauses()
    {
        _paused = false;
        _pauseIsTechnical = false;
        _pausingSide = CsTeam.None;
        _pauseSeconds = -1;
        _techPausesUsed[1] = 0;
        _techPausesUsed[2] = 0;
        _unpauseAsked.Clear();
        _pauseTimer?.Kill();
        _pauseTimer = null;
    }

    private int TeamNumberOf(CsTeam side) => side == _team1Side ? 1 : 2;

    [ConsoleCommand("css_pause", "Pause the match at the end of the round")]
    public void OnPauseCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => PauseMatch(Caller(player), technical: false));

    [ConsoleCommand("css_tech", "Call a technical pause")]
    public void OnTechPauseCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => PauseMatch(Caller(player), technical: true));

    [ConsoleCommand("css_unpause", "Ask to resume - both sides have to ask")]
    public void OnUnpauseCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => AskToUnpause(Caller(player)));

    private CCSPlayerController? Caller(CCSPlayerController? player) =>
        player == null || !player.IsValid ? null : PlayerBySteamId(Id(player));

    internal void PauseMatch(CCSPlayerController? player, bool technical)
    {
        if (!Config.Match.PausesEnabled)
        {
            Tell(player, "Pauses are turned off on this server.");
            return;
        }
        if (_matchPhase != MatchPhase.Live)
        {
            Tell(player, "Nothing to pause - no match is live.");
            return;
        }
        if (_paused)
        {
            Tell(player, "The match is already paused.");
            return;
        }
        if (InHalftime() || InPostGame())
        {
            Tell(player, "Not during halftime or after the match.");
            return;
        }

        // An admin over rcon has no side; the pause is theirs and nobody's to use up.
        CsTeam side = player != null && player.IsValid ? player.Team : CsTeam.None;
        if (player != null && side != CsTeam.CounterTerrorist && side != CsTeam.Terrorist)
        {
            Tell(player, "Only players in the match can pause it.");
            return;
        }

        if (technical && side != CsTeam.None)
        {
            int used = _techPausesUsed[TeamNumberOf(side)];
            if (!MatchRules.CanCallTechPause(used, Config.Match.MaxTechPauses))
            {
                Announce($"{SideName(side)} have used all {Config.Match.MaxTechPauses} of their technical pauses.");
                return;
            }
        }

        _paused = true;
        _pauseIsTechnical = technical;
        _pausingSide = side;
        _pauseSeconds = -1;
        _unpauseAsked.Clear();
        Server.ExecuteCommand("mp_pause_match");

        string who = side == CsTeam.None ? "An admin" : SideName(side);
        string kind = technical ? "a technical pause" : "a pause";
        Announce($"{Colorize("{green}")}{who}{Colorize("{default}")} called {kind} - it starts at the end of the round.");
        Prompt("To resume:", ".unpause", "both sides");

        _pauseTimer?.Kill();
        _pauseTimer = AddTimer(1.0f, PauseTick, TimerFlags.REPEAT);
        StartMatchHud();
    }

    // A pause only counts time while the engine is actually holding the game, which is freeze time. The
    // technical pause is counted as used on its first such second, so a pause taken back before it lands
    // costs nothing.
    private void PauseTick()
    {
        if (!_paused || _matchPhase != MatchPhase.Live)
        {
            _pauseTimer?.Kill();
            _pauseTimer = null;
            return;
        }
        if (!(GameRules()?.FreezePeriod ?? false)) return;

        _pauseSeconds++;
        if (_pauseSeconds == 0)
        {
            // The round the pause was called in has ended and the engine is actually holding the game
            // now. Worth saying out loud: between the call and this, play carries on, and a panel that
            // said PAUSED through all of it was simply wrong.
            Announce($"{Colorize("{green}")}Paused.{Colorize("{default}")} "
                + (_pausingSide == CsTeam.None ? "An admin" : SideName(_pausingSide)) + " called it.");
            if (_pauseIsTechnical && _pausingSide != CsTeam.None)
            {
                _techPausesUsed[TeamNumberOf(_pausingSide)]++;
            }
        }
    }

    // True when one side alone may resume: a technical pause that has run its time limit.
    private bool AnyoneCanUnpause() =>
        MatchRules.AnyoneCanUnpause(_pauseIsTechnical, _pauseSeconds, Config.Match.TechPauseSeconds);

    internal void AskToUnpause(CCSPlayerController? player)
    {
        // Round one's Discord pause is not a team pause and does not need both sides: anyone who is
        // already where they need to be can say so.
        if (OpeningPauseRunning)
        {
            string who = player != null && player.IsValid ? player.PlayerName : "An admin";
            EndOpeningPause($"{who} says they are ready.");
            return;
        }

        if (!_paused)
        {
            Tell(player, "The match is not paused.");
            return;
        }
        if (InHalftime() || InPostGame())
        {
            Tell(player, "Not during halftime or after the match.");
            return;
        }

        CsTeam side = player != null && player.IsValid ? player.Team : CsTeam.None;

        // The console resumes it outright - that is an admin over rcon, with no side in the match.
        //
        // An admin who is *playing* does not: half the regulars on this server are in admins.json, so an
        // admin bypass would mean "both sides have to agree" held only for whoever had not been given a
        // flag. They ask like everybody else, and an admin who wants to override does it from rcon or
        // with !pug.
        if (side == CsTeam.None)
        {
            Unpause("An admin");
            return;
        }
        // Checked before anything that names a side: a spectator is on neither, and a pause is not
        // theirs to end.
        if (side != CsTeam.CounterTerrorist && side != CsTeam.Terrorist)
        {
            Tell(player, "Only players in the match can unpause it.");
            return;
        }
        if (side == _pausingSide && _pauseSeconds < 0)
        {
            Unpause($"{SideName(side)} (taking their pause back)");
            return;
        }
        if (AnyoneCanUnpause())
        {
            Unpause(SideName(side));
            return;
        }
        if (!_unpauseAsked.Add(side))
        {
            Tell(player, "Your side has already asked to resume.");
            return;
        }

        var other = side == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        if (_unpauseAsked.Contains(other))
        {
            Unpause("both sides");
            return;
        }
        Announce($"{SideName(side)} want to resume - waiting on {SideName(other)} to type .unpause.");
    }

    private void Unpause(string who)
    {
        Server.ExecuteCommand("mp_unpause_match");
        _paused = false;
        _pauseIsTechnical = false;
        _pausingSide = CsTeam.None;
        _pauseSeconds = -1;
        _unpauseAsked.Clear();
        _pauseTimer?.Kill();
        _pauseTimer = null;
        Announce($"Unpaused by {who}.");
        Logger.LogInformation("Match unpaused by {Who}.", who);
    }

    // What the pause panel says, and the only place the tech-pause count is spelled out.
    // True once the engine is actually holding the game, rather than a pause having been asked for.
    internal bool PauseHasLanded => _paused && _pauseSeconds >= 0;

    internal string PauseLine()
    {
        if (!_paused) return "";
        string who = _pausingSide == CsTeam.None ? "an admin" : SideName(_pausingSide);
        if (!PauseHasLanded) return $"{who} called it - the round finishes first";
        if (!_pauseIsTechnical) return $"paused by {who}";

        int used = _pausingSide == CsTeam.None ? 0 : _techPausesUsed[TeamNumberOf(_pausingSide)];
        string count = Config.Match.MaxTechPauses > 0 ? $" ({used}/{Config.Match.MaxTechPauses})" : "";
        if (AnyoneCanUnpause()) return $"tech pause by {who}{count} - either side can .unpause";

        int wait = MatchRules.SecondsUntilAnyoneCanUnpause(true, _pauseSeconds, Config.Match.TechPauseSeconds);
        return $"tech pause by {who}{count}{(wait >= 0 ? $", {wait}s" : "")}";
    }
}
