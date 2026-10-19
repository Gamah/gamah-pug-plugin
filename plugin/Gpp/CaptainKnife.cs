using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// Captains knife 1v1 on the voted map for first pick. First blood decides it; if nobody connects a knife
// in time it goes on health, and only a dead-even duel is a coin flip. The match's own knife round still
// happens later with the full teams (Match.cs) - that one is for sides, this one is for the first pick.
public partial class Gpp
{
    // Both captains spawn with a knife and nothing else. Round wins stay ignored so the round does not end
    // (and restart) the moment one of them dies - we read the death ourselves.
    private const string DuelCvars =
        "mp_ignore_round_win_conditions 1;mp_ct_default_primary \"\";mp_t_default_primary \"\";" +
        "mp_ct_default_secondary \"\";mp_t_default_secondary \"\";mp_free_armor 1;mp_maxmoney 0;" +
        "mp_startmoney 0;mp_give_player_c4 0;mp_freezetime 3;mp_respawn_on_death_ct 0;" +
        "mp_respawn_on_death_t 0;mp_team_intro_time 0;mp_round_restart_delay 3;mp_warmup_end;mp_restartgame 1";

    // Back to competitive defaults, so an abandoned pug does not leave a knife server behind. The warmup
    // and live cfgs set all of these again when the match starts.
    private const string DuelCvarsReset =
        "mp_ct_default_secondary weapon_hkp2000;mp_t_default_secondary weapon_glock;mp_free_armor 0;" +
        "mp_maxmoney 16000;mp_startmoney 16000;mp_give_player_c4 1;mp_freezetime 10";

    private ulong _knifeWinner;
    private int _knifeSecondsLeft;

    // Moving a live player to another team kills their pawn, and mp_restartgame kills everyone again, so
    // the duel's own setup fires player_death for the captains before either of them has a knife in hand.
    // Deaths only decide the duel once both captains are standing up on the new map.
    private bool _duelArmed;
    private int _armingTicks;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _knifeTimer;

    private void HookCaptainKnife()
    {
        RegisterEventHandler<EventPlayerDeath>((@event, _) =>
        {
            if (_phase != PugPhase.CaptainKnife || !_duelArmed) return HookResult.Continue;

            var victimPlayer = @event.Userid;
            ulong victim = victimPlayer != null && victimPlayer.IsValid ? Id(victimPlayer) : 0;
            if (victim == _captain1) EndDuel(_captain2, $"{Name(_captain1)} got knifed");
            else if (victim == _captain2) EndDuel(_captain1, $"{Name(_captain2)} got knifed");
            return HookResult.Continue;
        });
    }

    // Returns false when there is nothing to knife for - the caller then drafts straight away.
    private bool StartCaptainKnife()
    {
        if (!Config.CaptainKnife) return false;

        var first = PlayerBySteamId(_captain1);
        var second = PlayerBySteamId(_captain2);
        if (first == null || second == null) return false;

        _phase = PugPhase.CaptainKnife;
        _knifeWinner = 0;
        _duelArmed = false;
        _armingTicks = 0;
        _knifeSecondsLeft = Math.Max(10, Config.CaptainKnifeSeconds);

        // Everyone else watches: a third player in the duel is a third player who can win it. Everyone
        // human, that is - a bot in spectator is outside bot_quota, so the engine kicks it and adds a
        // replacement, and a test run turns into a churn of connecting bots. StandDownTestBots below is
        // how bots are taken out of the duel: it kills them, which the duel's own cvars make stick.
        foreach (var player in HumanPlayers())
        {
            if (player.IsBot || Id(player) == _captain1 || Id(player) == _captain2) continue;
            HoldInSpectator(player);
        }
        first.ChangeTeam(CsTeam.CounterTerrorist);
        second.ChangeTeam(CsTeam.Terrorist);

        Server.ExecuteCommand(DuelCvars);
        // DuelCvars restarts the round, so the bots respawn into it; take them out again once it has.
        AddTimer(5.0f, () =>
        {
            if (_phase == PugPhase.CaptainKnife) StandDownTestBots(_captain1, _captain2);
        });
        StartSpectateGuard();
        StartHud();

        Prompt($"{Colorize("{green}")}{Name(_captain1)}{Colorize("{default}")} vs "
            + $"{Colorize("{green}")}{Name(_captain2)}{Colorize("{default}")} - knife for first pick.",
            "knife only", "both captains");

        _knifeTimer = AddTimer(1.0f, KnifeTick, TimerFlags.REPEAT);
        return true;
    }

    private void KnifeTick()
    {
        if (_phase != PugPhase.CaptainKnife)
        {
            _knifeTimer?.Kill();
            _knifeTimer = null;
            return;
        }

        // The clock does not start until both captains are alive on the new map - a slow spawn should
        // not eat the duel, and a death before that is the restart's, not a knife.
        if (!_duelArmed)
        {
            var one = PlayerBySteamId(_captain1);
            var two = PlayerBySteamId(_captain2);
            if (one != null && two != null && one.PawnIsAlive && two.PawnIsAlive)
            {
                _duelArmed = true;
                Announce("Knives out.");
                return;
            }
            if (++_armingTicks < 20) return;
            AfterTick(() => EndDuel(0, "the captains never both spawned"));
            return;
        }

        _knifeSecondsLeft--;

        // A captain who leaves mid-duel forfeits; their replacement is sorted out by the draft.
        var first = PlayerBySteamId(_captain1);
        var second = PlayerBySteamId(_captain2);
        if (first == null && second == null)
        {
            AfterTick(() => EndDuel(0, "both captains left"));
            return;
        }
        if (first == null)
        {
            AfterTick(() => EndDuel(_captain2, $"{Name(_captain1)} left"));
            return;
        }
        if (second == null)
        {
            AfterTick(() => EndDuel(_captain1, $"{Name(_captain2)} left"));
            return;
        }
        if (_knifeSecondsLeft > 0) return;

        int firstHealth = first.PlayerPawn.Value?.Health ?? 0;
        int secondHealth = second.PlayerPawn.Value?.Health ?? 0;
        if (firstHealth > secondHealth) AfterTick(() => EndDuel(_captain1, $"time up, {firstHealth}hp vs {secondHealth}hp"));
        else if (secondHealth > firstHealth) AfterTick(() => EndDuel(_captain2, $"time up, {secondHealth}hp vs {firstHealth}hp"));
        else AfterTick(() => EndDuel(0, "time up, dead even"));
    }

    private void EndDuel(ulong winner, string why)
    {
        if (_phase != PugPhase.CaptainKnife) return;

        _knifeTimer?.Kill();
        _knifeTimer = null;
        StopHud();
        StopSpectateGuard();
        Server.ExecuteCommand(DuelCvarsReset);

        if (winner == 0)
        {
            winner = _rng.Next(2) == 0 ? _captain1 : _captain2;
            why += ", coin flip";
        }
        _knifeWinner = winner;
        _phase = PugPhase.Idle;

        Logger.LogInformation("Captain knife won by {Winner} ({Why})", Name(winner), why);
        Announce($"{Colorize("{green}")}{Name(winner)}{Colorize("{default}")} picks first ({why}).");

        // A death in the duel ends the round, which leaves the loser dead and watching somebody else's
        // camera for the whole draft - and a dead captain is a captain with no pick list in front of
        // them, because the boards hang off the pawn you are looking through. So the round is restarted
        // here rather than left as the duel finished it: both captains come back in spawn, alive, with
        // their own camera, and the draft starts from a clean round.
        //
        // Spawns, weapons and freezetime are still the duel's until that restart settles, which is what
        // the wait is for.
        Server.ExecuteCommand("mp_restartgame 1");
        AddTimer(2.5f, () => StartDraft(force: true, skipMatchCheck: true));
    }

    private void ResetKnife()
    {
        _knifeTimer?.Kill();
        _knifeTimer = null;
        _duelArmed = false;
        _armingTicks = 0;
        _knifeSecondsLeft = 0;
        _knifeWinner = 0;
        Server.ExecuteCommand(DuelCvarsReset);
    }
}
