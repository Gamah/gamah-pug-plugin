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
        ResetVote();
        ResetKnife();
        ResetDraft();
        StopHud();
        StopReadyHud();
        StopReadyGate();
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

        // Test bots, if a harness run left any.
        KeepTestBotsTopped(0);

        // MatchZy next. get5_endmatch is sent unconditionally - _matchLoaded only tracks what this
        // plugin set up, and the whole point of !pug stop is to clear a mess it may not have made.
        Server.ExecuteCommand("get5_endmatch");
        _matchLoaded = false;

        // A series end restores whatever cvars MatchZy changed for it, which can put an old whitelist
        // back; re-assert after, not before.
        NeverKickAnyone();
        KeepMatchZyAsleep();
        AllowUnlimitedZeus();

        // And land in a held warmup rather than a live round ticking down with nobody in it.
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0;mp_warmup_start;"
            + "mp_warmuptime 9999;mp_warmup_pausetimer 1");

        Announce($"Everything stopped - back to warmup. {why}");
    }

    private CounterStrikeSharp.API.Modules.Timers.Timer? _moneyTimer;

    private void StartWarmupMoney()
    {
        if (Config.WarmupMoney <= 0) return;
        _moneyTimer?.Kill();
        _moneyTimer = AddTimer(1.0f, WarmupMoneyTick, TimerFlags.REPEAT);
    }

    // Written every second rather than once a round. mp_maxmoney caps what the engine will hand out and
    // mp_startmoney is only the round-start grant, so neither survives a player actually spending it -
    // topping the account back up is the only thing that reads as "money is not a thing in warmup".
    private void WarmupMoneyTick()
    {
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
