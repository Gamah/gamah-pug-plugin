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

    // Hands spectator back to the player. CsTeam.None is the unassigned slot the engine puts a fresh
    // connection in, which is what makes the team-select menu come up; a player left in Spectator proper
    // has to find it themselves, and most do not. Only players the pug moved are touched - anyone who
    // chose to spectate stays where they are.
    private void ReleaseHeldSpectators()
    {
        if (_heldInSpectator.Count == 0) return;

        foreach (var player in HumanPlayers())
        {
            if (player.IsBot || !_heldInSpectator.Contains(Id(player))) continue;
            if (player.Team != CsTeam.Spectator) continue;
            player.ChangeTeam(CsTeam.None);
            player.PrintToChat(Prefix() + "You are free to pick a team again - press M.");
        }
        _heldInSpectator.Clear();
    }

    private void AddToPool(ulong steamId)
    {
        if (_available.Contains(steamId)) return;
        if (_pickIndex >= _pickOrder.Count && DateTime.UtcNow >= _joinsOpenUntil)
        {
            // The draft is already wrapping up and the join window has closed.
            Announce($"{Name(steamId)} arrived too late to be drafted.");
            return;
        }

        _available.Add(steamId);
        Announce($"{Name(steamId)} joined the draft pool ({_available.Count} undrafted).");
        GrowDraft();
    }

    // Auto-sized teams grow as people arrive, and the pick order grows with them. Both pick orders are
    // deterministic from the start, so regenerating a longer one leaves the picks already made untouched.
    private void GrowDraft()
    {
        int headcount = _team1.Count + _team2.Count + _available.Count;
        bool autoSize = _teamSizeOverride == 0 && Config.PlayersPerTeam == 0;

        if (autoSize && (headcount + 1) / 2 > _playersPerTeam)
        {
            _playersPerTeam = (headcount + 1) / 2;
            Announce($"Now drafting up to {_playersPerTeam} a side.");
        }

        // Auto-sized: everyone undrafted still gets picked, however the split lands.
        int slotsLeft = autoSize ? _available.Count : (_playersPerTeam * 2) - _team1.Count - _team2.Count;
        int total = _pickIndex + Math.Min(slotsLeft, _available.Count);
        if (total <= _pickOrder.Count) return;

        _pickOrder = BuildPickOrder(total, Config.PickOrder, _firstPick);
        Logger.LogInformation("Pick order extended to {Total} picks ({Index} made).", total, _pickIndex);
        PromptPick();
        StartHud();
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
        return _phase switch
        {
            PugPhase.MapVote => false,
            PugPhase.CaptainKnife => steamId == _captain1 || steamId == _captain2,
            _ => _team1.Contains(steamId) || _team2.Contains(steamId),
        };
    }
}
