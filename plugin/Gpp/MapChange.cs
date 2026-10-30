using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// .map, for the warmup: loading the map people want to throw smokes on, which until now meant an admin at
// a console or a pug nobody wanted.
//
// It is fenced into the warmup with the rest of the toys - a map change during a live match is the match
// gone - and it is deliberately awkward in one specific way: an admin's .map does not change the map on
// its own. It arms a request that an admin has to answer with .yes inside map_confirm_seconds. The reason
// is that the room's actual mistake is the other one: typing .map because they want to play a pug on that
// map, when !pug start runs a map vote of its own and the .map was a map change for nothing. Ten seconds
// and a second word is enough to make somebody notice they are changing the map for practice.
//
// With no admin connected it is the room's call, and the bar is everybody: a map change takes the server
// away from whatever everyone else was doing, so one person wanting it is not enough and saying nothing
// is a no.
public partial class Gpp
{
    private MapEntry? _mapRequest;
    private string _mapRequestBy = "";
    private int _mapSecondsLeft;
    private bool _mapRequestIsVote;
    private readonly HashSet<ulong> _mapYes = [];
    private readonly HashSet<ulong> _mapNo = [];
    private CounterStrikeSharp.API.Modules.Timers.Timer? _mapTimer;

    private bool MapRequestOpen => _mapTimer != null;

    [ConsoleCommand("css_map", "Change the map during warmup")]
    [CommandHelper(minArgs: 0, usage: "<map>")]
    public void OnMapCommand(CCSPlayerController? player, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.ArgString.Trim() : "";
        AfterTick(() => MapCommand(player, arg));
    }

    [ConsoleCommand("css_yes", "Agree to the map change on offer")]
    public void OnYesCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => AnswerMapRequest(player, yes: true));

    [ConsoleCommand("css_no", "Refuse the map change on offer")]
    public void OnNoCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => AnswerMapRequest(player, yes: false));

    internal void MapCommand(CCSPlayerController? caller, string arg)
    {
        if (Config.MapConfirmSeconds <= 0)
        {
            Tell(caller, ".map is turned off on this server.");
            return;
        }
        if (!WarmupToy(caller, out _)) return;
        if (MapRequestOpen)
        {
            Tell(caller, $"{_mapRequest!.Label} is already on offer - .yes or .no.");
            return;
        }
        if (arg.Length == 0)
        {
            Tell(caller, "Usage: .map <map> - !pug for the list, and !pug start runs its own map vote.");
            return;
        }

        var map = FindMap(caller, arg);
        if (map == null) return;
        if (!map.IsWorkshop && Server.MapName.Equals(map.Name, StringComparison.OrdinalIgnoreCase))
        {
            Tell(caller, $"Already on {map.Label}.");
            return;
        }

        _mapRequest = map;
        _mapRequestBy = caller != null && caller.IsValid ? caller.PlayerName : "An admin";
        _mapYes.Clear();
        _mapNo.Clear();

        // Who has to agree. An admin anywhere in the room means the answer is an admin's, whoever asked -
        // a player's .map with an admin connected is a request put to them rather than a vote they would
        // be outvoted in anyway.
        _mapRequestIsVote = !AnyAdminHere();
        _mapSecondsLeft = _mapRequestIsVote
            ? Math.Max(5, Config.MapVoteSeconds)
            : Math.Max(5, Config.MapConfirmSeconds);

        if (_mapRequestIsVote)
        {
            Announce($"{_mapRequestBy} wants to change the map to "
                + $"{Colorize("{green}")}{_mapRequest.Label}{Colorize("{default}")}.");
            Announce($"Everybody has to agree, and saying nothing is a no. {_mapSecondsLeft}s.");
            Prompt("Change the map?", ".yes / .no", Everybody);
        }
        else
        {
            Announce($"{_mapRequestBy} wants to change the map to "
                + $"{Colorize("{green}")}{_mapRequest.Label}{Colorize("{default}")}.");
            Announce("!pug start runs its own map vote - you do not need .map to start a pug.");
            Prompt($"Changing the map for practice? {_mapSecondsLeft}s to confirm:", ".yes", "ADMIN");
        }

        _mapTimer = AddTimer(1.0f, MapRequestTick, TimerFlags.REPEAT);
        MapRequestPanel();
    }

    private bool AnyAdminHere() => HumanPlayers().Any(player => !player.IsBot && IsAdmin(player));

    // Exact name or label first, then a single unambiguous partial - the same rule .fent and the draft
    // use. The pool is the whole list on purpose: these are the maps the server has actually downloaded,
    // and a changelevel to a name nobody has is a server that does not come back.
    private MapEntry? FindMap(CCSPlayerController? caller, string needle)
    {
        var pool = Config.MapPool;

        var exact = pool.FirstOrDefault(map =>
            map.Name.Equals(needle, StringComparison.OrdinalIgnoreCase)
            || map.Label.Equals(needle, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var partial = pool
            .Where(map => map.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || map.Label.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (partial.Count == 1) return partial[0];

        Tell(caller, partial.Count == 0
            ? $"No map called \"{needle}\" - !pug for the list."
            : $"\"{needle}\" matches {partial.Count} maps - type more of it.");
        return null;
    }

    internal void AnswerMapRequest(CCSPlayerController? player, bool yes)
    {
        // Nothing on offer means nothing to say: ".no" in chat is somebody talking, the same way a bare
        // number outside a vote is. The console gets told, because a console caller typed it on purpose.
        if (!MapRequestOpen)
        {
            if (player == null || !player.IsValid) Tell(player, "Nothing on offer - .map <map> asks for one.");
            return;
        }

        if (!_mapRequestIsVote)
        {
            // The admin confirm. One word from anybody with the flag does it, including over rcon, and
            // anybody else saying it is told whose call it is.
            if (!IsAdmin(player))
            {
                Tell(player, "An admin has to confirm the map change.");
                return;
            }
            if (!yes)
            {
                CloseMapRequest($"{Who(player)} said no.");
                return;
            }
            DoMapChange();
            return;
        }

        if (player == null || !player.IsValid) return;

        if (yes)
        {
            _mapNo.Remove(Id(player));
            if (!_mapYes.Add(Id(player))) return;
        }
        else
        {
            _mapYes.Remove(Id(player));
            if (!_mapNo.Add(Id(player))) return;
            // One no is the whole answer: the bar is everybody, so the vote cannot be won from here and
            // holding the room for the rest of the thirty seconds proves nothing.
            CloseMapRequest($"{player.PlayerName} said no.");
            return;
        }

        var voters = AnsweringPlayers();
        Announce($"{player.PlayerName} is up for {_mapRequest!.Label} ({_mapYes.Count}/{voters.Count}).");
        if (voters.All(other => _mapYes.Contains(Id(other)))) DoMapChange();
        else MapRequestPanel();
    }

    private string Who(CCSPlayerController? player) =>
        player != null && player.IsValid ? player.PlayerName : "The console";

    private void MapRequestTick()
    {
        // Anything that stops being a quiet warmup takes the offer with it. A pug starting is the big
        // one: it runs a map vote of its own, and the two of them deciding the map at once is the mess
        // the confirm exists to avoid.
        if (_phase != PugPhase.Idle || MatchRunning || !InWarmupPeriod())
        {
            AfterTick(() => CloseMapRequest("Something else is going on now."));
            return;
        }
        if (--_mapSecondsLeft > 0)
        {
            MapRequestPanel();
            return;
        }
        AfterTick(() => CloseMapRequest(_mapRequestIsVote
            ? "Not everybody agreed - staying put."
            : "Nobody confirmed it - staying put."));
    }

    private void CloseMapRequest(string why)
    {
        if (!MapRequestOpen) return;
        _mapTimer?.Kill();
        _mapTimer = null;
        _mapSecondsLeft = 0;
        _mapRequest = null;
        _mapYes.Clear();
        _mapNo.Clear();
        Clear();
        Announce($"Map change dropped. {why}");
    }

    private void DoMapChange()
    {
        var map = _mapRequest;
        if (map == null) return;

        _mapTimer?.Kill();
        _mapTimer = null;
        _mapSecondsLeft = 0;
        _mapRequest = null;
        _mapYes.Clear();
        _mapNo.Clear();
        Clear();

        // The toys do not survive a map change, and the engine will not clear them: a warped FOV follows
        // the player onto the new map, where nothing knows it is there.
        ClearFunEffects();

        Announce($"Changing the map to {Colorize("{green}")}{map.Label}{Colorize("{default}")}.");
        Logger.LogInformation("Map change to {Map} from .map, asked for by {Who}.", map.Label, _mapRequestBy);
        AddTimer(3.0f, () => ChangeMap(map));
    }

    private void MapRequestPanel()
    {
        if (!Config.Hud) return;
        _repaint = MapRequestPanel;
        WorldMapRequestPanel();
    }
}
