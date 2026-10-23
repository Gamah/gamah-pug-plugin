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
        if (_phase is PugPhase.Idle or PugPhase.MapVote) return HookResult.Continue;
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
            if (_phase is PugPhase.Idle or PugPhase.MapVote) return;

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

    // Everyone the plugin itself has put in spectator. The pug moves people out of the way for the knife
    // and the draft, and before this nothing ever moved them back: an undrafted player, or anyone swept
    // while the teams were being built, sat in spectator through the match and through the one after it.
    private readonly HashSet<ulong> _heldInSpectator = [];

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
