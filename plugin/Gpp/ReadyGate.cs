using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// MatchZy decides the knife round from whoever is alive the moment the round ends, and a round can end the
// instant it restarts - so a knife round that starts while someone is still in spectator or dead is decided
// on the spot, with no knives thrown (a 2v2 where both sides are even goes to a coin flip, which looks like
// a captain winning for nothing). MatchZy readies players regardless of where they are standing, so between
// the handoff and the first .ready this holds the ready commands until every drafted player is connected, on
// their drafted team and spawned - and moves them there while it waits.
public partial class Gpp
{
    private static readonly string[] ReadyCommands = ["css_ready"];

    // Admin overrides: these still sweep, but they are an explicit "start it anyway" and are not blocked.
    private static readonly string[] ForceStartCommands =
        ["css_start", "css_force", "css_forcestart", "css_forceready"];

    // MatchZy's chat aliases for the same thing. ".ready" typed in chat never becomes a css_ command
    // unless "." is a CounterStrikeSharp chat trigger, in which case MatchZy stands aside and CSSharp runs
    // css_ready instead - so both routes have to be watched, and both are idempotent.
    // [SOURCE: MatchZy 0.9.1 MatchZy.cs EventPlayerChat/commandActions, read 2026-10-19]
    private static readonly string[] ReadyChat = [".ready", ".r"];
    private static readonly string[] UnreadyChat = [".unready", ".notready", ".ur"];
    private static readonly string[] ForceReadyChat = [".forceready"];

    private static readonly string[] UnreadyCommands = ["css_unready", "css_notready"];

    private readonly Dictionary<ulong, CsTeam> _roster = new();

    // Who has readied, as far as GPP can tell. MatchZy keeps the real list in playerReadyStatus and
    // exposes nothing to read it, so this is inferred from the ready commands going past.
    private readonly HashSet<ulong> _readied = [];

    private bool _gateActive;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _gateTimer;

    private void HookReadyGate()
    {
        foreach (var command in ReadyCommands)
        {
            AddCommandListener(command, OnReadyCommand, HookMode.Pre);
        }
        foreach (var command in ForceStartCommands)
        {
            AddCommandListener(command, OnForceStartCommand, HookMode.Pre);
        }
        foreach (var command in UnreadyCommands)
        {
            AddCommandListener(command, OnUnreadyCommand, HookMode.Pre);
        }

        RegisterEventHandler<EventPlayerChat>((@event, _) =>
        {
            // EventPlayerChat carries an entity index, not a userid or a slot.
            var player = Utilities.GetPlayerFromIndex(@event.Userid + 1);
            if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

            string message = @event.Text.Trim().ToLowerInvariant();
            if (ReadyChat.Contains(message)) _readied.Add(Id(player));
            else if (UnreadyChat.Contains(message)) _readied.Remove(Id(player));
            else if (ForceReadyChat.Contains(message)) ReadyEverybody();
            return HookResult.Continue;
        });
    }

    // A force ready readies a whole side at once, and which side depends on who called it; the panel is
    // about to come down either way, so treat it as everybody.
    private void ReadyEverybody()
    {
        foreach (var steamId in _roster.Keys) _readied.Add(steamId);
    }

    private void StartReadyGate()
    {
        _roster.Clear();
        _readied.Clear();
        foreach (var steamId in _team1) _roster[steamId] = CsTeam.CounterTerrorist;
        foreach (var steamId in _team2) _roster[steamId] = CsTeam.Terrorist;
        if (_roster.Count == 0) return;

        _gateActive = true;
        _gateTimer?.Kill();
        _gateTimer = AddTimer(2.0f, GateTick, TimerFlags.REPEAT);
    }

    // The roster deliberately survives: the gate stops at the first ready that gets through, the ready
    // panel keeps listing who has not readied for the whole phase, and StopReadyHud clears it.
    private void StopReadyGate()
    {
        _gateActive = false;
        _gateTimer?.Kill();
        _gateTimer = null;
    }

    private void GateTick()
    {
        // Warmup is the only window where moving and respawning people is harmless: the knife round itself
        // ends warmup, so this stops on its own as soon as the match gets going.
        if (!_gateActive || !InWarmupPeriod())
        {
            if (_gateActive && !InWarmupPeriod()) StopReadyGate();
            return;
        }
        SweepRoster();
    }

    // Puts drafted players back on their drafted team and spawns them. Returns who is still not in place.
    private List<string> SweepRoster()
    {
        var missing = new List<string>();

        foreach (var (steamId, team) in _roster)
        {
            var player = PlayerBySteamId(steamId);
            if (player == null || !player.IsValid)
            {
                missing.Add($"{Name(steamId)} (not connected)");
                continue;
            }

            if (player.Team != team)
            {
                player.ChangeTeam(team);
                missing.Add($"{player.PlayerName} (moving to {(team == CsTeam.CounterTerrorist ? "CT" : "T")})");
                continue;
            }

            if (!player.PawnIsAlive)
            {
                // Warmup respawns on its own, but not always promptly, and a dead player at the knife
                // restart counts as a loss for their side.
                try
                {
                    player.Respawn();
                }
                catch (Exception exception)
                {
                    Logger.LogWarning("Could not respawn {Name}: {Message}", player.PlayerName, exception.Message);
                }
                missing.Add($"{player.PlayerName} (spawning)");
            }
        }

        return missing;
    }

    private HookResult OnReadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!_gateActive || !InWarmupPeriod())
        {
            if (player != null && player.IsValid) _readied.Add(Id(player));
            return HookResult.Continue;
        }

        var missing = SweepRoster();
        if (missing.Count == 0)
        {
            StopReadyGate();
            if (player != null && player.IsValid) _readied.Add(Id(player));
            return HookResult.Continue;
        }

        string who = string.Join(", ", missing);
        if (player != null && player.IsValid)
        {
            player.PrintToChat(Prefix() + $"Not yet - waiting on {who}.");
        }
        else
        {
            Logger.LogInformation("Ready held: {Who}", who);
        }
        return HookResult.Stop;
    }

    private HookResult OnUnreadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && player.IsValid) _readied.Remove(Id(player));
        return HookResult.Continue;
    }

    private HookResult OnForceStartCommand(CCSPlayerController? player, CommandInfo command)
    {
        ReadyEverybody();
        if (!_gateActive) return HookResult.Continue;

        var missing = SweepRoster();
        if (missing.Count > 0)
        {
            Announce($"Starting anyway, waiting on {string.Join(", ", missing)}.");
        }
        StopReadyGate();
        return HookResult.Continue;
    }

    // Strictly the engine's warmup flag: InWarmup() also counts a map with no rounds played, which includes
    // the knife round, and nothing here may touch players once that has started.
    private bool InWarmupPeriod()
    {
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        return proxy?.GameRules?.WarmupPeriod ?? false;
    }
}
