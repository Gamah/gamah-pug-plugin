using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
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
            return HookResult.Continue;
        }, HookMode.Post);

        // The match is over. This fires for any match the engine ends, including one nobody here started,
        // which is why everything below it is safe to run from an idle server.
        RegisterEventHandler<EventCsWinPanelMatch>((_, _) =>
        {
            AfterTick(FinishMatch);
            return HookResult.Continue;
        });

        // The demo cannot start until after the restart that going live performs.
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            StartPendingDemo();
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
    private const string WarmupFallback =
        "mp_autoteambalance 0;mp_limitteams 0;mp_maxmoney 16000;mp_startmoney 16000;mp_free_armor 0;"
        + "mp_ignore_round_win_conditions 0;mp_warmup_start;mp_warmuptime 9999;mp_warmup_pausetimer 1";

    private const string KnifeFallback =
        "mp_ct_default_secondary \"\";mp_t_default_secondary \"\";mp_free_armor 1;mp_freezetime 10;"
        + "mp_give_player_c4 0;mp_maxmoney 0;mp_respawn_immunitytime 0;mp_respawn_on_death_ct 0;"
        + "mp_respawn_on_death_t 0;mp_roundtime 1.92;mp_roundtime_defuse 1.92;mp_roundtime_hostage 1.92;"
        + "mp_round_restart_delay 3;mp_team_intro_time 0;mp_warmup_pausetimer 0";

    private const string LiveFallback =
        "mp_maxrounds 24;mp_halftime 1;mp_overtime_enable 1;mp_overtime_maxrounds 6;mp_freezetime 15;"
        + "mp_maxmoney 16000;mp_startmoney 800;mp_roundtime 1.92;mp_roundtime_defuse 1.92;"
        + "mp_free_armor 0;mp_give_player_c4 1;mp_autoteambalance 0;mp_limitteams 0;"
        + "mp_match_can_clinch 1;mp_warmup_pausetimer 0";

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
        _team1Name = TeamName(_captain1);
        _team2Name = TeamName(_captain2);
        ResetPauses();
        ClearStats();

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

    // Everybody readied. Either the knife round for sides, or straight to live.
    private void StartKnifeOrLive()
    {
        if (!Config.KnifeRound)
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

        _knifeSide = winner;
        _matchPhase = MatchPhase.SideChoice;

        // Back to a held warmup while they decide, so the next round does not start without them.
        ExecWarmupCfg();

        string name = SideName(winner);
        Announce($"{Colorize("{green}")}{name}{Colorize("{default}")} won the knife ({why}).");
        Prompt("Pick a side.", ".stay or .switch", $"{name} only");
        StartMatchHud();
    }

    // The side that won the knife round, while the choice is open.
    private CsTeam _knifeSide = CsTeam.None;

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

    // .stay / .switch, from a player on the winning side or from an admin.
    internal void ChooseSide(CCSPlayerController? player, bool stay)
    {
        if (_matchPhase != MatchPhase.SideChoice)
        {
            if (player != null && player.IsValid) player.PrintToChat(Prefix() + "Nothing to pick a side for.");
            return;
        }

        // The winning side calls it, or the console does. An admin playing on the other side does not get
        // to pick for them - that is a captain's call, not a permission.
        bool console = player == null || !player.IsValid;
        if (!console && player!.Team != _knifeSide)
        {
            player.PrintToChat(Prefix() + $"Not your call - {SideName(_knifeSide)} won the knife.");
            return;
        }

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
            Announce($"{chooser} switches sides.");
        }

        _knifeSide = CsTeam.None;
        // A frame later: this is called from a chat hook, and going live execs a cfg and restarts the game.
        AfterTick(GoLive);
    }

    // --- live -------------------------------------------------------------------------------------

    private void GoLive()
    {
        _matchPhase = MatchPhase.Live;
        StopReadyHud();
        StopReadyPhase();
        ExecPhaseCfg(Config.Match.LiveCfg, LiveFallback, "Live");

        // live.cfg ends the warmup; the restart is what starts round 1 with everyone's money and score at
        // zero. mp_match_end_restart reloads the map when the match is over, which is what puts the server
        // back to a clean warmup without anybody having to.
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0;mp_restartgame 1;mp_warmup_end;"
            + "mp_match_end_restart true");

        StartDemoAfterRestart(LiveRestartSeconds);
        RecordMatchStart();

        Announce($"{Colorize("{green}")}LIVE! LIVE! LIVE!{Colorize("{default}")} "
            + $"{_team1Name} ({(_team1Side == CsTeam.CounterTerrorist ? "CT" : "T")}) vs {_team2Name}.");

        // Nothing is being asked of anyone now, so the overlay comes down and stays down until a pause.
        StopMatchHud();
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
    private void FinishMatch()
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
        int flush = TvDelaySeconds() + 15;
        int needed = TvDelaySeconds() > 0 ? flush + 10 : flush;
        var restartDelay = ConVar.Find("mp_match_restart_delay");
        if (restartDelay != null && restartDelay.GetPrimitiveValue<int>() < needed)
        {
            Logger.LogInformation("Extending mp_match_restart_delay to {Needed}s so GOTV can finish.", needed);
            restartDelay.SetValue(needed);
        }
        StopDemo(Math.Max(0, flush - 1));
        RecordMatchEnd(score1, score2, winner);

        ReturnDiscordTeams();
        StopMatchHud();

        // mp_match_end_restart reloads the map, which is where the idle warmup comes from; OnMapStart
        // execs warmup.cfg again. Until then nothing is live and nothing is being asked of anyone.
        _matchPhase = MatchPhase.None;
        _knifeSide = CsTeam.None;
        ResetPauses();
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

        Server.ExecuteCommand("mp_unpause_match;mp_match_end_restart false;mp_restartgame 1");
        ExecWarmupCfg();
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
            MatchPhase.Live => _paused ? $"live, paused ({score1}-{score2})" : $"live {score1}-{score2}",
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
