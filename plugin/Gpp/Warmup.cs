using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// Two things that put the server back the way an idle pug server should be.
//
// !pug stop, which is the one people reach for when something has gone sideways: it does not care what
// phase anything is in, it just takes everything down and leaves a held warmup behind. !pug cancel is
// still the polite version that only abandons a running vote or draft.
//
// And warmup money, which the cvars alone cannot do: mp_maxmoney and mp_startmoney set what you are
// given, not what you keep, so the first rifle and a couple of nades still empties a wallet. The only
// way to hold it is to keep writing it back.
public partial class Gpp
{
    // Everything off, everything back to warmup. Safe to run from any phase, including none.
    internal void StopEverything(string why)
    {
        // Phase state first, so nothing below is fighting a timer that is still running.
        CloseCaptainOptIn(draw: false);
        ResetVote();
        ResetKnife();
        ResetDraft();
        StopHud();
        StopReadyHud();
        StopReadyPhase();
        StopSpectateGuard();
        ClearFunEffects();
        _waitTimer?.Kill();
        _waitTimer = null;
        _votedMap = null;
        _draftPendingMapChange = false;
        _teamSizeOverride = 0;
        _knifeWinner = 0;
        _phase = PugPhase.Idle;

        // Anyone the pug parked in spectator gets the team menu back.
        ReleaseHeldSpectators();

        // The harness, if a test run left one going.
        StopTestRun();

        // Any match, ours or one nobody here started: !pug stop is the command people reach for when the
        // server is in a state nothing else will clear, so it ends the match and execs the warmup cfg
        // rather than trusting the plugin's own idea of what is running.
        AbandonMatch(why);

        // And land in a held warmup rather than a live round ticking down with nobody in it. The cfg does
        // this too; saying it here as well means a missing cfg still leaves an idle server idle.
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0;mp_warmup_start;"
            + "mp_warmuptime 9999;mp_warmup_pausetimer 1");

        Announce($"Everything stopped - back to warmup. {why}");
    }

    private CounterStrikeSharp.API.Modules.Timers.Timer? _warmupTimer;

    // Null until the first tick, so the buy cap is written once on load whichever phase the server is in.
    private bool? _wasInWarmup;

    private void StartWarmupWatch()
    {
        _warmupTimer?.Kill();
        _warmupTimer = AddTimer(1.0f, WarmupTick, TimerFlags.REPEAT);
    }

    // Warmup hands out unlimited money, so a per-round buy cap reads as a bug - it is what stops a fifth
    // zeus, and dying does not clear it because a held warmup is one long round. mp_weapons_allow_zeus is
    // only a 0/1 toggle; the cap is mp_weapons_allow_typecount, which defaults to 5.
    //
    // Followed on the warmup edge rather than asserted at load or on map start, because the server has to
    // get the cap back for a live match and nothing else puts it back: the warmup cfg does not set it at
    // all (the line was taken out so this is the only owner) and the live cfg says nothing about it.
    private void SyncBuyCap()
    {
        bool warmup = InWarmupPeriod();
        if (_wasInWarmup == warmup) return;

        _wasInWarmup = warmup;
        Server.ExecuteCommand(warmup
            ? "mp_weapons_allow_zeus 1;mp_weapons_allow_typecount -1"
            : "mp_weapons_allow_zeus 1;mp_weapons_allow_typecount 5");
    }

    // Written every second rather than once a round. mp_maxmoney caps what the engine will hand out and
    // mp_startmoney is only the round-start grant, so neither survives a player actually spending it -
    // topping the account back up is the only thing that reads as "money is not a thing in warmup".
    private void WarmupTick()
    {
        SyncBuyCap();
        TrackIdleTime();

        if (Config.WarmupMoney <= 0 || !InWarmupPeriod()) return;

        foreach (var player in HumanPlayers())
        {
            if (player.IsBot) continue;
            var money = player.InGameMoneyServices;
            if (money == null || money.Account >= Config.WarmupMoney) continue;

            money.Account = Config.WarmupMoney;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
        }
    }
}
