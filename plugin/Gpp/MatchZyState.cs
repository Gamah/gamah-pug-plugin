using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;

namespace Gpp;

// MatchZy exposes no API to other plugins - no plugin capability, no shared state. What it does expose is
// its console commands, so we follow the ones that load or end a match and keep our own view of whether a
// match is up. The same hooks let us block MatchZy commands that would wreck a running vote or draft.
public partial class Gpp
{
    private static readonly string[] LoadMatchCommands =
        ["matchzy_loadmatch", "matchzy_loadmatch_url", "get5_loadmatch", "get5_loadmatch_url"];

    private static readonly string[] EndMatchCommands =
        ["get5_endmatch", "css_endmatch", "css_forceend", "css_restart", "css_rr"];

    // MatchZy commands that assume nothing else is moving players around.
    private static readonly string[] GuardedCommands =
    [
        "css_ready", "css_unready", "css_notready", "css_start", "css_force", "css_forcestart",
        "css_map", "css_rmap",
        // MatchZy's own team-switch commands go nowhere near jointeam, so they would walk straight
        // through force_spectate.
        "css_t", "css_ct",
    ];

    private bool _matchLoaded;

    private void HookMatchZy()
    {
        foreach (var command in LoadMatchCommands)
        {
            AddCommandListener(command, OnMatchLoaded, HookMode.Post);
        }
        foreach (var command in EndMatchCommands)
        {
            AddCommandListener(command, OnMatchEnded, HookMode.Post);
        }
        foreach (var command in GuardedCommands)
        {
            AddCommandListener(command, OnGuardedCommand, HookMode.Pre);
        }

        // Nothing here hooks round_announce_match_start: that fires whenever a match starts, and the
        // engine starts one of its own every time warmup ends - which the pug phases do themselves, and
        // every map load does. Treating it as "MatchZy has a match loaded" left the flag stuck on and
        // !pug start refusing for the rest of the map. A match nobody loaded is caught by InWarmup().
        RegisterEventHandler<EventCsWinPanelMatch>((_, _) =>
        {
            _matchLoaded = false;
            StopReadyGate();
            StopReadyHud();
            // The match is over and the pug is holding nobody. Anyone it parked in spectator gets the
            // team menu back here too, in case the draft ended without reaching ReleaseHeldSpectators.
            AfterTick(ReleaseHeldSpectators);
            ReturnDiscordTeams();
            // Back to a quiet server. A match that ends leaves MatchZy in match mode with its ready phase
            // available again, which is what arms the repeating "N players are not ready" chat timer on an
            // idle server; sleep mode is what GPP keeps it in between pugs.
            PutMatchZyBackToSleep();
            return HookResult.Continue;
        });

        // Not used to decide whether a match is loaded (see above) - only to take the ".ready" overlay down
        // once the round it was asking for has actually started.
        RegisterEventHandler<EventRoundAnnounceMatchStart>((_, _) =>
        {
            StopReadyHud();
            return HookResult.Continue;
        });
    }

    private HookResult OnMatchLoaded(CCSPlayerController? player, CommandInfo command)
    {
        // Post hook: this fires whether or not MatchZy accepted the config, so a rejected file leaves us
        // thinking a match is up. !pug status shows it and !pug clear (or force) clears it.
        _matchLoaded = true;
        return HookResult.Continue;
    }

    private HookResult OnMatchEnded(CCSPlayerController? player, CommandInfo command)
    {
        _matchLoaded = false;
        StopReadyGate();
        return HookResult.Continue;
    }

    private HookResult OnGuardedCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (_phase == PugPhase.Idle) return HookResult.Continue;

        string what = _phase == PugPhase.MapVote ? "map vote" : "draft";
        if (player != null && player.IsValid)
        {
            player.PrintToChat(Prefix() + $"Not now - a {what} is running. An admin can !pug cancel.");
        }
        return HookResult.Stop;
    }

    // True when the server looks idle enough to run a draft: warmup, or a map that has not started yet.
    private bool InWarmup()
    {
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        var rules = proxy?.GameRules;
        if (rules == null) return true; // map just loaded, nothing running
        return rules.WarmupPeriod || rules.TotalRoundsPlayed == 0;
    }

    // Returns false and explains why when a vote or draft should not start. force clears a loaded match first.
    private bool MatchZyIsClear(bool force, out bool waitForEnd)
    {
        waitForEnd = false;

        if (!_matchLoaded && InWarmup()) return true;

        if (!force)
        {
            Announce((_matchLoaded
                ? "A MatchZy match is loaded - .forceend it first, or !pug clear, or use force to end it and start anyway."
                : "A match looks to be in progress - .forceend it first, or use force to start anyway."));
            return false;
        }

        if (_matchLoaded || !InWarmup())
        {
            Announce("Ending the loaded MatchZy match first.");
            Server.ExecuteCommand("get5_endmatch");
            _matchLoaded = false;
            waitForEnd = true;
        }
        return true;
    }
}
