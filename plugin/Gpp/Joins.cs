using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// A draft that starts before everyone has loaded in has to cope with people turning up mid-pick: they go
// into the pool rather than onto a team of their choosing, and force_spectate has to hold for them too.
// Moving everyone to spectator once at the start only covers whoever was there at that second.
public partial class Gpp
{
    private void HookJoins()
    {
        AddCommandListener("jointeam", OnJoinTeam, HookMode.Pre);
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
    }

    // From the knife until the teams are handed over, the plugin owns team assignment. The vote is not
    // part of that: people carry on playing while they vote.
    private HookResult OnJoinTeam(CCSPlayerController? player, CommandInfo command)
    {
        if (_phase is PugPhase.Idle or PugPhase.MapVote)
        {
            return JoinLiveMatchNow(player, command);
        }
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        player.PrintToChat(Prefix() + (_phase == PugPhase.Draft
            ? "A draft is running - the captains decide teams."
            : "Hold on - the pug decides teams."));
        return HookResult.Stop;
    }

    private void OnClientPutInServer(int slot)
    {
        // A fresh connection is not fully set up this frame: the controller has no SteamID yet.
        AddTimer(1.0f, () =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;
            if (!player.IsBot && player.SteamID == 0) return;

            // Before anything else, and whatever phase it is: the number is handed out in connection
            // order, so it cannot be skipped on the phases that return early below.
            NumberPlayer(Id(player));

            if (_phase is PugPhase.Idle or PugPhase.MapVote)
            {
                HoldMidMatchArrival(player);
                return;
            }

            _names[Id(player)] = player.PlayerName;
            if (_phase != PugPhase.Draft)
            {
                if (_phase == PugPhase.CaptainKnife && Id(player) == _captain1)
                {
                    player.ChangeTeam(CsTeam.CounterTerrorist);
                }
                else if (_phase == PugPhase.CaptainKnife && Id(player) == _captain2)
                {
                    player.ChangeTeam(CsTeam.Terrorist);
                }
                else if (Config.ForceSpectate && !player.IsBot)
                {
                    HoldInSpectator(player);
                }
                return;
            }

            // Already drafted: they left mid-draft and came back, so put them where they belong.
            if (_team1.Contains(Id(player)))
            {
                player.ChangeTeam(CsTeam.CounterTerrorist);
                return;
            }
            if (_team2.Contains(Id(player)))
            {
                player.ChangeTeam(CsTeam.Terrorist);
                return;
            }

            if (Config.ForceSpectate && !player.IsBot) HoldInSpectator(player);
            AddToPool(Id(player));
        });
    }

    // A spectator picking a side in a live match. The engine queues that pick to the round boundary, so
    // the side does not take for up to two minutes - and the mid-match nudge below reads the player as a
    // spectator in the meantime and parks them again, which cancels the pick outright. So the pick is
    // honoured here instead: on a team and alive this round, the same way the draft seats people.
    //
    // A player already on a side asking for the other one is taken over too: left to the engine, a
    // player the plugin had placed could not switch, and the engine logged no ChangeTeam() CTMDBG line
    // for the attempt. Why it refuses is not known. ChangeTeam follows the gamemode rules, so an alive
    // player dies in the switch, exactly as a jointeam would have them. [SOURCE: CS2 1.41.9.8,
    // 2026-11-22, one report from a live pug]
    //
    // Everything else - a team change in warmup, the vote, a bot, picking the side you are on - is the
    // engine's to handle.
    private HookResult JoinLiveMatchNow(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return HookResult.Continue;
        if (!MatchRunning || !Config.SpectateMidMatchJoins) return HookResult.Continue;

        // jointeam <team>: 2 is T, 3 is CT. Anything else - spectator, the unassigned slot, a malformed
        // argument - is not a side, so it is left alone.
        var side = command.GetArg(1) switch
        {
            "2" => CsTeam.Terrorist,
            "3" => CsTeam.CounterTerrorist,
            _ => CsTeam.None,
        };
        if (side == CsTeam.None || player.Team == side) return HookResult.Continue;
        var from = player.Team;

        // The team is taken here rather than left to the engine, which would hold the pick as pending and
        // let the nudge above cancel it. No respawn: they join the next round like anyone else, and are a
        // dead teammate until it starts - which is the whole reason they are told so.
        _midMatchNudged.Remove(Id(player));
        _heldInSpectator.Remove(Id(player));
        player.ChangeTeam(side);
        bool thisRound = SpawnNowOrNextRound(player);
        if (!thisRound)
        {
            HoldSpawnNotice(player);
            Announce($"{player.PlayerName} YOU WILL SPAWN NEXT ROUND");
        }
        Logger.LogInformation("{Name} picked {Side} mid-match (from {From}) - on the team now, spawning {When}.",
            player.PlayerName, side == CsTeam.CounterTerrorist ? "CT" : "T", from,
            thisRound ? "into this round (freeze time)" : "next round");
        return HookResult.Stop;
    }

    // The alert box is transient - one print fades - so it is redrawn at 1Hz for as long as the notice
    // should stand, and only ever to the player it is addressed to. Everybody else reads it in chat.
    private void HoldSpawnNotice(CCSPlayerController player)
    {
        ulong id = Id(player);
        var ticksLeft = 6;
        CounterStrikeSharp.API.Modules.Timers.Timer? notice = null;
        notice = AddTimer(1.0f, () =>
        {
            var them = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && Id(p) == id);
            var pawn = them?.PlayerPawn.Value;
            var alive = pawn != null && pawn.IsValid && pawn.Health > 0;

            // Retired, gone, spawned, or shown for long enough: the notice has nothing left to say, and a
            // repeating timer nobody kills outlives the match it belongs to.
            if (Retired || them == null || alive || ticksLeft-- <= 0)
            {
                notice?.Kill();
                return;
            }

            them.PrintToCenterAlert(Alert(them.PlayerName, "YOU WILL SPAWN NEXT ROUND"));
        }, TimerFlags.REPEAT);
    }

    // Somebody connecting into a live match. The pug's phase is Idle for the whole match - the phases are
    // the pre-match machinery - so none of the sweeps above are running and the engine assigns the team
    // itself: sit in the team-select menu past mp_force_pick_time and it picks a side for you. Which is
    // how somebody who had been on CT all night came back from a disconnect onto T without touching a
    // key, and how a player who was tabbed out joined a round in progress.
    //
    // So they land in spectator and choose for themselves. Nothing is blocked and nobody is kicked - the
    // server is open, and once a pug is live anyone may walk in and take a side. They just have to say
    // which one.
    private void HoldMidMatchArrival(CCSPlayerController player)
    {
        if (!MatchRunning || !Config.SpectateMidMatchJoins) return;
        if (player.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist) return;

        // Somebody who was drafted into this match has a right answer, and it is not a question: put them
        // back on their own team. The side it is on now is read live rather than from the ready roster,
        // because the halftime swap moves it and nothing updates that roster.
        ulong id = Id(player);
        if (_team1.Contains(id) || _team2.Contains(id))
        {
            var side = _team1.Contains(id) ? _team1Side : SideOf(2);
            player.ChangeTeam(side);
            SpawnNowOrNextRound(player);
            player.PrintToChat(Prefix() + "Welcome back - you are on "
                + $"{(side == CsTeam.CounterTerrorist ? "CT" : "T")} with your team.");
            Logger.LogInformation("{Name} came back mid-match - put back on {Side} with their team.",
                player.PlayerName, side == CsTeam.CounterTerrorist ? "CT" : "T");
            return;
        }

        // Anybody else chooses for themselves. Not held in _heldInSpectator: that set is the pug's own
        // parking, swept and released by the phases. This is a one-off nudge, and from here it is their
        // call.
        // OnClientPutInServer fires on every pawn the engine makes for them, not just on arrival, so the
        // nudge is once per match: told twice it reads as the server fighting them for the slot.
        if (!_midMatchNudged.Add(Id(player))) return;

        player.ChangeTeam(CsTeam.Spectator);
        player.PrintToChat(Prefix() + "A match is live - you are in spectator so the server does not "
            + "pick a side for you. Press M to join one.");
        Logger.LogInformation("{Name} arrived mid-match - held in spectator to choose a side.", player.PlayerName);
    }

    // Taken a side mid-match and not spawned yet. A team change made by the plugin does not reliably put
    // somebody on the engine's round-start spawn list, so the promise made on their screen is kept here
    // instead of hoped for: at the next round start anyone in this set who is on a side and not alive is
    // spawned, and the set empties either way. Freeze time is paid sooner - see SpawnNowOrNextRound.
    private readonly HashSet<ulong> _owedASpawn = [];

    // Freeze time is *inside* a round, not in front of one: EventRoundStart has already fired and
    // drained the set by the time anyone is frozen at a spawn point. So somebody who takes a side during
    // freeze time is owed a spawn that nothing pays until the round after - they read "YOU WILL SPAWN
    // NEXT ROUND" and then watched a whole round from the dead.
    //
    // Nobody has moved yet during freeze time, so there is nothing to protect: they are spawned into the
    // round that is about to run. The debt is still recorded first and only cleared once the spawn has
    // actually happened, because a team change made by the plugin does not reliably reach the engine's
    // spawn list - if the immediate spawn does not take, the next round start still pays it.
    private bool SpawnNowOrNextRound(CCSPlayerController player)
    {
        ulong id = Id(player);
        _owedASpawn.Add(id);
        if (!MatchRunning || !(GameRules()?.FreezePeriod ?? false)) return false;

        AfterTick(() => PayOwedSpawn(id, "into this round (freeze time)", 3));
        return true;
    }

    // The round start pays whatever freeze time could not. A debt is only cleared by seeing the player
    // alive, so one that no spawn will take is carried to every round start until the match ends.
    internal void SpawnWhoeverTookASideMidMatch()
    {
        if (_owedASpawn.Count == 0) return;

        var here = HumanPlayers().Select(Id).ToHashSet();
        foreach (ulong id in _owedASpawn.ToList())
        {
            // Gone: they do not come back owed a spawn.
            if (!here.Contains(id))
            {
                _owedASpawn.Remove(id);
                continue;
            }
            PayOwedSpawn(id, "at the round start", 3);
        }
    }

    // Respawn() proves nothing by returning: CounterStrikeSharp's returns silently when the controller has
    // no player pawn, and the controller's own PawnIsAlive flag can say alive over a body that is not. So
    // the debt is cleared only by a pawn seen alive half a second later, retried a few times, and what
    // the pawn looked like is logged when it never takes. [SOURCE: CounterStrikeSharp 1.0.378
    // CCSPlayerController.Respawn, read 2026-11-22]
    private void PayOwedSpawn(ulong id, string when, int attemptsLeft)
    {
        if (Retired || !_owedASpawn.Contains(id)) return;
        var them = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && Id(p) == id);
        if (them == null || them.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist))
        {
            _owedASpawn.Remove(id);
            return;
        }
        if (HasLiveBody(them))
        {
            _owedASpawn.Remove(id);
            return;
        }

        try
        {
            them.Respawn();
        }
        catch (Exception error)
        {
            Logger.LogWarning("Respawn of {Name} threw: {Error}", them.PlayerName, error.Message);
        }

        AddTimer(0.5f, () =>
        {
            if (Retired || !_owedASpawn.Contains(id)) return;
            var now = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && Id(p) == id);
            if (now == null) return;
            if (HasLiveBody(now))
            {
                _owedASpawn.Remove(id);
                Logger.LogInformation("Spawned {Name} {When} after their mid-match pick.", now.PlayerName, when);
                return;
            }
            if (attemptsLeft > 1)
            {
                PayOwedSpawn(id, when, attemptsLeft - 1);
                return;
            }

            // Still owed, so the next round start tries again. A reconnect is what has fixed this by
            // hand, and the controller keeps its side through one, so that is what they are told.
            var pawn = now.PlayerPawn.Value;
            Logger.LogWarning("Could not spawn {Name} {When}: team {Team}, PawnIsAlive {Flag}, player pawn "
                + "{Pawn}, life state {Life}, health {Health}. Still owed; told to reconnect.",
                now.PlayerName, when, now.Team, now.PawnIsAlive,
                pawn == null ? "none" : pawn.IsValid ? "valid" : "invalid",
                pawn is { IsValid: true } ? pawn.LifeState : -1, pawn is { IsValid: true } ? pawn.Health : -1);
            now.PrintToChat(Prefix() + "The server could not spawn you. Type retry in the console - you "
                + "come back on the same side.");
        });
    }

    private static bool HasLiveBody(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        return pawn != null && pawn.IsValid && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE && pawn.Health > 0;
    }

    // Everyone the plugin itself has put in spectator. The pug moves people out of the way for the knife
    // and the draft, and before this nothing ever moved them back: an undrafted player, or anyone swept
    // while the teams were being built, sat in spectator through the match and through the one after it.
    private readonly HashSet<ulong> _heldInSpectator = [];

    // Who has already been told, this match, that they are in spectator and may pick. Cleared with the
    // held-spectator sweep, so the next pug asks again.
    private readonly HashSet<ulong> _midMatchNudged = [];

    private void HoldInSpectator(CCSPlayerController player)
    {
        _heldInSpectator.Add(Id(player));
        player.ChangeTeam(CsTeam.Spectator);
    }

    // The opposite, for the draft: on a team, alive, and standing still. Which team does not matter -
    // being picked moves them - but being on one does, because that is what gives them a pawn to look
    // through and a board to look at.
    private void PutOnTheField(CCSPlayerController player, CsTeam side)
    {
        _heldInSpectator.Remove(Id(player));
        if (player.Team != CsTeam.CounterTerrorist && player.Team != CsTeam.Terrorist)
        {
            Logger.LogInformation("Draft: moving {Name} from {From} to {To} and spawning them.",
                player.PlayerName, player.Team, side);
            player.ChangeTeam(side);
        }

        // A team change does not hand back a pawn in the same frame, so the respawn waits for one. The
        // draft sweep calls this again a second later if it still has not taken.
        var pawn = player.PlayerPawn.Value;
        if (pawn != null && pawn.IsValid && pawn.Health > 0) return;

        Server.NextFrame(() =>
        {
            if (!player.IsValid) return;
            var now = player.PlayerPawn.Value;
            if (now != null && now.IsValid && now.Health > 0) return;
            try
            {
                player.Respawn();
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Could not respawn {Name} for the draft: {Message}", player.PlayerName, exception.Message);
            }
        });
    }

    // Hands spectator back to the player. This used to put them on CsTeam.None - the unassigned slot a
    // fresh connection sits in - because that makes the team-select menu come up by itself. It also puts
    // them in no team section at all, which is how people went missing from the scoreboard. Spectator
    // proper costs them pressing M and keeps them on it. Only players the pug moved are touched; anyone
    // who chose to spectate stays where they are.
    private void ReleaseHeldSpectators()
    {
        _midMatchNudged.Clear();
        if (_heldInSpectator.Count == 0) return;

        foreach (var player in HumanPlayers())
        {
            if (player.IsBot || !_heldInSpectator.Contains(Id(player))) continue;
            if (player.Team != CsTeam.Spectator) continue;
            player.PrintToChat(Prefix() + "You are free to pick a team again - press M.");
        }
        _heldInSpectator.Clear();
    }

    private void AddToPool(ulong steamId)
    {
        // Back after leaving mid-draft: they keep the number they had. Nobody else was given it.
        if (_pool.Contains(steamId))
        {
            if (_gone.Remove(steamId))
            {
                Announce($"{Name(steamId)} is back in the pool as !{_pool.IndexOf(steamId) + 1}.");
                GrowDraft();
            }
            return;
        }
        if (_pickIndex >= _pickOrder.Count && DateTime.UtcNow >= _joinsOpenUntil)
        {
            // The draft is already wrapping up and the join window has closed.
            Announce($"{Name(steamId)} arrived too late to be drafted.");
            return;
        }

        // Appended, never inserted: the numbers already on screen have to keep meaning what they did.
        _pool.Add(steamId);
        Announce($"{Name(steamId)} joined the draft pool as !{_pool.Count} ({UndraftedCount()} undrafted).");
        GrowDraft();
    }

    // Auto-sized teams grow as people arrive, and the pick order grows with them. Both pick orders are
    // deterministic from the start, so regenerating a longer one leaves the picks already made untouched.
    private void GrowDraft()
    {
        int headcount = _team1.Count + _team2.Count + UndraftedCount();
        bool autoSize = _teamSizeOverride == 0 && Config.PlayersPerTeam == 0;

        if (autoSize && (headcount + 1) / 2 > _playersPerTeam)
        {
            _playersPerTeam = (headcount + 1) / 2;
            Announce($"Now drafting up to {_playersPerTeam} a side.");
        }

        // Auto-sized: everyone undrafted still gets picked, however the split lands.
        int slotsLeft = autoSize ? UndraftedCount() : (_playersPerTeam * 2) - _team1.Count - _team2.Count;
        int total = _pickIndex + Math.Min(slotsLeft, UndraftedCount());
        if (total <= _pickOrder.Count) return;

        _pickOrder = BuildPickOrder(total, Config.PickOrder, _firstPick);
        Logger.LogInformation("Pick order extended to {Total} picks ({Index} made).", total, _pickIndex);
        // Same order as StartDraft, for the same reason: PromptPick can end the draft here too, and a
        // StartHud after that is a HUD timer nothing will stop.
        StartHud();
        PromptPick();
    }

    // Blocking jointeam is not quite enough: a player sitting in the team-select menu is on no team at
    // all, and the engine will happily put them somewhere when they spawn. Sweep them back to spectator.
    private CounterStrikeSharp.API.Modules.Timers.Timer? _spectateTimer;

    private void StartSpectateGuard()
    {
        if (!Config.ForceSpectate) return;
        if (_spectateTimer != null) return;
        _spectateTimer?.Kill();
        _spectateTimer = AddTimer(2.0f, SpectateTick, TimerFlags.REPEAT);
    }

    private void StopSpectateGuard()
    {
        _spectateTimer?.Kill();
        _spectateTimer = null;
    }

    private void SpectateTick()
    {
        if (_phase == PugPhase.Idle)
        {
            StopSpectateGuard();
            return;
        }

        foreach (var player in HumanPlayers())
        {
            // Bots are never swept: a bot in spectator does not count towards bot_quota, so the engine
            // kicks it and adds a replacement, and a test run's roster churns under the draft.
            if (player.IsBot) continue;
            if (BelongsOnTheField(Id(player))) continue;
            if (player.Team == CsTeam.Spectator) continue;
            HoldInSpectator(player);
        }
    }

    // Who is allowed out of spectator: nobody during the vote, the two captains during their duel, and
    // drafted players once they have been picked.
    private bool BelongsOnTheField(ulong steamId)
    {
        // The draft keeps everybody on the field now, drafted or not. The overlay is the draft, and a
        // spectator's camera is driven client-side: the server cannot read where it is, so a board hung
        // on it lands somewhere nobody is looking. Everyone alive has a pawn, and a pawn is a camera the
        // boards can be hung from. They are frozen instead, which stops the running about that holding
        // them in spectator was for.
        //
        // The captain duel still clears the floor: a third player in a 1v1 is a third player who can win
        // it. Those watchers get two lines of centre-screen alert rather than boards.
        return _phase switch
        {
            PugPhase.MapVote => true,
            PugPhase.CaptainKnife => steamId == _captain1 || steamId == _captain2,
            PugPhase.Draft => true,
            _ => _team1.Contains(steamId) || _team2.Contains(steamId),
        };
    }
}
