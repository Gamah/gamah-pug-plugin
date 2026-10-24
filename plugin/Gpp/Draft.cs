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
    // Everyone in the draft pool, in the order they entered it. A player's number is their place in
    // this list plus one, and it does not change for the life of the draft: a list that renumbers itself
    // after every pick is how a captain types !3 and gets somebody else. Picked players keep their
    // number and drop off the list; late joiners get the next number along; a player who leaves has
    // their number retired rather than handed to the next person.
    private readonly List<ulong> _pool = [];

    // In the pool, but gone from the server.
    private readonly HashSet<ulong> _gone = [];
    private readonly Dictionary<ulong, string> _names = [];

    private ulong _captain1;
    private ulong _captain2;

    // How many of the players from before a map change were bots, so a test run can ask the engine for
    // the same number again on the new map. Nothing counts the humans: a map change does not disconnect
    // anyone, so a headcount from the old map is a list of people who may simply not be playing this one.
    private int _expectedBots;

    // Who those players were, by name, so the reconnect panel can say who the room is waiting on rather
    // than only how many. A headcount tells nobody whether to go and poke someone in Discord.
    private readonly Dictionary<ulong, string> _expectedRoster = [];
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

    private bool IsUndrafted(ulong steamId) =>
        !_gone.Contains(steamId) && !_team1.Contains(steamId) && !_team2.Contains(steamId);

    private List<ulong> Undrafted() => _pool.Where(IsUndrafted).ToList();

    private int UndraftedCount() => _pool.Count(IsUndrafted);

    // The pickable players with the numbers a captain actually types.
    private List<(int Number, ulong Id)> PickList() =>
        _pool.Select((steamId, index) => (index + 1, steamId))
            .Where(entry => IsUndrafted(entry.Item2))
            .ToList();

    private void StartWaitingForPlayers()
    {
        _waitTimer?.Kill();
        _waitSecondsLeft = Math.Max(5, Config.ReconnectWaitSeconds);
        Announce("Waiting for the captains to load - the knife starts as soon as both are in.");
        _waitTimer = AddTimer(1.0f, WaitTick, TimerFlags.REPEAT);
    }

    // After a map change, the only people worth waiting for are the two captains.
    //
    // This used to wait for the whole room to be back, which was wrong in both directions: a map change
    // does not disconnect anybody, so the headcount was satisfied the instant the map loaded and the duel
    // started while people were still looking at a loading screen - a captain on a slow machine could
    // lose first pick without ever seeing it. And when somebody genuinely had gone, it waited out the
    // whole ninety seconds for them.
    //
    // Nobody needs to be spawned or warmed up for this. Everyone who is not a captain is held in
    // spectator, which is where the duel wants them anyway, and the pick list is what they are waiting
    // for rather than the map.
    private void WaitTick()
    {
        _waitSecondsLeft--;

        var first = PlayerBySteamId(_captain1);
        var second = PlayerBySteamId(_captain2);

        if (Config.ForceSpectate)
        {
            foreach (var player in HumanPlayers())
            {
                if (player.IsBot) continue;
                if (Id(player) == _captain1 || Id(player) == _captain2) continue;
                if (player.Team == CsTeam.Spectator) continue;
                HoldInSpectator(player);
            }
        }

        // A bot captain does not come back from a map change: the engine kicks the lot and the top-up
        // adds new ones under new names, so holding the room for "Bank" to load is holding it for
        // somebody who no longer exists. Hand the armband on now rather than at the end of the clock.
        if (first == null && IsBotId(_captain1))
        {
            _captain1 = ReplaceCaptain(HumanPlayers().Select(Id).ToList(), _captain2);
            first = PlayerBySteamId(_captain1);
        }
        if (second == null && IsBotId(_captain2))
        {
            _captain2 = ReplaceCaptain(HumanPlayers().Select(Id).ToList(), _captain1);
            second = PlayerBySteamId(_captain2);
        }

        if (first != null && second != null)
        {
            Done();
            return;
        }

        if (_waitSecondsLeft > 0)
        {
            WorldWaitPanel();
            if (_waitSecondsLeft % 15 == 0)
            {
                Announce($"Still waiting on {MissingCaptains()} - {_waitSecondsLeft}s.");
            }
            return;
        }

        // Out of time with an armband nobody is wearing. Somebody here takes it: whoever was in the room
        // that voted this map in, if any of them are still around, and anybody at all if not. An armband
        // on a player who is not coming back is a pug that never starts.
        var here = HumanPlayers().Select(Id).ToList();
        if (first == null) _captain1 = ReplaceCaptain(here, _captain2);
        if (second == null || _captain2 == _captain1) _captain2 = ReplaceCaptain(here, _captain1);

        if (_captain1 == 0 || _captain2 == 0 || _captain1 == _captain2)
        {
            Announce("Not enough players here to pick two captains - an admin can !pug start again.");
            _waitTimer?.Kill();
            _waitTimer = null;
            return;
        }

        Announce($"Gave up waiting: {Colorize("{green}")}{Name(_captain1)}{Colorize("{default}")} and "
            + $"{Colorize("{green}")}{Name(_captain2)}{Colorize("{default}")} are the captains.");
        Done();

        void Done()
        {
            ClearWorldPanels();
            _waitTimer?.Kill();
            _waitTimer = null;
            _joinWindowSeconds = Math.Max(0, _waitSecondsLeft);
            // force, because the headcount was checked when the pug started and the room has not changed
            // since: waiting on min_players here would stop a pug that is already under way.
            AfterTick(() => StartDraft(force: true));
        }
    }

    private string MissingCaptains()
    {
        var missing = new List<string>();
        if (PlayerBySteamId(_captain1) == null) missing.Add(Name(_captain1));
        if (PlayerBySteamId(_captain2) == null) missing.Add(Name(_captain2));
        return missing.Count == 0 ? "nobody" : string.Join(" and ", missing);
    }

    // The people who voted this map in come first - they were here a minute ago and chose to be - and
    // anybody else present after that.
    private ulong ReplaceCaptain(List<ulong> here, ulong other)
    {
        var fromThePool = here
            .Where(steamId => steamId != other && _expectedRoster.ContainsKey(steamId))
            .OrderBy(_ => _rng.Next())
            .ToList();
        if (fromThePool.Count > 0) return fromThePool[0];

        return DrawCaptain(here, other);
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
            // A test run with two bot captains is a draft nobody can take part in; this hands one of the
            // armbands to a human and kicks the bot that had it.
            DropOneBotCaptain();
            if (_captain2 == 0) _captain2 = DrawCaptain(ids, _captain1);
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
        // Everybody stays on the field for the draft, drafted or not, and is frozen there - see
        // BelongsOnTheField. Holding the pool in spectator is what left them with no overlay at all:
        // the server cannot read a spectator's camera, so there is nowhere to hang their boards.
        if (Config.ForceSpectate)
        {
            int side = 0;
            foreach (var player in players.Where(player => !player.IsBot))
            {
                if (Id(player) == keep1 || Id(player) == keep2) continue;
                PutOnTheField(player, side++ % 2 == 0 ? CsTeam.CounterTerrorist : CsTeam.Terrorist);
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
        _pool.AddRange(pool);

        int picks = autoSized ? UndraftedCount() : Math.Min(UndraftedCount(), (_playersPerTeam * 2) - 2);
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

        // Armed before the first prompt, never after it. PromptPick can finish the draft in this same
        // frame - two players are two captains and nothing to pick - and FinishDraft tears these three
        // down. Arming them afterwards left the sweep running on an Idle server, putting MOVETYPE_NONE
        // back on every human once a second: the sides knife and the match that followed were fought
        // standing still.
        StartHud();
        StartSpectateGuard();

        // Locked where they spawned for as long as the draft runs: nobody reading a pick list has any
        // reason to be anywhere, and somebody wandering off during the draft is somebody whose boards
        // are behind them by the time they look up. Re-applied every second because being picked moves
        // a player to their team, and being moved spawns them again with movement back on.
        _lockTimer?.Kill();
        _lockTimer = AddTimer(1.0f, KeepDraftersStill, TimerFlags.REPEAT);

        PromptPick();
    }

    private CounterStrikeSharp.API.Modules.Timers.Timer? _lockTimer;

    // Once a second for the whole draft: everybody on a team, alive, and standing still.
    //
    // Doing it once at the start does not hold. ChangeTeam does not hand back a pawn in the same frame,
    // so the respawn that followed it had nothing to respawn - which is how somebody moved out of
    // spectator was still spectating a bot a minute later. Being picked moves a player again, and that
    // move spawns them with movement back on. So this is a sweep rather than a step: it fixes whatever
    // it finds, every second, until the draft ends.
    private void KeepDraftersStill()
    {
        // Nothing outside the draft is held still by this, whoever forgot to kill the timer: the
        // spectate guard stops itself the same way, for the same reason.
        if (_phase != PugPhase.Draft)
        {
            StopDraftLock();
            return;
        }

        int side = 0;
        foreach (var player in HumanPlayers())
        {
            if (player.IsBot) continue;

            if (player.Team != CsTeam.CounterTerrorist && player.Team != CsTeam.Terrorist)
            {
                PutOnTheField(player, side++ % 2 == 0 ? CsTeam.CounterTerrorist : CsTeam.Terrorist);
                continue;
            }

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.Health <= 0)
            {
                PutOnTheField(player, player.Team);
                continue;
            }

            SetMovement(pawn, MoveType_t.MOVETYPE_NONE);
        }
    }

    // Killing the sweep is half of letting go, and the half that was missed: it sets MOVETYPE_NONE on
    // every human once a second, so a draft whose timer outlives it is a match where the client is
    // corrected a second after every step - which reads as the boards lagging the camera.
    private void StopDraftLock()
    {
        _lockTimer?.Kill();
        _lockTimer = null;
        LockDrafters(false);
    }

    // Everybody in the draft held still, or let go. Movement only - they can still look around, which is
    // what the boards follow.
    private void LockDrafters(bool locked)
    {
        foreach (var player in HumanPlayers())
        {
            if (player.IsBot) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.Health <= 0) continue;

            SetMovement(pawn, locked ? MoveType_t.MOVETYPE_NONE : MoveType_t.MOVETYPE_WALK);
        }
    }

    // Both fields, or neither works. m_MoveType is not what the movement code reads: set to
    // MOVETYPE_NONE on its own it leaves m_nActualMoveType at MOVETYPE_WALK and the player keeps
    // walking, so the freeze becomes an argument between the client and the server for as long as the
    // draft lasts. Measured on a bot: m_MoveType alone left it moving at 125 u/s, both stopped it dead.
    private static void SetMovement(CBasePlayerPawn pawn, MoveType_t wanted)
    {
        if (pawn.MoveType == wanted && pawn.ActualMoveType == wanted) return;

        pawn.MoveType = wanted;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
        pawn.ActualMoveType = wanted;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_nActualMoveType");
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

        // Renumbering is fine here and nowhere else: a reroll is only allowed before the first pick.
        var pool = Undrafted().Concat([_captain1, _captain2]).OrderBy(_ => _rng.Next()).ToList();
        _captain1 = pool[0];
        _captain2 = pool[1];
        _team1.Clear();
        _team2.Clear();
        _team1.Add(_captain1);
        _team2.Add(_captain2);
        _pool.Clear();
        _gone.Clear();
        _pool.AddRange(pool.Skip(2));

        Announce($"New captains: {Colorize("{green}")}{Name(_captain1)}{Colorize("{default}")} (CT) and {Colorize("{green}")}{Name(_captain2)}{Colorize("{default}")} (T).");
        MoveToTeam(_captain1, CsTeam.CounterTerrorist);
        MoveToTeam(_captain2, CsTeam.Terrorist);
        PromptPick();
    }

    private ulong CurrentCaptain() => _pickOrder[_pickIndex] == 0 ? _captain1 : _captain2;

    private void PrintAvailable(CCSPlayerController? only = null)
    {
        var pickable = PickList();
        if (pickable.Count == 0)
        {
            Announce("Nobody left undrafted.");
            return;
        }

        string header = $"Undrafted ({pickable.Count}) - pick with !1, !2, ...:";
        var lines = pickable
            .Select(entry => $"{Colorize("{green}")}{entry.Number}{Colorize("{default}")} {Name(entry.Id)}")
            .ToList();

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

        if (_pickIndex >= _pickOrder.Count || UndraftedCount() == 0)
        {
            FinishDraft();
            return;
        }

        // The last player in the pool is not a choice. They go to whoever is on the clock - which is what
        // keeps uneven teams even-handed, since the pick order decides who gets the odd one - rather than
        // leaving somebody as the last name on a screen waiting to be picked.
        var pickable = Undrafted();
        if (pickable.Count == 1)
        {
            Announce($"{Colorize("{green}")}{Name(pickable[0])}{Colorize("{default}")} goes to "
                + $"{Name(CurrentCaptain())} - last in the pool.");
            ulong last = pickable[0];
            AfterTick(() => ApplyPick(last));
            return;
        }

        var captain = PlayerBySteamId(CurrentCaptain());
        Prompt($"{Colorize("{green}")}{Name(CurrentCaptain())}{Colorize("{default}")} picks ({UndraftedCount()} left).",
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

        var pickable = Undrafted();
        if (pickable.Count == 0)
        {
            AfterTick(FinishDraft);
            return;
        }
        var pick = pickable[_rng.Next(pickable.Count)];
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
        // The number is a place in the pool, not a place in what is left of it.
        if (int.TryParse(arg, out int number) && number >= 1 && number <= _pool.Count)
        {
            ulong numbered = _pool[number - 1];
            return IsUndrafted(numbered) ? numbered : 0;
        }

        var undrafted = Undrafted();
        var exact = undrafted.Where(s => Name(s).Equals(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];

        var partial = undrafted.Where(s => Name(s).Contains(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : 0;
    }

    private void ApplyPick(ulong steamId)
    {
        // Two things can pick at once: the captain typing a number and the clock running out on the same
        // frame, both of which hand their work to the next frame. Without this the same player is added
        // to both teams and the pick order advances twice, which is what "picked twice" looks like from
        // the chat. Whoever gets here first wins and the other is a no-op.
        if (_pickIndex >= _pickOrder.Count || !IsUndrafted(steamId)) return;
        _pickTimer?.Kill();
        _pickTimer = null;
        _pickSecondsLeft = 0;

        int team = _pickOrder[_pickIndex];
        // Not removed from the pool: being on a team is what takes a player off the list, and their
        // number stays theirs so nobody else inherits it.
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
        _pickTimer?.Kill();
        _pickTimer = null;
        _waitTimer?.Kill();
        _waitTimer = null;
        StopHud();
        StopSpectateGuard();
        StopDraftLock();

        // Anyone left over (a short pick order, or someone who turned up late) fills the smaller team. Only
        // a pinned team size leaves people in spectator; an auto-sized draft puts everyone in the match.
        bool pinned = _teamSizeOverride > 0 || Config.PlayersPerTeam > 0;
        foreach (var steamId in Undrafted())
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
        }

        Announce($"CT: {string.Join(", ", _team1.Select(Name))}");
        Announce($"T: {string.Join(", ", _team2.Select(Name))}");
        if (UndraftedCount() > 0)
        {
            Announce($"Not drafted: {string.Join(", ", Undrafted().Select(Name))}");
        }

        // Whoever is left over was put in spectator by the pug, not by choice. The draft is done with
        // them, so give them the team menu back rather than leaving them there for the whole match.
        ReleaseHeldSpectators();

        _phase = PugPhase.Idle;
        _teamSizeOverride = 0;
        _knifeWinner = 0;
        HandOffToMatch();
    }

    // The draft used to hold here until the headcount from before the map change was back in the room,
    // and that was wrong twice over. A map change disconnects nobody, so the people who are missing are
    // usually people who voted for a map and then decided not to play it - the draft waited on players who
    // were never coming, and the room sat on a "7 of 8" panel. And the hold ran its own repeat timer whose
    // tick called PromptPick directly, so when the window closed the draft was finished from inside a
    // timer tick: FinishDraft kills the pick timer, the HUD, the spectate guard and the draft lock, and
    // killing another repeat timer from inside a tick corrupts CSS's timer walk and segfaults the server a
    // frame or two later (see the comment on PickTick, and commit c306e81). Whoever is in the room when
    // the picks run out is the match; anyone who loads in later joins the pool if there are picks left.
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

        if (_pool.Contains(steamId) && IsUndrafted(steamId))
        {
            // Retired, not removed: the numbers after theirs must not all shift up one.
            _gone.Add(steamId);
            Announce($"{Name(steamId)} left the draft pool.");
            // The pool may now be shorter than the remaining picks.
            if (_pickOrder.Count > _pickIndex + UndraftedCount())
            {
                _pickOrder = _pickOrder.Take(_pickIndex + UndraftedCount()).ToList();
            }
            if (UndraftedCount() == 0) FinishDraft();
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
        return $"Draft running - {Name(CurrentCaptain())} to pick, {UndraftedCount()} undrafted, CT {_team1.Count} / T {_team2.Count}.";
    }

    private void ResetDraft()
    {
        // Whatever ended the draft - a finished pick order, !pug cancel, !pug stop - nobody is left
        // standing still because of it.
        StopDraftLock();
        StopHud();
        StopReadyPhase();
        StopSpectateGuard();
        _pickTimer?.Kill();
        _pickTimer = null;
        _waitTimer?.Kill();
        _waitTimer = null;
        _team1.Clear();
        _team2.Clear();
        _pool.Clear();
        _gone.Clear();
        _pickOrder = [];
        _pickIndex = 0;
        _captain1 = 0;
        _captain2 = 0;
    }
}
