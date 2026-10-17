using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
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

    private readonly Dictionary<ulong, CsTeam> _roster = new();
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
    }

    private void StartReadyGate()
    {
        _roster.Clear();
        foreach (var steamId in _team1) _roster[steamId] = CsTeam.CounterTerrorist;
        foreach (var steamId in _team2) _roster[steamId] = CsTeam.Terrorist;
        if (_roster.Count == 0) return;

        _gateActive = true;
        _gateTimer?.Kill();
        _gateTimer = AddTimer(2.0f, GateTick, TimerFlags.REPEAT);
    }

    private void StopReadyGate()
    {
        _gateActive = false;
        _gateTimer?.Kill();
        _gateTimer = null;
        _roster.Clear();
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
        if (!_gateActive || !InWarmupPeriod()) return HookResult.Continue;

        var missing = SweepRoster();
        if (missing.Count == 0)
        {
            StopReadyGate();
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

    private HookResult OnForceStartCommand(CCSPlayerController? player, CommandInfo command)
    {
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
