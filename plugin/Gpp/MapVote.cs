using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

public partial class Gpp
{
    private List<MapEntry> _ballot = [];
    private readonly Dictionary<ulong, int> _votes = [];
    private int _voteSecondsLeft;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _voteTimer;

    private void StartMapVote(bool force, bool skipMatchCheck = false)
    {
        if (_phase != PugPhase.Idle)
        {
            Announce($"Already busy: {Status()}");
            return;
        }
        if (Config.MapPool.Count == 0)
        {
            Announce("No maps in the pool - fill map_pool in the config.");
            return;
        }

        var players = HumanPlayers();
        if (!force && players.Count < Config.MinPlayers)
        {
            Announce($"Need {Config.MinPlayers} players to start ({players.Count} here). Use !pug vote force to override.");
            return;
        }

        if (!skipMatchCheck)
        {
            if (!MatchIsClear(force, out bool waitForEnd)) return;
            if (waitForEnd)
            {
                AddTimer(3.0f, () => StartMapVote(force, skipMatchCheck: true));
                return;
            }
        }

        ClearFunEffects();
        ResetVote();
        ResetKnife();
        _phase = PugPhase.MapVote;
        _ballot = Ballot();
        _voteSecondsLeft = Math.Max(5, Config.VoteSeconds);

        // The vote does not take the server away from anyone: people carry on warming up while they vote,
        // and the round is left alone. The old reason for freezing it - the HTML overlay losing to the
        // warmup HUD - went away when the overlay moved to the Center and Alert channels, which draw over
        // warmup quite happily. The knife and the draft still hold the round and the teams, because those
        // two really do need everybody standing still.
        StartHud();

        Prompt($"Map vote - {_voteSecondsLeft}s.", "vote now - !1, !2, ...", Everybody);
        // The ballot goes to chat even with the HUD on. The centre panel is drawn from the player's own
        // pawn, and force_spectate has just moved everybody to spectator, so the overlay reaches nobody
        // during a vote - which is how a vote ran with no map list on anyone's screen.
        PrintBallotToAll();

        _voteTimer = AddTimer(1.0f, VoteTick, TimerFlags.REPEAT);
    }

    // A random handful of the pool rather than the whole thing: a ten-map ballot is a ten-line overlay
    // and the same three maps win anyway.
    private List<MapEntry> Ballot()
    {
        var pool = Config.MapPool.ToList();
        if (Config.BallotSize <= 0 || Config.BallotSize >= pool.Count) return pool;
        return pool.OrderBy(_ => _rng.Next()).Take(Config.BallotSize).ToList();
    }

    // A line per map is fine for a short ballot and is thirty lines of scrollback for the whole pool, so
    // past a dozen maps chat points at the overlay instead of competing with it.
    private const int BallotChatLimit = 12;

    private void PrintBallotToAll()
    {
        if (_ballot.Count > BallotChatLimit)
        {
            Announce($"{_ballot.Count} maps on the ballot - the list is on your screen, vote with !1, !2, ...");
            return;
        }
        for (int i = 0; i < _ballot.Count; i++)
        {
            Announce($"{Colorize("{green}")}!{i + 1}{Colorize("{default}")} - {_ballot[i].Label}");
        }
    }

    private void VoteTick()
    {
        _voteSecondsLeft--;

        // Everyone present has voted: no reason to keep waiting.
        bool everyoneVoted = _votes.Count > 0 && _votes.Count >= HumanPlayers().Count;
        if (_voteSecondsLeft > 0 && !everyoneVoted) return;

        _voteTimer?.Kill();
        _voteTimer = null;
        AfterTick(FinishMapVote);
    }

    [ConsoleCommand("css_vote", "Vote for a map during a GPP map vote")]
    [CommandHelper(minArgs: 1, usage: "<number>")]
    public void OnVoteCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid) return;
        CastVote(player, command.ArgByIndex(1).Trim());
    }

    // Shared by !vote <n> and the bare !<n>.
    internal void CastVote(CCSPlayerController player, string arg)
    {
        if (_phase != PugPhase.MapVote)
        {
            player.PrintToChat(Prefix() + "No map vote is running.");
            return;
        }

        int index = ResolveBallotIndex(arg);
        if (index < 0)
        {
            player.PrintToChat(Prefix() + $"Not a map on the ballot: {arg}");
            return;
        }

        _votes[Id(player)] = index;
        // The overlay shows the tally live, so the room does not need a line per vote.
        player.PrintToChat(Prefix() + $"Voted {Colorize("{green}")}{_ballot[index].Label}{Colorize("{default}")}.");
    }

    private int ResolveBallotIndex(string arg)
    {
        if (int.TryParse(arg, out int number) && number >= 1 && number <= _ballot.Count) return number - 1;

        int match = _ballot.FindIndex(m =>
            m.Name.Equals(arg, StringComparison.OrdinalIgnoreCase) ||
            m.Label.Equals(arg, StringComparison.OrdinalIgnoreCase));
        if (match >= 0) return match;

        var partial = _ballot
            .Select((m, i) => (m, i))
            .Where(t => t.m.Name.Contains(arg, StringComparison.OrdinalIgnoreCase) ||
                        t.m.Label.Contains(arg, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return partial.Count == 1 ? partial[0].i : -1;
    }

    private void FinishMapVote()
    {
        var tally = new int[_ballot.Count];
        foreach (var index in _votes.Values) tally[index]++;

        MapEntry winner;
        if (_votes.Count == 0)
        {
            winner = _ballot[_rng.Next(_ballot.Count)];
            Announce($"Nobody voted - random map: {Colorize("{green}")}{winner.Label}{Colorize("{default}")}.");
        }
        else
        {
            int best = tally.Max();
            var top = Enumerable.Range(0, _ballot.Count).Where(i => tally[i] == best).ToList();
            winner = _ballot[top[_rng.Next(top.Count)]];
            string tie = top.Count > 1 ? ", tie broken at random" : "";
            Announce($"Map: {Colorize("{green}")}{winner.Label}{Colorize("{default}")} ({best} vote(s){tie}).");
        }

        _votedMap = winner;
        var here = HumanPlayers();
        _expectedPlayers = here.Count;
        _expectedBots = here.Count(player => player.IsBot);
        _expectedRoster.Clear();
        foreach (var player in here.Where(player => !player.IsBot)) _expectedRoster[Id(player)] = player.PlayerName;
        ResetVote();
        StopHud();
        _phase = PugPhase.Idle;

        // Captains are drawn now, from the people who actually voted the map in - after a map change the
        // room fills in reconnect order, and that would hand the armbands to whoever loads fastest.
        if (here.Count >= 2)
        {
            var shuffled = here.Select(Id).OrderBy(_ => _rng.Next()).ToList();
            _captain1 = shuffled[0];
            _captain2 = shuffled[1];
            foreach (var player in here) _names[Id(player)] = player.PlayerName;
            Announce($"Captains: {Colorize("{green}")}{Name(_captain1)}{Colorize("{default}")} and "
                + $"{Colorize("{green}")}{Name(_captain2)}{Colorize("{default}")} - they knife for first pick on the new map.");
        }

        bool alreadyOnIt = !winner.IsWorkshop &&
                           Server.MapName.Equals(winner.Name, StringComparison.OrdinalIgnoreCase);
        if (alreadyOnIt)
        {
            Announce("Already on that map.");
            AddTimer(3.0f, () => StartDraft(force: true));
            return;
        }

        _draftPendingMapChange = true;
        Announce("Changing map - reconnect and wait, the knife starts when everyone is back.");
        AddTimer(5.0f, () => ChangeMap(winner));
    }

    private static void ChangeMap(MapEntry map)
    {
        if (map.IsWorkshop) Server.ExecuteCommand($"host_workshop_map \"{map.WorkshopId}\"");
        else Server.ExecuteCommand($"changelevel \"{map.Name}\"");
    }

    private void ResetVote()
    {
        _voteTimer?.Kill();
        _voteTimer = null;
        _votes.Clear();
        _ballot = [];
        _voteSecondsLeft = 0;
    }
}
