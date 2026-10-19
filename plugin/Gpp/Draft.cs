using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

public partial class Gpp
{
    private MapEntry? _votedMap;

    // Team size for the draft in progress, and the one-shot override from !pug draft <n>.
    private int _playersPerTeam;
    private int _teamSizeOverride;

    private readonly List<ulong> _team1 = [];
    private readonly List<ulong> _team2 = [];
    private readonly List<ulong> _available = [];
    private readonly Dictionary<ulong, string> _names = [];

    private ulong _captain1;
    private ulong _captain2;

    // Headcount from before a map change, and the wait for those players to come back. How many of them
    // were bots, so a test run can ask the engine for the same number again on the new map.
    private int _expectedPlayers;
    private int _expectedBots;
    private int _waitSecondsLeft;

    // Seconds of the reconnect window left over when the draft started early, and the moment it closes.
    // Until then latecomers join the pool and the teams do not lock.
    private int _joinWindowSeconds;
    private DateTime _joinsOpenUntil;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _waitTimer;

    private List<int> _pickOrder = [];
    private int _firstPick;
    private int _pickIndex;
    private int _pickSecondsLeft;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _pickTimer;

    private void StartWaitingForPlayers()
    {
        _waitTimer?.Kill();
        _waitSecondsLeft = Math.Max(5, Config.ReconnectWaitSeconds);
        int here = HumanPlayers().Count;
        if (_expectedPlayers > here)
        {
            Announce($"Waiting for players to reconnect: {here}/{_expectedPlayers}.");
        }
        _waitTimer = AddTimer(1.0f, WaitTick, TimerFlags.REPEAT);
    }

    private void WaitTick()
    {
        int here = HumanPlayers().Count;
        bool everyoneBack = here >= _expectedPlayers;
        _waitSecondsLeft--;

        // Waiting means waiting for the people we know are coming, not for a quorum. Starting as soon as
        // min_players were back took the armband off a captain who was still loading and sized the teams
        // from half the room; reconnect_wait_seconds is the limit, not the headcount.
        if (!everyoneBack && _waitSecondsLeft > 0)
        {
            if (_waitSecondsLeft % 15 == 0)
            {
                Announce($"Waiting: {here}/{_expectedPlayers} back, {_waitSecondsLeft}s.");
            }
            return;
        }

        _waitTimer?.Kill();
        _waitTimer = null;
        bool enough = here >= Math.Max(2, Config.MinPlayers);
        if (!everyoneBack && !enough)
        {
            Announce($"Gave up waiting - {here} of {_expectedPlayers} are back.");
        }
        else if (!everyoneBack)
        {
            Announce($"Starting with {here} of {_expectedPlayers} - anyone else who joins goes in the pool.");
        }

        _joinWindowSeconds = Math.Max(0, _waitSecondsLeft);
        // min_players still applies: better to stop and let an admin decide than to draft a 1v1 by accident.
        AfterTick(() => StartDraft(force: false));
    }

    private void StartDraft(bool force, bool skipMatchCheck = false)
    {
        if (_phase != PugPhase.Idle)
        {
            Announce($"Already busy: {Status()}");
            return;
        }

        var players = HumanPlayers();
        if (!force && players.Count < Config.MinPlayers)
        {
            Announce($"Need {Config.MinPlayers} players to draft ({players.Count} here). Use !pug draft force to override.");
            return;
        }
        if (players.Count < 2)
        {
            Announce("Need at least 2 players to pick captains.");
            return;
        }

        // Pinned by !pug draft <n>, else by the config, else sized from whoever turned up.
        // Auto-sizing rounds up, so an odd headcount drafts uneven teams instead of shutting somebody out:
        // 5 players is a 3v2, not a 2v2 with a spectator. A pinned size still caps the teams.
        bool autoSized = _teamSizeOverride == 0 && Config.PlayersPerTeam == 0;
        int wanted = _teamSizeOverride > 0 ? _teamSizeOverride
            : Config.PlayersPerTeam > 0 ? Config.PlayersPerTeam
            : (players.Count + 1) / 2;
        _playersPerTeam = autoSized ? wanted : Math.Max(1, Math.Min(wanted, players.Count / 2));

        if (!skipMatchCheck)
        {
            if (!MatchIsClear(force, out bool waitForEnd)) return;
            if (waitForEnd)
            {
                AddTimer(3.0f, () => StartDraft(force, skipMatchCheck: true));
                return;
            }
        }

        // Captains knife 1v1 for first pick before anyone is drafted. EndDuel comes back in here with a
        // winner, so this runs once per draft.
        if (Config.CaptainKnife && _knifeWinner == 0)
        {
            // One captain missing is one armband to reassign. Re-rolling both took the armband off a
            // captain who was standing there, voted in before the map change.
            var ids = players.Select(Id).ToList();
            if (_captain1 == 0 || !ids.Contains(_captain1)) _captain1 = DrawCaptain(ids, _captain2);
            if (_captain2 == 0 || _captain2 == _captain1 || !ids.Contains(_captain2))
            {
                _captain2 = DrawCaptain(ids, _captain1);
            }
            if (StartCaptainKnife()) return;
        }

        // Captains are chosen before the map change (and the duel is fought with them), so carry them
        // through ResetDraft rather than handing the armbands to whoever reconnected first.
        ulong keep1 = _captain1;
        ulong keep2 = _captain2;
        ulong duelWinner = _knifeWinner;

        ClearFunEffects();
        ResetDraft();
        _phase = PugPhase.Draft;
        _joinsOpenUntil = DateTime.UtcNow.AddSeconds(_joinWindowSeconds);
        _joinWindowSeconds = 0;

        foreach (var player in players) _names[Id(player)] = player.PlayerName;

        // Round held and warmup off for the same reason as the vote: the overlay is the draft, and nobody
        // should be running around in it.
        HoldTheRound();
        if (Config.ForceSpectate)
        {
            foreach (var player in players.Where(player => !player.IsBot))
            {
                if (Id(player) == keep1 || Id(player) == keep2) continue;
                HoldInSpectator(player);
            }
        }

        var pool = players.Select(Id).OrderBy(_ => _rng.Next()).ToList();
        _captain1 = keep1;
        _captain2 = keep2;
        if (_captain1 == _captain2 || !pool.Contains(_captain1) || !pool.Contains(_captain2))
        {
            _captain1 = pool[0];
            _captain2 = pool.First(steamId => steamId != _captain1);
        }
        pool.Remove(_captain1);
        pool.Remove(_captain2);
        _team1.Add(_captain1);
        _team2.Add(_captain2);
        _available.AddRange(pool);

        int picks = autoSized ? _available.Count : Math.Min(_available.Count, (_playersPerTeam * 2) - 2);
        if (picks == 0)
        {
            Announce($"Nothing to draft at {_playersPerTeam}v{_playersPerTeam} - the captains are the teams.");
        }
        _firstPick = duelWinner == _captain2 ? 1 : 0;
        _pickOrder = BuildPickOrder(picks, Config.PickOrder, _firstPick);
        _pickIndex = 0;

        Announce($"{_playersPerTeam}v{_playersPerTeam}. Captains: {Colorize("{green}")}{Name(_captain1)}"
            + $"{Colorize("{default}")} (CT) and {Colorize("{green}")}{Name(_captain2)}{Colorize("{default}")} (T).");
        MoveToTeam(_captain1, CsTeam.CounterTerrorist);
        MoveToTeam(_captain2, CsTeam.Terrorist);

        PromptPick();
        StartHud();
        StartSpectateGuard();
    }

    // A captain from the room, never the one already wearing the other armband.
    private ulong DrawCaptain(List<ulong> ids, ulong other)
    {
        var pick = ids.Where(steamId => steamId != other).OrderBy(_ => _rng.Next()).ToList();
        return pick.Count > 0 ? pick[0] : 0;
    }

    // first is 0 for captain1 or 1 for captain2 - whoever won the knife duel picks first, and the order
    // runs from there.
    private static List<int> BuildPickOrder(int picks, string style, int first)
    {
        var order = new List<int>();
        for (int i = 0; i < picks; i++)
        {
            int side = style == "snake" ? ((i + 1) / 2) % 2 : i % 2;
            order.Add(side ^ first);
        }
        return order;
    }

    // !pug captains <a> <b>, or !pug captains humans - pin who wears the armbands instead of drawing
    // them. Set while idle it is what the next draft starts from (StartDraft keeps captains who are
    // still connected); set during a draft before any pick has been made it takes effect there and then.
    //
    // "humans" exists for a test run: with count_bots on, the draw is over bots as well, and a bot
    // captain neither knifes nor picks - so a night with two people on the server and eight bots in the
    // pool has to be able to say "us two".
    private void SetCaptains(CommandInfo command, string first, string second)
    {
        if (_phase == PugPhase.CaptainKnife)
        {
            Reply(command, "The captains are already knifing - !pug cancel first.");
            return;
        }
        if (_phase == PugPhase.Draft && (_team1.Count > 1 || _team2.Count > 1))
        {
            Reply(command, "Picks have already been made - !pug cancel and start again.");
            return;
        }

        ulong one;
        ulong two;
        if (first == "humans")
        {
            var humans = HumanPlayers().Where(player => !player.IsBot).Select(Id).ToList();
            if (humans.Count < 2)
            {
                Reply(command, $"Need two humans on the server for that ({humans.Count} here).");
                return;
            }
            // More than two and it is a draw among them, which is still the thing being asked for.
            var drawn = humans.OrderBy(_ => _rng.Next()).ToList();
            one = drawn[0];
            two = drawn[1];
        }
        else
        {
            one = ResolvePlayer(first);
            two = ResolvePlayer(second);
            if (one == 0) { Reply(command, $"No single player matches \"{first}\"."); return; }
            if (two == 0) { Reply(command, $"No single player matches \"{second}\"."); return; }
            if (one == two) { Reply(command, "Those are the same player."); return; }
        }

        _captain1 = one;
        _captain2 = two;
        foreach (var steamId in new[] { one, two }) _names[steamId] = Name(steamId);

        if (_phase == PugPhase.Draft)
        {
            _team1.Clear();
            _team2.Clear();
            _team1.Add(_captain1);
            _team2.Add(_captain2);
            _available.RemoveAll(steamId => steamId == _captain1 || steamId == _captain2);
            MoveToTeam(_captain1, CsTeam.CounterTerrorist);
            MoveToTeam(_captain2, CsTeam.Terrorist);
            Announce($"Captains: {Colorize("{green}")}{Name(_captain1)}{Colorize("{default}")} (CT) and "
                + $"{Colorize("{green}")}{Name(_captain2)}{Colorize("{default}")} (T).");
            PromptPick();
            return;
        }

        Announce($"Captains pinned: {Colorize("{green}")}{Name(_captain1)}{Colorize("{default}")} and "
            + $"{Colorize("{green}")}{Name(_captain2)}{Colorize("{default}")} - they knife for first pick.");
    }

    // Any connected player, by exact then partial name. 0 when nothing or more than one matches.
    private ulong ResolvePlayer(string arg)
    {
        var players = HumanPlayers();
        var exact = players.Where(p => p.PlayerName.Equals(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return Id(exact[0]);

        var partial = players.Where(p => p.PlayerName.Contains(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? Id(partial[0]) : 0;
    }

    private void RerollCaptains(CommandInfo command)
    {
        if (_phase != PugPhase.Draft)
        {
            Reply(command, "No draft is running.");
            return;
        }
        if (_team1.Count > 1 || _team2.Count > 1)
        {
            Reply(command, "Picks have already been made - !pug cancel and start again.");
            return;
        }

        var pool = _available.Concat([_captain1, _captain2]).OrderBy(_ => _rng.Next()).ToList();
        _captain1 = pool[0];
        _captain2 = pool[1];
        _team1.Clear();
        _team2.Clear();
        _team1.Add(_captain1);
        _team2.Add(_captain2);
        _available.Clear();
        _available.AddRange(pool.Skip(2));

        Announce($"New captains: {Colorize("{green}")}{Name(_captain1)}{Colorize("{default}")} (CT) and {Colorize("{green}")}{Name(_captain2)}{Colorize("{default}")} (T).");
        MoveToTeam(_captain1, CsTeam.CounterTerrorist);
        MoveToTeam(_captain2, CsTeam.Terrorist);
        PromptPick();
    }

    private ulong CurrentCaptain() => _pickOrder[_pickIndex] == 0 ? _captain1 : _captain2;

    private void PrintAvailable(CCSPlayerController? only = null)
    {
        if (_available.Count == 0)
        {
            Announce("Nobody left undrafted.");
            return;
        }

        string header = $"Undrafted ({_available.Count}) - pick with !1, !2, ...:";
        var lines = new List<string>();
        for (int i = 0; i < _available.Count; i++)
        {
            lines.Add($"{Colorize("{green}")}{i + 1}{Colorize("{default}")} {Name(_available[i])}");
        }

        // Three to a line: one chat line per player buries the prompt on a 10-man.
        var chunks = lines
            .Select((line, index) => (line, index))
            .GroupBy(entry => entry.index / 3)
            .Select(group => string.Join("   ", group.Select(entry => entry.line)))
            .ToList();

        if (only != null && only.IsValid)
        {
            only.PrintToChat(Prefix() + header);
            foreach (var chunk in chunks) only.PrintToChat(Prefix() + chunk);
            return;
        }

        Announce(header);
        foreach (var chunk in chunks) Announce(chunk);
    }

    [ConsoleCommand("css_players", "Show the undrafted players and their numbers")]
    public void OnPlayersCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (_phase != PugPhase.Draft)
        {
            command.ReplyToCommand("No draft is running.");
            return;
        }
        PrintAvailable(player);
    }

    private void PromptPick()
    {
        _pickTimer?.Kill();
        _pickTimer = null;

        if (_pickIndex >= _pickOrder.Count || _available.Count == 0)
        {
            FinishDraft();
            return;
        }

        // A hold for latecomers is over as soon as there is a real pick to make.
        _waitTimer?.Kill();
        _waitTimer = null;

        var captain = PlayerBySteamId(CurrentCaptain());
        Prompt($"{Colorize("{green}")}{Name(CurrentCaptain())}{Colorize("{default}")} picks ({_available.Count} left).",
            "pick now - !1, !2, ...", $"{Name(CurrentCaptain())} only");
        // With the HUD up the captains already have the list on screen; chat only repeats it without one.
        if (!Config.Hud)
        {
            PrintAvailable();
            captain?.PrintToChat(Prefix() + $"Your pick: {Colorize("{green}")}!1, !2, ...{Colorize("{default}")} - !players to list again");
        }

        // A bot captain has no way to type !draft, so its pick is the timeout. Waiting out the full clock
        // for every one of them turns a test run into five minutes of nothing.
        bool botOnClock = IsBotId(CurrentCaptain());
        if (Config.PickSeconds > 0 || botOnClock)
        {
            _pickSecondsLeft = botOnClock ? 3 : Config.PickSeconds;
            _pickTimer = AddTimer(1.0f, PickTick, TimerFlags.REPEAT);
        }
    }

    private void PickTick()
    {
        _pickSecondsLeft--;
        if (_pickSecondsLeft > 0) return;

        var pick = _available[_rng.Next(_available.Count)];
        Announce($"Out of time - {Name(pick)} picked at random.");
        AfterTick(() => ApplyPick(pick));
    }

    [ConsoleCommand("css_draft", "Pick a player during a GPP draft")]
    [CommandHelper(minArgs: 0, usage: "<number|name>")]
    public void OnPickCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid) return;
        if (command.ArgCount < 2 && _phase == PugPhase.Draft && Id(player) == CurrentCaptain())
        {
            PrintAvailable(player);
            return;
        }
        MakePick(player, command.ArgCount > 1 ? command.ArgByIndex(1).Trim() : "");
    }

    // Shared by !draft <n|name> and the bare !<n>.
    internal void MakePick(CCSPlayerController player, string arg)
    {
        if (_phase != PugPhase.Draft)
        {
            player.PrintToChat(Prefix() + "No draft is running.");
            return;
        }
        if (_pickIndex >= _pickOrder.Count)
        {
            player.PrintToChat(Prefix() + "The draft is over.");
            return;
        }
        if (Id(player) != CurrentCaptain())
        {
            player.PrintToChat(Prefix() + $"Not your pick - it is {Name(CurrentCaptain())}'s.");
            return;
        }

        if (string.IsNullOrEmpty(arg))
        {
            PrintAvailable(player);
            return;
        }

        ulong pick = ResolvePick(arg);
        if (pick == 0)
        {
            player.PrintToChat(Prefix() + $"No single undrafted player matches \"{arg}\".");
            return;
        }

        ApplyPick(pick);
    }

    private ulong ResolvePick(string arg)
    {
        if (int.TryParse(arg, out int number) && number >= 1 && number <= _available.Count) return _available[number - 1];

        var exact = _available.Where(s => Name(s).Equals(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];

        var partial = _available.Where(s => Name(s).Contains(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : 0;
    }

    private void ApplyPick(ulong steamId)
    {
        int team = _pickOrder[_pickIndex];
        _available.Remove(steamId);
        if (team == 0)
        {
            _team1.Add(steamId);
            MoveToTeam(steamId, CsTeam.CounterTerrorist);
        }
        else
        {
            _team2.Add(steamId);
            MoveToTeam(steamId, CsTeam.Terrorist);
        }

        Announce($"{Name(CurrentCaptain())} picked {Colorize("{green}")}{Name(steamId)}{Colorize("{default}")} ({(team == 0 ? "CT" : "T")}).");
        _pickIndex++;
        PromptPick();
    }

    private void FinishDraft()
    {
        if (HoldForLateJoiners()) return;

        _pickTimer?.Kill();
        _pickTimer = null;
        _waitTimer?.Kill();
        _waitTimer = null;
        StopHud();
        StopSpectateGuard();

        // Anyone left over (a short pick order, or someone who turned up late) fills the smaller team. Only
        // a pinned team size leaves people in spectator; an auto-sized draft puts everyone in the match.
        bool pinned = _teamSizeOverride > 0 || Config.PlayersPerTeam > 0;
        foreach (var steamId in _available.ToList())
        {
            bool toTeam1 = _team1.Count <= _team2.Count;
            if (pinned && (toTeam1 ? _team1.Count : _team2.Count) >= _playersPerTeam)
            {
                continue; // the size was pinned: they stay in spectator
            }
            if (toTeam1)
            {
                _team1.Add(steamId);
                MoveToTeam(steamId, CsTeam.CounterTerrorist);
            }
            else
            {
                _team2.Add(steamId);
                MoveToTeam(steamId, CsTeam.Terrorist);
            }
            _available.Remove(steamId);
        }

        Announce($"CT: {string.Join(", ", _team1.Select(Name))}");
        Announce($"T: {string.Join(", ", _team2.Select(Name))}");
        if (_available.Count > 0)
        {
            Announce($"Not drafted: {string.Join(", ", _available.Select(Name))}");
        }

        // Whoever is left over was put in spectator by the pug, not by choice. The draft is done with
        // them, so give them the team menu back rather than leaving them there for the whole match.
        ReleaseHeldSpectators();

        _phase = PugPhase.Idle;
        _teamSizeOverride = 0;
        _knifeWinner = 0;
        HandOffToMatch();
    }

    // Teams must not lock while players we were expecting are still loading in - that is how a 2v2 ends up
    // loaded with everyone else shut out by the whitelist.
    private bool HoldForLateJoiners()
    {
        int here = _team1.Count + _team2.Count + _available.Count;
        if (here >= _expectedPlayers || DateTime.UtcNow >= _joinsOpenUntil) return false;

        _pickTimer?.Kill();
        _pickTimer = null;
        if (_waitTimer == null)
        {
            Announce($"Teams on hold - {here} of {_expectedPlayers} are in. Picking resumes as they connect.");
            _waitTimer = AddTimer(2.0f, () =>
            {
                if (_phase == PugPhase.Draft) PromptPick();
            }, TimerFlags.REPEAT);
        }
        return true;
    }

    private void HandleDraftDisconnect(ulong steamId)
    {
        if (_phase != PugPhase.Draft) return;

        if (steamId == _captain1 || steamId == _captain2)
        {
            bool isTeam1 = steamId == _captain1;
            var roster = isTeam1 ? _team1 : _team2;
            roster.Remove(steamId);

            var replacement = roster.FirstOrDefault(s => PlayerBySteamId(s) != null);
            if (replacement == 0)
            {
                Cancel($"{Name(steamId)} was a captain and left with nobody to take over.");
                return;
            }

            if (isTeam1) _captain1 = replacement;
            else _captain2 = replacement;
            Announce($"{Name(steamId)} left - {Colorize("{green}")}{Name(replacement)}{Colorize("{default}")} is now captain.");
            PromptPick();
            return;
        }

        if (_available.Remove(steamId))
        {
            Announce($"{Name(steamId)} left the draft pool.");
            // The pool may now be shorter than the remaining picks.
            if (_pickOrder.Count > _pickIndex + _available.Count)
            {
                _pickOrder = _pickOrder.Take(_pickIndex + _available.Count).ToList();
            }
            if (_available.Count == 0) FinishDraft();
            else if (PlayerBySteamId(CurrentCaptain()) != null) PromptPick();
            return;
        }

        // A drafted player left. The roster is keyed on SteamID, so they keep their slot if they come back.
        if (_team1.Contains(steamId) || _team2.Contains(steamId))
        {
            Announce($"{Name(steamId)} left - still on their drafted team if they come back.");
        }
    }

    private void MoveToTeam(ulong steamId, CsTeam team)
    {
        var player = PlayerBySteamId(steamId);
        if (player == null || !player.IsValid) return;
        player.ChangeTeam(team);
    }

    private string Name(ulong steamId)
    {
        var player = PlayerBySteamId(steamId);
        if (player != null) _names[steamId] = player.PlayerName;
        return _names.TryGetValue(steamId, out var name) ? name : steamId.ToString();
    }

    private string DraftStatus()
    {
        if (_pickIndex >= _pickOrder.Count) return "Draft finishing up.";
        return $"Draft running - {Name(CurrentCaptain())} to pick, {_available.Count} undrafted, CT {_team1.Count} / T {_team2.Count}.";
    }

    private void ResetDraft()
    {
        StopHud();
        StopReadyPhase();
        StopSpectateGuard();
        _pickTimer?.Kill();
        _pickTimer = null;
        _waitTimer?.Kill();
        _waitTimer = null;
        _team1.Clear();
        _team2.Clear();
        _available.Clear();
        _pickOrder = [];
        _pickIndex = 0;
        _captain1 = 0;
        _captain2 = 0;
    }
}
