using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// Ready -> knife -> sides -> live -> over. This is the part MatchZy used to run, and it is here because
// nothing else about MatchZy could be kept: every per-match thing it does hangs off a loaded match config,
// and loading one is what kicked anyone outside the drafted roster. The replacement keeps the open server -
// anyone may walk in and pick a side once the pug is live - and gives up MatchZy's round backups, which
// means a match that breaks mid-way cannot be restored. That was decided, not overlooked.
//
// Three cvars are the whole phase machine as far as the engine is concerned: a held warmup
// (mp_warmup_start + mp_warmuptime 9999 + mp_warmup_pausetimer 1, from warmup.cfg) is "not playing",
// mp_warmup_end + mp_restartgame starts a round for real, and mp_halftime/mp_maxrounds let the engine run
// the sides. Everything below is about which cfg is exec'd when, and who is allowed to say "go".
public enum MatchPhase
{
    None,
    Ready,
    Knife,
    SideChoice,
    Live,
    Over,
}

public partial class Gpp
{
    private MatchPhase _matchPhase = MatchPhase.None;

    // Unix seconds, and the stats key for the match.
    private long _matchId;
    private string _matchMap = "";
    private DateTime _matchStartedAt;

    // Which side team1 is on. The draft puts team1 on CT, .switch and every half swap flip it, and
    // everything that has to turn "team1" into "a side" (or back) goes through here.
    private CsTeam _team1Side = CsTeam.CounterTerrorist;

    private string _team1Name = "team1";
    private string _team2Name = "team2";

    // Seconds the engine's own restart after exec'ing live.cfg takes. MatchZy calls this liveRestartDelay
    // and uses it for the same thing: a demo started before the restart is lost.
    private const float LiveRestartSeconds = 1.0f;

    internal bool MatchRunning => _matchPhase != MatchPhase.None;

    private void HookMatch()
    {
        // The knife round is decided here, before the round end is sent on, so the end-of-round panel and
        // its sound name the side that actually won the knives rather than the side the engine scored.
        // The win panel event would be the obvious place and is not available: EventCsWinPanelRound
        // stopped firing in CS2 some updates ago, which is why MatchZy moved this to round end too.
        // [SOURCE: MatchZy 0.9.1 MatchZy.cs EventRoundEnd pre-hook, EventHandlers.cs:130, read 2026-10-19]
        RegisterEventHandler<EventRoundEnd>((@event, _) =>
        {
            if (_matchPhase != MatchPhase.Knife) return HookResult.Continue;

            var side = DetermineKnifeSide(out string why);
            @event.Winner = (int)side;
            @event.Reason = side == CsTeam.CounterTerrorist ? 8 : 9;
            // Not inside the event: finishing the knife round execs a cfg and starts timers.
            AfterTick(() => FinishKnifeRound(side, why));
            return HookResult.Changed;
        }, HookMode.Pre);

        // Post: the half swaps, so team1 is still team1 afterwards.
        RegisterEventHandler<EventRoundEnd>((@event, _) =>
        {
            if (_matchPhase == MatchPhase.Live && IsSideSwapRound())
            {
                _team1Side = _team1Side == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
                Logger.LogInformation("Half swap: {Team} is now {Side}.", _team1Name,
                    _team1Side == CsTeam.CounterTerrorist ? "CT" : "T");
            }

            // Next tick, not this one: the side scores are still being written while the round is ending,
            // and a clinch read off a stale score is a match called a round early.
            if (_matchPhase == MatchPhase.Live && Config.Match.OwnMatchEnd) AfterTick(CheckForOurOwnClinch);
            return HookResult.Continue;
        }, HookMode.Post);

        // The match is over. This fires for any match the engine ends, including one nobody here started,
        // which is why everything below it is safe to run from an idle server.
        RegisterEventHandler<EventCsWinPanelMatch>((_, _) =>
        {
            AfterTick(() => FinishMatch(engineEnded: true));
            return HookResult.Continue;
        });

        // The demo cannot start until after the restart that going live performs.
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            StartPendingDemo();

            // Round one, freeze time: hold it while the losing side sorts out Discord. Not from GoLive
            // itself, because the restart it runs has not happened yet at that point.
            if (_matchPhase == MatchPhase.Live && !_openingDone && (GameRules()?.TotalRoundsPlayed ?? 1) == 0)
            {
                _openingDone = true;
                AfterTick(StartOpeningPause);
            }

            // Asserted here rather than at go-live, because `exec` queues the file's contents *behind*
            // whatever is sent after it: live.cfg's own mp_match_can_clinch 1 landed last and won. The
            // engine then clinched at 3-0 and went to intermission one tick before we called the match
            // ourselves, and an intermission is the lock - a step of movement, then snapped back.
            if (_matchPhase == MatchPhase.Live && Config.Match.OwnMatchEnd)
            {
                Server.ExecuteCommand("mp_match_can_clinch 0");
            }
            return HookResult.Continue;
        });

        // Going live ends the warmup, which is the moment the ready panel has nothing left to ask for.
        RegisterEventHandler<EventRoundAnnounceMatchStart>((_, _) =>
        {
            StopReadyHud();
            return HookResult.Continue;
        });

        HookMatchStats();
    }

    // Everything a pug phase must not have running underneath it. These are GPP's own match commands now,
    // so the guard is about the vote and the draft, not about another plugin.
    private static readonly string[] GuardedCommands =
    [
        "css_ready", "css_unready", "css_notready", "css_forceready", "css_start", "css_force",
        "css_forcestart", "css_stay", "css_switch", "css_swap", "css_pause", "css_unpause", "css_tech",
    ];

    private void HookGuardedCommands()
    {
        foreach (var command in GuardedCommands)
        {
            AddCommandListener(command, OnGuardedCommand, HookMode.Pre);
        }
    }

    private HookResult OnGuardedCommand(CCSPlayerController? player, CounterStrikeSharp.API.Modules.Commands.CommandInfo command)
    {
        if (_phase == PugPhase.Idle) return HookResult.Continue;

        string what = _phase == PugPhase.MapVote ? "map vote" : "draft";
        if (player != null && player.IsValid)
        {
            player.PrintToChat(Prefix() + $"Not now - a {what} is running. An admin can !pug cancel.");
        }
        return HookResult.Stop;
    }

    // --- cfgs -------------------------------------------------------------------------------------

    // exec is fire and forget: the console says nothing a plugin can read, and a missing file is a warning
    // in the server log and no cvars changed. So a missing cfg falls back to the handful of settings the
    // phase cannot do without, rather than carrying on with whatever the last phase left behind.
    private void ExecPhaseCfg(string relativePath, string fallback, string what)
    {
        string absolute = Path.Join(Server.GameDirectory, "csgo", "cfg", relativePath);
        if (File.Exists(absolute))
        {
            Server.ExecuteCommand($"exec {relativePath}");
            return;
        }

        Logger.LogWarning("{What} config {Path} is missing - using built-in defaults. Copy examples/cfg/gpp "
            + "into csgo/cfg/gpp.", what, absolute);
        Server.ExecuteCommand(fallback);
    }

    // A held warmup: warmup running, its clock stopped, nobody scoring. This is the state an idle pug
    // server sits in, and the one the ready phase and the side choice are played out in.
    //
    // mp_freezetime 0 is load-bearing. The warmup clock is paused, and a paused clock does not count a
    // freeze period down either - so a round started in here with live.cfg's mp_freezetime 20 still in
    // place freezes everybody where they stand, for good. That is what a called match looked like from
    // inside: a warmup that worked in every way except that nobody could move.
    private const string WarmupFallback =
        "mp_autoteambalance 0;mp_limitteams 0;mp_maxmoney 16000;mp_startmoney 16000;mp_free_armor 0;"
        + "mp_freezetime 0;mp_ignore_round_win_conditions 0;mp_warmup_start;mp_warmuptime 9999;"
        + "mp_warmup_pausetimer 1";

    private const string KnifeFallback =
        "mp_ct_default_secondary \"\";mp_t_default_secondary \"\";mp_free_armor 1;mp_freezetime 10;"
        + "mp_give_player_c4 0;mp_maxmoney 0;mp_respawn_immunitytime 0;mp_respawn_on_death_ct 0;"
        + "mp_respawn_on_death_t 0;mp_roundtime 1.92;mp_roundtime_defuse 1.92;mp_roundtime_hostage 1.92;"
        + "mp_round_restart_delay 3;mp_team_intro_time 0;mp_warmup_pausetimer 0";

    private const string LiveFallback =
        "mp_maxrounds 24;mp_halftime 1;mp_overtime_enable 1;mp_overtime_maxrounds 6;mp_freezetime 15;"
        + "mp_maxmoney 16000;mp_startmoney 800;mp_roundtime 1.92;mp_roundtime_defuse 1.92;"
        + "mp_free_armor 0;mp_give_player_c4 1;mp_autoteambalance 0;mp_limitteams 0;"
        + "mp_warmup_pausetimer 0";

    internal void ExecWarmupCfg() => ExecPhaseCfg(Config.Match.WarmupCfg, WarmupFallback, "Warmup");

    // --- starting ---------------------------------------------------------------------------------

    // Called when the draft finishes. The teams are already on their sides; this is the match around them.
    private void StartMatch(string mapLabel)
    {
        _matchPhase = MatchPhase.Ready;
        _matchId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _matchMap = mapLabel;
        _matchStartedAt = DateTime.UtcNow;
        _team1Side = CsTeam.CounterTerrorist;
        _knifeDone = false;
        _moveSide = SideOf(2);
        _team1Name = TeamName(_captain1);
        _team2Name = TeamName(_captain2);
        ResetPauses();
        ClearStats();
        _openingDone = false;

        // Nobody starts a round with a warped camera or a spun view.
        ClearFunEffects();

        // The draft ran with the round held still (mp_ignore_round_win_conditions 1, warmup ended). The
        // ready phase wants the opposite: a warmup people can shoot in, going nowhere until they ready up.
        ExecWarmupCfg();
        Server.ExecuteCommand($"mp_teamname_1 \"{_team1Name}\";mp_teamname_2 \"{_team2Name}\";"
            + "mp_autoteambalance 0;mp_limitteams 0;mp_ignore_round_win_conditions 0");

        MoveDiscordTeams();
        StartReadyPhase();
        Prompt($"Teams set on {mapLabel}.", ".ready", Everybody);
        StartReadyHud(mapLabel);
    }

    // --- knife ------------------------------------------------------------------------------------

    // Everybody readied. Either the knife round for sides, or straight to live - and straight to live is
    // also the way out of the optional second ready check, which happens after the knife round is over.
    private void StartKnifeOrLive()
    {
        if (!Config.KnifeRound || _knifeDone)
        {
            GoLive();
            return;
        }

        _matchPhase = MatchPhase.Knife;
        StopReadyHud();
        ExecPhaseCfg(Config.Match.KnifeCfg, KnifeFallback, "Knife");
        // The knife round is a real round: warmup off, scores on, and a restart so everyone spawns into it.
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0;mp_restartgame 1;mp_warmup_end");
        Announce($"{Colorize("{green}")}KNIFE!{Colorize("{default}")} Winner picks the side to start on.");
        StartMatchHud();
    }

    // Alive players decide it, then total health, then a coin flip - the engine's own round winner is no
    // use because a knife round that runs out of time has no winner at all.
    // [SOURCE: MatchZy 0.9.1 Utility.cs DetermineKnifeWinner, read 2026-10-19]
    private CsTeam DetermineKnifeSide(out string why)
    {
        var (ctAlive, ctHealth) = SideStanding(CsTeam.CounterTerrorist);
        var (tAlive, tHealth) = SideStanding(CsTeam.Terrorist);

        int winner = MatchRules.KnifeWinner(ctAlive, ctHealth, tAlive, tHealth);
        if (winner == 0)
        {
            why = "dead even - coin flip";
            return _rng.Next(2) == 0 ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        }

        why = ctAlive != tAlive ? $"{Math.Max(ctAlive, tAlive)} left standing" : "more health left";
        return winner == MatchRules.CtTeam ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
    }

    private void FinishKnifeRound(CsTeam winner, string why)
    {
        if (_matchPhase != MatchPhase.Knife) return;

        _knifeDone = true;
        _knifeSide = winner;
        // The losers move in Discord: the winners picked the side they wanted and stay where they are.
        _moveSide = (CsTeam)MatchRules.MovingSide((int)winner);
        _matchPhase = MatchPhase.SideChoice;

        // Back to a held warmup while they decide, so the next round does not start without them.
        ExecWarmupCfg();

        string name = SideName(winner);
        Announce($"{Colorize("{green}")}{name}{Colorize("{default}")} won the knife ({why}).");
        Prompt("Pick a side.", ".stay or .switch", $"{name} only");
        StartMatchHud();
        StartSideClock();
    }

    // Thirty seconds to call it, then the side they are already standing on.
    //
    // A side choice that waits forever is a pug that waits forever: the winning captain can be loading,
    // arguing in voice, or - on a test server - a bot that will never type anything. Staying is the
    // choice that changes nothing, so it is the safe thing to do when nobody says otherwise.
    private const int SideChoiceSeconds = 30;

    internal int _sideSecondsLeft;

    private CounterStrikeSharp.API.Modules.Timers.Timer? _sideTimer;

    private void StartSideClock()
    {
        _sideSecondsLeft = SideChoiceSeconds;
        _sideTimer?.Kill();
        _sideTimer = AddTimer(1.0f, () =>
        {
            if (_matchPhase != MatchPhase.SideChoice)
            {
                AfterTick(StopSideClock);
                return;
            }
            if (--_sideSecondsLeft > 0) return;

            AfterTick(() =>
            {
                StopSideClock();
                if (_matchPhase != MatchPhase.SideChoice) return;
                Announce("No call in 30s - staying.");
                ChooseSide(null, stay: true);
            });
        }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
    }

    private void StopSideClock()
    {
        _sideTimer?.Kill();
        _sideTimer = null;
        _sideSecondsLeft = 0;
    }

    // The side that won the knife round, while the choice is open.
    private CsTeam _knifeSide = CsTeam.None;

    // Set once the knife round has been played, so a ready check after it does not start another one.
    private bool _knifeDone;

    // Set once round one's opening pause has been done, so it is not run again at every restart.
    private bool _openingDone;

    private (int alive, int health) SideStanding(CsTeam side)
    {
        int alive = 0;
        int health = 0;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsHLTV || player.Team != side) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || pawn.Health <= 0) continue;
            alive++;
            health += pawn.Health;
        }
        return (alive, health);
    }

    [ConsoleCommand("css_stay", "Keep the side you won the knife round on")]
    public void OnStayCommand(CCSPlayerController? player, CounterStrikeSharp.API.Modules.Commands.CommandInfo command) =>
        AfterTick(() => ChooseSide(player == null || !player.IsValid ? null : PlayerBySteamId(Id(player)), stay: true));

    [ConsoleCommand("css_switch", "Take the other side after the knife round")]
    [ConsoleCommand("css_swap", "Take the other side after the knife round")]
    public void OnSwitchCommand(CCSPlayerController? player, CounterStrikeSharp.API.Modules.Commands.CommandInfo command) =>
        AfterTick(() => ChooseSide(player == null || !player.IsValid ? null : PlayerBySteamId(Id(player)), stay: false));

    // .stay / .switch, from a player on the winning side or from the console.
    internal void ChooseSide(CCSPlayerController? player, bool stay)
    {
        if (_matchPhase != MatchPhase.SideChoice)
        {
            if (player != null && player.IsValid) player.PrintToChat(Prefix() + "Nothing to pick a side for.");
            return;
        }

        // The winning side's captain calls it, or the console does. Not an admin on the other side -
        // that is a captain's call, not a permission - and by default not the captain's team-mates
        // either, because five people typing .switch at once is a side picked by reaction time.
        bool console = player == null || !player.IsValid;
        if (!console && player!.Team != _knifeSide)
        {
            player.PrintToChat(Prefix() + $"Not your call - {SideName(_knifeSide)} won the knife.");
            return;
        }
        if (!console && Config.Match.SideChoiceCaptainOnly && Id(player!) != WinningCaptain())
        {
            player!.PrintToChat(Prefix() + $"{Name(WinningCaptain())} picks the side for your team.");
            return;
        }

        StopSideClock();

        // Named before the swap: afterwards the winning side is the other team's.
        string chooser = SideName(_knifeSide);
        if (stay)
        {
            Announce($"{chooser} stays.");
        }
        else
        {
            // mp_swapteams moves the players; the side each drafted team is on has to follow it here,
            // because nothing else tracks who team1 is once they are not on CT any more.
            Server.ExecuteCommand("mp_swapteams");
            _team1Side = _team1Side == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
            _moveSide = (CsTeam)MatchRules.OtherSide((int)_moveSide);
            Announce($"{chooser} switches sides.");
        }

        _knifeSide = CsTeam.None;

        // A frame later either way: this is called from a chat hook, and both branches exec a cfg.
        if (Config.Match.ReadyAfterSides)
        {
            AfterTick(() =>
            {
                _matchPhase = MatchPhase.Ready;
                ExecWarmupCfg();
                StartReadyPhase();
                Prompt("Sides are set - last ready before we go live.", ".ready", Everybody);
                StartReadyHud(_matchMap);
            });
            return;
        }
        AfterTick(GoLive);
    }

    // The captain of the side that won the knife round. The draft's captains are team1's and team2's, and
    // which of those is on the winning side depends on where the sides are at the time.
    private ulong WinningCaptain() => _knifeSide == _team1Side ? _captain1 : _captain2;

    // --- live -------------------------------------------------------------------------------------

    private void GoLive()
    {
        _matchPhase = MatchPhase.Live;
        StopReadyHud();
        StopReadyPhase();
        ExecPhaseCfg(Config.Match.LiveCfg, LiveFallback, "Live");

        // live.cfg ends the warmup; the restart is what starts round 1 with everyone's money and score at
        // zero. mp_match_end_restart stays *off*: a map reload at the end of a match drops everybody back
        // through a loading screen for no reason. FinishMatch opens a warmup instead, in place.
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0;mp_restartgame 1;mp_warmup_end;"
            + "mp_match_end_restart false");

        // The engine must not reach its own end of the match, because nothing dismisses the panel it puts
        // up there - and the intermission behind that panel is what holds everybody still. With the clinch
        // off it keeps playing rounds and CheckForOurOwnClinch calls the match first. mp_maxrounds is left
        // alone: the halftime swap is measured off it, and that part works. The command itself goes out at
        // every round start, because one sent here loses to live.cfg's own line.
        if (!Config.Match.OwnMatchEnd) Server.ExecuteCommand("mp_match_can_clinch 1");

        StartDemoAfterRestart(LiveRestartSeconds);
        CaptureStatsBaseline();
        RecordMatchStart();

        Announce($"{Colorize("{green}")}LIVE! LIVE! LIVE!{Colorize("{default}")} "
            + $"{_team1Name} ({(_team1Side == CsTeam.CounterTerrorist ? "CT" : "T")}) vs {_team2Name}.");

        // Nothing is being asked of anyone now, so the overlay comes down and stays down until a pause.
        StopMatchHud();

        // Credit goes last or it is not read. With the Discord hold on, the last thing printed at
        // go-live is that hold, so the credit waits for it to finish and lands when play starts.
        if (Config.Match.OpeningPauseSeconds <= 0) AnnounceCreditLast();
    }

    // The last two lines in chat when a match starts.
    // A second behind whatever else is being said, so it is the last thing in chat whichever order the
    // engine and the plugin got their lines out in - the point of it is to be the line still on screen.
    internal void AnnounceCreditLast() => AddTimer(1.0f, AnnounceCredit);

    internal void AnnounceCredit()
    {
        // Without the [GPP] prefix the rest of the plugin's chat carries: the line says who it is from
        // already, and "[GPP] This match provided by Gamah Pug Plugin" says it twice.
        Server.PrintToChatAll($" This match provided by {Colorize("{green}")}Gamah Pug Plugin{Colorize("{default}")}");
        Server.PrintToChatAll($" {Colorize("{grey}")}https://github.com/Gamah/gamah-pug-plugin{Colorize("{default}")}");
    }

    // True when the round that just ended is the one the engine swaps sides on - the half, and every
    // overtime half. [SOURCE: MatchZy 0.9.1 Utility.cs IsTeamSwapRequired, itself from Get5, read 2026-10-19]
    private static bool IsSideSwapRound()
    {
        var rules = GameRules();
        if (rules == null) return false;

        return MatchRules.IsSideSwapRound(
            rules.TotalRoundsPlayed,
            ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24,
            ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6,
            ConVar.Find("mp_halftime")?.GetPrimitiveValue<bool>() ?? false);
    }

    // --- ending -----------------------------------------------------------------------------------

    // The engine's match end. Scores are read before anything restarts, the demo is given the GOTV flush
    // time it needs, and the server is handed back to an idle warmup.
    // The round that just ended may have decided it. Called a tick after every round end while we own the
    // match end, which is one score read per round and nothing else.
    private void CheckForOurOwnClinch()
    {
        if (_matchPhase != MatchPhase.Live) return;

        var (score1, score2) = Score();
        int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
        int roundsPlayed = GameRules()?.TotalRoundsPlayed ?? 0;
        if (!MatchRules.MatchIsWon(score1, score2, maxRounds, roundsPlayed)) return;

        Logger.LogInformation("Calling the match at {Score1}-{Score2} after {Rounds} of {Max} rounds - "
            + "before the engine's own end of it.", score1, score2, roundsPlayed, maxRounds);
        FinishMatch(engineEnded: false);
    }

    private void FinishMatch(bool engineEnded = true)
    {
        if (_matchPhase is MatchPhase.None or MatchPhase.Over) return;
        _matchPhase = MatchPhase.Over;

        var (score1, score2) = Score();
        string winner = score1 > score2 ? _team1Name : score2 > score1 ? _team2Name : "";
        Announce(winner.Length > 0
            ? $"{Colorize("{green}")}{winner}{Colorize("{default}")} win it {Math.Max(score1, score2)}-{Math.Min(score1, score2)}."
            : $"Drawn {score1}-{score2}.");

        // GOTV is still sending the last round when the match ends, and the map restart cuts it off
        // mid-flush. Get5 and MatchZy both stretch mp_match_restart_delay to cover the broadcast delay;
        // the demo is stopped inside that window. [SOURCE: MatchZy 0.9.1 Utility.cs HandleMatchEnd]
        //
        // Only when there is a broadcast to flush. With GOTV off this was stretching the intermission
        // from 12s to 15s for nothing, and the intermission is time nobody can do anything in.
        int flush = TvDelaySeconds() + 15;
        if (TvDelaySeconds() > 0)
        {
            int needed = flush + 10;
            var restartDelay = ConVar.Find("mp_match_restart_delay");
            if (restartDelay != null && restartDelay.GetPrimitiveValue<int>() < needed)
            {
                Logger.LogInformation("Extending mp_match_restart_delay to {Needed}s so GOTV can finish.", needed);
                restartDelay.SetValue(needed);
            }
        }
        StopDemo(Math.Max(0, flush - 1));
        RecordMatchEnd(score1, score2, winner);

        ReturnDiscordTeams();
        StopMatchHud();

        _matchPhase = MatchPhase.None;
        _knifeSide = CsTeam.None;
        ResetPauses();

        if (engineEnded) ReturnToHeldWarmup();
        else OpenWarmupWithTheResult(winner, score1, score2);
    }

    // Our own end of the match: the engine is still playing, so there is no panel to escape and no map to
    // reload. The warmup opens where everybody is standing and the result goes on a panel of ours for
    // win_panel_seconds, which is the engine's end-of-match screen replaced rather than fought with.
    private void OpenWarmupWithTheResult(string winner, int score1, int score2)
    {
        // Lost the race: the engine got to its own end of the match in the same breath. Opening a warmup
        // inside the intermission is the bug this whole path exists to avoid, so hand it to the reload.
        if (InPostGame())
        {
            Logger.LogWarning("The engine reached intermission before we called it - falling back to the "
                + "map reload. mp_match_can_clinch was not held off.");
            ReturnToHeldWarmup();
            return;
        }

        ReturnServerToWarmup();
        StartWinPanel(winner, score1, score2);
        LogTheWarmupWeOpened();
    }

    // Ten seconds of what the server actually looks like after a called match: the game phase, and
    // whether anybody's pawn is being held still. Both of those have been the answer tonight - the
    // intermission freeze reads as a pawn nobody can move, and the two movetype fields disagree when
    // something has frozen a player and not let go.
    private void LogTheWarmupWeOpened()
    {
        int ticks = 0;
        CounterStrikeSharp.API.Modules.Timers.Timer? timer = null;
        timer = AddTimer(1.0f, () =>
        {
            if (++ticks > 10 || MatchRunning)
            {
                timer?.Kill();
                return;
            }
            try
            {
                var rules = GameRules();
                string movement = string.Join(", ", HumanPlayers().Where(player => !player.IsBot)
                    .Select(player =>
                    {
                        var pawn = player.PlayerPawn.Value;
                        return pawn == null || !pawn.IsValid
                            ? $"{player.PlayerName}: no pawn"
                            : $"{player.PlayerName}: {pawn.MoveType}/{pawn.ActualMoveType} hp {pawn.Health}";
                    }));
                Logger.LogInformation("Warmup +{Ticks}s: phase {Phase}, warmup {Warmup}, freeze {Freeze}, "
                    + "clinch {Clinch}, freezetime {FreezeTime}. {Movement}",
                    ticks, rules?.GamePhase, rules?.WarmupPeriod, rules?.FreezePeriod,
                    ConVar.Find("mp_match_can_clinch")?.GetPrimitiveValue<bool>(),
                    ConVar.Find("mp_freezetime")?.GetPrimitiveValue<int>(), movement);
            }
            catch (Exception error)
            {
                Logger.LogWarning("Could not read the warmup state: {Error}", error.Message);
            }
        }, TimerFlags.REPEAT);
    }

    // The hand-back !pug stop has always used, and the only ordering of it that works: the restart
    // first, the warmup cfg after. The other way round - warmup opened, restart behind it - opens the
    // warmup inside the round-end freeze, and a warmup whose clock is paused never counts that freeze
    // down. The room was left able to take about a step before being snapped back to where it stood.
    private void ReturnServerToWarmup()
    {
        Server.ExecuteCommand("mp_unpause_match;mp_match_end_restart false;mp_restartgame 1");
        ExecWarmupCfg();
    }

    // Back to a held warmup after a match. The map is reloaded to get there, because nothing else does.
    //
    // A 13-10 win read as "match cancelled" and 0-0 to everyone, on a panel nothing cleared
    // (2026-10-23, nuke then dust2, five players). The warmup used to go out one tick after the engine
    // said "Going to intermission...", which aborts the end-of-match state rather than following it -
    // that is where the cancelled panel and the 0-0 came from. Three ways out were tried on real
    // clients that night and only the last one works:
    //
    //   mp_restartgame                 twice over rcon on the stuck server, no effect
    //   m_gamePhase 5 -> 2 + restart   server-side phase changed, client stayed on the panel - and it
    //                                  fooled our own InPostGame() check, so the fallback never fired
    //   a map load                     clears it every time
    //
    // So the panel is client-side and latched by the end-of-match state, not readable from the phase.
    // Nothing is sent during the intermission, the final score stays up for post_match_seconds, and
    // then the map is reloaded. OnMapStart holds the warmup from there, so this execs nothing itself.
    // The cost is a loading screen between pugs; in-place is not on offer.
    private void ReturnToHeldWarmup()
    {
        AddTimer(Math.Max(1, Config.Match.PostMatchSeconds), () =>
        {
            // A new pug got going in the meantime - a map vote of its own, or a draft somebody
            // reconnected into. Reloading the map under that is worse than the panel.
            if (MatchRunning || _phase != PugPhase.Idle)
            {
                Logger.LogInformation("Post-match map reload skipped - the server is busy again.");
                return;
            }
            Logger.LogInformation("Reloading the map to clear the end-of-match panel.");
            ReloadThisMap();
        });
    }

    // An admin ending a match that is still going: .endmatch, !pug stop. The engine is told to stop
    // scoring and the server is put back in a held warmup, because nothing else will.
    internal void AbandonMatch(string why)
    {
        bool wasRunning = MatchRunning;
        if (wasRunning && _matchPhase == MatchPhase.Live)
        {
            var (score1, score2) = Score();
            RecordMatchEnd(score1, score2, "");
            StopDemo(0);
        }

        _matchPhase = MatchPhase.None;
        _knifeSide = CsTeam.None;
        ResetPauses();
        StopReadyPhase();
        StopReadyHud();
        StopMatchHud();
        ReturnDiscordTeams();

        ReturnServerToWarmup();
        if (wasRunning) Announce($"Match ended. {why}");
    }

    // --- reading the match ------------------------------------------------------------------------

    internal static CCSGameRules? GameRules()
    {
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        return proxy?.GameRules;
    }

    // Scores come off the team entities, which hold them per side, so they have to be read through
    // whichever side each drafted team is on at the time.
    private (int team1, int team2) Score()
    {
        int ct = 0;
        int t = 0;
        foreach (var team in Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager"))
        {
            if (team.Teamname == "CT") ct = team.Score;
            else if (team.Teamname == "TERRORIST") t = team.Score;
        }
        return _team1Side == CsTeam.CounterTerrorist ? (ct, t) : (t, ct);
    }

    // GOTV's broadcast delay, which is how long after a round the viewers are still being sent it.
    private static int TvDelaySeconds()
    {
        if (!(ConVar.Find("tv_enable")?.GetPrimitiveValue<bool>() ?? false)) return 0;
        int delay = ConVar.Find("tv_delay")?.GetPrimitiveValue<int>() ?? 0;
        if (!(ConVar.Find("tv_enable1")?.GetPrimitiveValue<bool>() ?? false)) return delay;
        return Math.Max(delay, ConVar.Find("tv_delay1")?.GetPrimitiveValue<int>() ?? 0);
    }

    // 4 is halftime, 5 is the post-game panel. A pause is refused in both: the engine is already holding
    // the game and unpausing afterwards does not come back.
    // [SOURCE: MatchZy 0.9.1 Utility.cs IsHalfTimePhase/IsPostGamePhase, read 2026-10-19]
    private static bool InHalftime() => GameRules()?.GamePhase == 4;

    private static bool InPostGame() => GameRules()?.GamePhase == 5;

    // The drafted team on a side, by name. Falls back to the side's own name for anyone who walked in.
    // The side itself, not whoever is standing on it. SideName answers with a team name, which on the
    // side-choice panel read as "they are on team_Blackwolf right now" where it meant CT.
    private static string Side(CsTeam side) => side switch
    {
        CsTeam.CounterTerrorist => "CT",
        CsTeam.Terrorist => "T",
        _ => "nobody",
    };

    private string SideName(CsTeam side)
    {
        if (side == CsTeam.None) return "nobody";
        return side == _team1Side ? _team1Name : _team2Name;
    }

    private CsTeam SideOf(int teamNumber) => teamNumber == 1 ? _team1Side
        : _team1Side == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;

    internal string MatchStatus()
    {
        if (_matchPhase == MatchPhase.None) return "No match running.";
        var (score1, score2) = Score();
        string where = _matchPhase switch
        {
            MatchPhase.Ready => $"ready phase, {ReadyCount()}/{ReadyNeeded()} ready",
            MatchPhase.Knife => "knife round",
            MatchPhase.SideChoice => $"{SideName(_knifeSide)} picking a side",
            MatchPhase.Live when PauseHasLanded => $"live, paused ({score1}-{score2})",
            MatchPhase.Live when _paused => $"live, pause called for the end of the round ({score1}-{score2})",
            MatchPhase.Live => $"live {score1}-{score2}",
            _ => "finishing up",
        };
        return $"{_team1Name} vs {_team2Name} on {_matchMap}: {where}.";
    }

    // Returns false and says why when a vote or a draft should not start. force ends whatever is running.
    private bool MatchIsClear(bool force, out bool waitForEnd)
    {
        waitForEnd = false;
        bool enginePlaying = !InWarmup();

        if (!MatchRunning && !enginePlaying) return true;

        if (!force)
        {
            Announce(MatchRunning
                ? $"A match is running ({MatchStatus()}) - !pug stop it, or use force."
                : "A match looks to be in progress - !pug stop it, or use force to start anyway.");
            return false;
        }

        Announce("Ending the match in progress first.");
        AbandonMatch("An admin started a new pug.");
        waitForEnd = true;
        return true;
    }

    // True when the server looks idle enough to run a draft: warmup, or a map that has not started yet.
    private static bool InWarmup()
    {
        var rules = GameRules();
        if (rules == null) return true; // map just loaded, nothing running
        return rules.WarmupPeriod || rules.TotalRoundsPlayed == 0;
    }

    // Strictly the engine's warmup flag: InWarmup() also counts a map with no rounds played, which
    // includes the knife round, and nothing that moves players may touch that.
    private static bool InWarmupPeriod() => GameRules()?.WarmupPeriod ?? false;
}
