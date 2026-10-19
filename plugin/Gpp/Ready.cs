using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// The ready phase. This used to be MatchZy's, with GPP inferring who had readied by watching the commands
// go past - MatchZy kept the real list in playerReadyStatus and exposed no way to read it. The list below
// *is* the real list now, so the panel and the threshold agree by construction.
//
// The gate in front of it stays: a knife round is decided from who is alive when the round ends, and a
// round can end the instant it restarts, so a knife round that starts with somebody still in spectator or
// dead is decided on the spot with no knives thrown. Until every drafted player is connected, on their
// drafted side and spawned, a ready does not count - and the sweep moves them there while it waits.
public partial class Gpp
{
    private readonly Dictionary<ulong, CsTeam> _roster = new();

    private readonly HashSet<ulong> _readied = [];

    private bool _gateActive;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _gateTimer;

    private void StartReadyPhase()
    {
        _roster.Clear();
        _readied.Clear();
        foreach (var steamId in _team1) _roster[steamId] = _team1Side;
        foreach (var steamId in _team2) _roster[steamId] = SideOf(2);
        if (_roster.Count == 0) return;

        _gateActive = true;
        _gateTimer?.Kill();
        _gateTimer = AddTimer(2.0f, GateTick, TimerFlags.REPEAT);
    }

    // The roster deliberately survives: the panel keeps listing who has not readied for the whole phase,
    // and StopReadyHud clears it.
    private void StopReadyPhase()
    {
        _gateActive = false;
        _gateTimer?.Kill();
        _gateTimer = null;
    }

    // Bots play the match but cannot type, so they are neither counted nor named.
    private List<ulong> ReadyRoster() => _roster.Keys.Where(steamId => !IsBotId(steamId)).ToList();

    internal int ReadyCount() => ReadyRoster().Count(_readied.Contains);

    internal int ReadyNeeded() => MatchRules.ReadyNeeded(ReadyRoster().Count, Config.MinReady);

    private void GateTick()
    {
        // Warmup is the only window where moving and respawning people is harmless: the knife round ends
        // the warmup, so this stops on its own as soon as the match gets going.
        if (!_gateActive || !InWarmupPeriod())
        {
            if (_gateActive && !InWarmupPeriod()) StopReadyPhase();
            return;
        }
        SweepRoster();
    }

    // Puts drafted players back on their drafted side and spawns them. Returns who is still not in place.
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

    // --- the commands -----------------------------------------------------------------------------

    [ConsoleCommand("css_ready", "Ready up for the pug")]
    [ConsoleCommand("css_r", "Ready up for the pug")]
    public void OnReadyCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => Ready(player == null || !player.IsValid ? null : PlayerBySteamId(Id(player))));

    [ConsoleCommand("css_unready", "Take your ready back")]
    [ConsoleCommand("css_notready", "Take your ready back")]
    [ConsoleCommand("css_ur", "Take your ready back")]
    public void OnUnreadyCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => Unready(player == null || !player.IsValid ? null : PlayerBySteamId(Id(player))));

    [ConsoleCommand("css_forceready", "Admin: ready everybody and start")]
    [ConsoleCommand("css_forcestart", "Admin: ready everybody and start")]
    [ConsoleCommand("css_start", "Admin: ready everybody and start")]
    public void OnForceReadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsAdmin(player))
        {
            Reply(command, "You do not have access to this command.");
            return;
        }
        AfterTick(ForceReady);
    }

    // A ready from a player, from an admin over rcon (player null), or from the chat hook.
    internal void Ready(CCSPlayerController? player)
    {
        if (_matchPhase != MatchPhase.Ready)
        {
            if (player != null && player.IsValid && _matchPhase == MatchPhase.None)
            {
                player.PrintToChat(Prefix() + "Nothing to ready for - an admin starts a pug with !pug start.");
            }
            return;
        }

        // The gate: everybody has to be in place before the first ready counts for anything.
        var missing = SweepRoster();
        if (missing.Count > 0)
        {
            string who = string.Join(", ", missing);
            if (player != null && player.IsValid) player.PrintToChat(Prefix() + $"Not yet - waiting on {who}.");
            else Logger.LogInformation("Ready held: {Who}", who);
            return;
        }
        StopReadyPhase();

        if (player == null || !player.IsValid)
        {
            // From the console: nobody to ready, so take it as "start it".
            ForceReady();
            return;
        }

        if (!_readied.Add(Id(player))) return;
        Announce($"{player.PlayerName} is ready ({ReadyCount()}/{ReadyNeeded()}).");
        StartIfEverybodyIsReady();
    }

    internal void Unready(CCSPlayerController? player)
    {
        if (_matchPhase != MatchPhase.Ready || player == null || !player.IsValid) return;
        if (!_readied.Remove(Id(player))) return;
        Announce($"{player.PlayerName} is not ready ({ReadyCount()}/{ReadyNeeded()}).");
    }

    internal void ForceReady()
    {
        if (_matchPhase != MatchPhase.Ready) return;

        var missing = SweepRoster();
        if (missing.Count > 0) Announce($"Starting anyway, waiting on {string.Join(", ", missing)}.");
        StopReadyPhase();

        foreach (var steamId in _roster.Keys) _readied.Add(steamId);
        AfterTick(StartKnifeOrLive);
    }

    private void StartIfEverybodyIsReady()
    {
        if (_matchPhase != MatchPhase.Ready || ReadyCount() < ReadyNeeded()) return;
        AfterTick(StartKnifeOrLive);
    }
}
