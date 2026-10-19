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

    // Who the ready gate is actually about: the drafted players who are here and can type. Bots play the
    // match but cannot ready, and somebody who has left cannot either.
    //
    // Counting a player who is not connected means a pug waits forever on somebody who has gone home,
    // with no way past it but an admin - which is what "my .ready did nothing" was. They are named on
    // the panel so the room can decide whether to wait for them, and that decision belongs to the room
    // rather than to a gate that cannot be argued with.
    private List<ulong> ReadyRoster() => _roster.Keys
        .Where(steamId => !IsBotId(steamId) && PlayerBySteamId(steamId) != null)
        .ToList();

    // Drafted, human, and not here. The panel names them; nothing waits on them.
    internal List<string> AbsentRoster() => _roster.Keys
        .Where(steamId => !IsBotId(steamId) && PlayerBySteamId(steamId) == null)
        .Select(Name)
        .ToList();

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
        // Round one's Discord hold has a ready of its own: same word, different question.
        if (ReadyForOpening(player)) return;

        if (_matchPhase != MatchPhase.Ready)
        {
            if (player != null && player.IsValid && _matchPhase == MatchPhase.None)
            {
                player.PrintToChat(Prefix() + "Nothing to ready for - an admin starts a pug with !pug start.");
            }
            return;
        }

        // The sweep puts whoever is here on the side they were drafted to and spawns them. It used to be
        // a gate as well - any name it returned refused the ready outright - so one player who had left
        // for good held a whole pug, and everybody else's .ready did nothing at all with no way past it
        // but an admin. It reports now, and readiness is counted over the people who are actually here.
        var missing = SweepRoster();
        if (missing.Count > 0)
        {
            string who = string.Join(", ", missing);
            if (player != null && player.IsValid) player.PrintToChat(Prefix() + $"Still out: {who}.");
            else Logger.LogInformation("Readying with players out: {Who}", who);
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
