using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// What goes on each phase's world panel.
//
// The centre panel had four lines of eighty characters, so every panel was an exercise in what to leave
// out. The world panel has twelve lines and no width budget, and the plugin already knows far more than
// it was able to show: who has not voted, who is still to connect, both teams as they fill, the score.
// So these panels show it. The point of the overlay is to get ten to twenty people to cooperate without
// anyone having to herd them in chat, and "nothing on screen" is the failure it exists to fix.
//
// Two rules hold everywhere:
//   - the title is the instruction, in the imperative, and it is the one line that is never paged away
//   - what a player sees depends on what they still have to do; someone who has voted and someone who
//     has not need different panels, not the same panel with a different word in it
public partial class Gpp
{

    // The panel is up from !pug start until the room is told to move in Discord, and it is deliberately
    // absent for both knife fights: the panel is a solid object in front of the face, and a knife round
    // is the one part of a pug that is aim.
    private void WorldHide() => ClearWorldPanels();

    private void WorldShow(CCSPlayerController player, string panel, string? left = null, string? right = null) =>
        ShowWorldPanel(player, panel, left, right);

    // The two team boards, one to each side. Team A is always the left one for everybody, whatever side
    // they end up on and whoever they are: ten people looking at the same board is what makes "you're on
    // A" mean something out loud, and it is the label the Discord bot will want when it comes to move
    // people into voice.
    //
    // They start as the two captains and grow a line per pick. A name leaving the middle panel and
    // appearing under a captain *is* the feedback that the pick landed - nothing else has to announce it.
    private string TeamBoard(string label, ulong captain, List<ulong> team) =>
        WorldLayout.TeamBoard(label, Name(captain), team.Where(steamId => steamId != captain).Select(Name));

    private string LeftBoard() => TeamBoard(TeamALabel(), _captain1, _team1);

    private string RightBoard() => TeamBoard(TeamBLabel(), _captain2, _team2);

    private string TeamALabel() => "A";

    private string TeamBLabel() => "B";

    // --- captain sign-up ---------------------------------------------------------------------------

    private void WorldOptInPanel()
    {
        _keepCentred = false;
        var volunteers = _volunteers.Where(steamId => PlayerBySteamId(steamId) != null).Select(Name).ToList();
        var quiet = HumanPlayers()
            .Where(player => !player.IsBot && !_volunteers.Contains(Id(player)))
            .Select(player => player.PlayerName)
            .ToList();

        // Both halves are grids rather than lines: a row of names that grows sideways runs out of board
        // and starts clipping, while the board has eight spare lines underneath it doing nothing.
        foreach (var player in HumanPlayers())
        {
            bool mine = _volunteers.Contains(Id(player));
            string panel = WorldLayout.MainPanel(
                mine ? "YOU ARE IN THE CAPTAIN POOL" : "TYPE !captain TO JOIN THE CAPTAIN POOL",
                [
                    WorldLayout.Section.Of(volunteers.Count == 0
                        ? "NOBODY YET - CAPTAINS DRAWN AT RANDOM"
                        : "IN"),
                    WorldLayout.Section.List(WorldLayout.Grid(volunteers.Select(WorldLayout.Clip))),
                ],
                WorldLayout.Footer($"{_optInSecondsLeft}s"),
                _hudTicks);
            WorldShow(player, panel);
        }
    }

    // --- map vote ----------------------------------------------------------------------------------

    private void WorldVotePanel()
    {
        _keepCentred = false;
        var tally = new int[_ballot.Count];
        foreach (var index in _votes.Values) tally[index]++;

        var ballot = _ballot
            .Select((map, index) => WorldLayout.Cell(index + 1,
                map.Label + (tally[index] > 0 ? " " + new string('*', Math.Min(tally[index], 5)) : "")))
            .ToList();

        var voters = HumanPlayers().Where(player => !player.IsBot).ToList();
        var missing = voters.Where(player => !_votes.ContainsKey(Id(player))).Select(player => player.PlayerName).ToList();

        // Somebody who has not voted gets the ballot and nothing else - they cannot act on who else has
        // voted, and a list of names next to the maps is what makes people type the wrong number. Once
        // they have voted the ballot has done its job and the panel turns into the thing they can act
        // on: who to go and shout at.
        foreach (var player in voters)
        {
            string panel = _votes.TryGetValue(Id(player), out int mine)
                // Somebody who has voted gets the whole ballot with its tally against it - every map,
                // every time, so the board is one shape and the stars are the only thing moving - and
                // who is holding the vote up underneath.
                ? WorldLayout.MainPanel($"{_votes.Count}/{voters.Count} VOTED",
                    [
                        new([.. WorldLayout.Grid(Tally(tally))]),
                        WorldLayout.Section.Of(missing.Count == 0 ? null : "STILL VOTING:"),
                        WorldLayout.Section.List(WorldLayout.Grid(missing.Select(WorldLayout.Clip))),
                    ],
                    WorldLayout.Footer($"{_voteSecondsLeft}s"),
                    _hudTicks)
                : WorldLayout.MainPanel("PICK A MAP - TYPE THE NUMBER IN CHAT",
                    [WorldLayout.Section.List(WorldLayout.Grid(ballot))],
                    WorldLayout.Footer($"{_voteSecondsLeft}s"),
                    _hudTicks);
            WorldShow(player, panel);
        }
    }

    // Every map with its votes against it, in ballot order so a map does not move as the votes land -
    // somebody watching for their own vote to appear should not have to find the map again first.
    private List<string> Tally(int[] tally) =>
        _ballot.Select((map, index) =>
            $"{WorldLayout.Clip(map.Label)} {new string('*', Math.Min(tally[index], 5))}".TrimEnd()).ToList();

    // --- waiting for the room to come back after the map change -------------------------------------

    private void WorldWaitPanel()
    {
        // Only the captains hold this up now - everybody else is in spectator waiting to be drafted, and
        // the map is already loaded for them.
        var waiting = new List<string>();
        if (PlayerBySteamId(_captain1) == null) waiting.Add(WorldLayout.Clip(Name(_captain1)));
        if (PlayerBySteamId(_captain2) == null) waiting.Add(WorldLayout.Clip(Name(_captain2)));

        foreach (var player in HumanPlayers())
        {
            // Everyone who is not a captain is in spectator for this, so almost nobody has a pawn to
            // hang a board on. Two lines of alert says the same thing and needs none.
            if (!HasCamera(player))
            {
                HideWorldPanel(player.Slot);
                player.PrintToCenterAlert(Alert("WAITING FOR CAPTAINS",
                    waiting.Count == 1 ? $"{waiting[0]} IS LOADING" : "THEY ARE STILL LOADING"));
                continue;
            }

            _keepCentred = true;
            string panel = WorldLayout.MainPanel("WAITING FOR THE CAPTAINS",
                [
                    WorldLayout.Section.Of(waiting.Count == 0
                        ? "BOTH CAPTAINS ARE IN - KNIFING NOW"
                        : "STILL LOADING"),
                    new([.. WorldLayout.Slots(waiting, 2, 1)]),
                    WorldLayout.Section.Of("CAPTAINS KNIFE FOR FIRST PICK ONCE THEY JOIN"),
                ],
                WorldLayout.Footer($"{_waitSecondsLeft}s"),
                _hudTicks);
            WorldShow(player, panel);
        }
    }

    // --- the draft ---------------------------------------------------------------------------------

    private void WorldDraftPanel()
    {
        _keepCentred = false;
        ulong onClock = CurrentCaptain();
        var pool = PickList().Select(entry => WorldLayout.Cell(entry.Number, Name(entry.Id))).ToList();
        string clock = _pickSecondsLeft > 0 ? $"{_pickSecondsLeft}s" : "";

        // The same board for the whole room, bar its top line. Everyone is watching the same draft, and
        // a pool that only the two captains can see is a room full of people guessing whether they have
        // been picked yet. The one line that differs is the one addressed to whoever has to type.
        // Eighteen slots, which is a twenty-man pug once the captains are out of the pool. They empty
        // as picks land rather than the list closing up, so the board is the same height throughout and
        // the number against a name never moves.
        var body = new List<WorldLayout.Section>
        {
            new([.. WorldLayout.Slots(pool, Math.Max(pool.Count, _pool.Count))]),
        };
        string footer = WorldLayout.Footer(clock);

        string left = LeftBoard();
        string right = RightBoard();

        foreach (var player in HumanPlayers())
        {
            // Nobody to hang a board on - a spectator, or somebody the respawn sweep has not caught yet.
            // Two lines of centre-screen alert instead: who the room is waiting on, and that being
            // picked is what puts them in the game. A board drawn for them lands where their camera
            // is not, which is worse than no board.
            if (!HasCamera(player))
            {
                HideWorldPanel(player.Slot);
                // Thirty characters is the alert box's cap, and it clips rather than wraps: the first version of
                // this line lost its last letter and read "WHEN PICKE".
                player.PrintToCenterAlert(Alert($"{Name(onClock)} IS PICKING", "YOU WILL SPAWN WHEN PICKED"));
                DraftDrawn(player, "alert (no pawn of their own)");
                continue;
            }

            // A centre board is for somebody who has to do something, which is both captains: the one on
            // the clock types a number, and the one waiting is reading the same pool to decide what to
            // type next. Everybody else gets the two team boards - which is where being picked shows up
            // - and an alert naming who the room is waiting on, rather than a list of eighteen names
            // they cannot use.
            if (Id(player) != _captain1 && Id(player) != _captain2)
            {
                WorldShow(player, "", left, right);
                player.PrintToCenterAlert(Alert($"{Name(onClock)} IS PICKING",
                    _pickSecondsLeft > 0 ? $"{_pickSecondsLeft}s" : "YOU WILL SPAWN WHEN PICKED"));
                DraftDrawn(player, "alert and team boards");
                continue;
            }

            string title = Id(player) == onClock
                ? "YOUR PICK - TYPE THE NUMBER"
                : $"{Name(onClock)} IS PICKING";
            WorldShow(player, WorldLayout.MainPanel(title, body, footer, _hudTicks), left, right);
            DraftDrawn(player, Id(player) == onClock ? "boards (on the clock)" : "boards (waiting)");
        }
    }

    // What was drawn for whom, logged when it changes rather than every tick. "I saw nothing" and "the
    // plugin drew nothing" are different problems and this is the line that tells them apart.
    private readonly Dictionary<int, string> _drewForDraft = [];

    private void DraftDrawn(CCSPlayerController player, string what)
    {
        if (_drewForDraft.TryGetValue(player.Slot, out var last) && last == what) return;
        _drewForDraft[player.Slot] = what;

        var pawn = player.PlayerPawn.Value;
        Logger.LogInformation("Draft overlay for {Name}: {What} (team {Team}, pawn {Pawn})",
            player.PlayerName, what, player.Team,
            pawn == null || !pawn.IsValid ? "none" : $"health {pawn.Health}");
    }

    private List<ulong> TeamOf(ulong captain) => captain == _captain1 ? _team1 : _team2;

    private ulong Other(ulong captain) => captain == _captain1 ? _captain2 : _captain1;

    private static string Clip(string name) => WorldLayout.Clip(name);

    // --- ready -------------------------------------------------------------------------------------

    private void WorldReadyPanel()
    {
        _keepCentred = false;
        var roster = ReadyRoster();
        int ready = roster.Count(_readied.Contains);

        // Who is holding it up, named. The marked ten-line roster it replaced was a board of crosses to
        // read through for the one name that mattered.
        var notReady = roster.Where(steamId => !_readied.Contains(steamId)).Select(Name).ToList();
        var absent = AbsentRoster();

        string left = LeftBoard();
        string right = RightBoard();

        foreach (var player in HumanPlayers())
        {
            bool drafted = _roster.ContainsKey(Id(player));
            bool mine = _readied.Contains(Id(player));

            string panel = WorldLayout.MainPanel(
                !drafted ? "MATCH STARTING" : mine ? "YOU ARE READY" : "TYPE .ready IN CHAT",
                [
                    WorldLayout.Section.Of($"{ready}/{roster.Count} READY"),
                    WorldLayout.Section.Of(notReady.Count == 0 ? null : "NOT READY"),
                    WorldLayout.Section.List(WorldLayout.Grid(notReady.Select(WorldLayout.Clip))),
                    // Named, not waited on: the match can start without them, and the room decides
                    // whether to hold for somebody who has gone.
                    WorldLayout.Section.Of(absent.Count == 0 ? null : $"NOT HERE: {string.Join("  ", absent.Select(WorldLayout.Clip))}"),
                ],
                WorldLayout.Footer(drafted ? null : "YOU ARE NOT IN THIS MATCH"),
                _hudTicks);
            WorldShow(player, panel, left, right);
        }
    }

    // --- side choice after the knife round ----------------------------------------------------------

    private void WorldSidePanel()
    {
        _keepCentred = false;
        string left = LeftBoard();
        string right = RightBoard();
        string winner = Name(WinningCaptain());
        string on = Side(_knifeSide);
        string other = Side(OtherSide(_knifeSide));

        foreach (var player in HumanPlayers())
        {
            bool mine = Id(player) == WinningCaptain()
                || (!Config.Match.SideChoiceCaptainOnly && player.Team == _knifeSide);

            // Somebody who cannot answer does not need to be told what the two commands do. They need to
            // know who the room is waiting on and that nothing is being asked of them - the first panel
            // here spelled out both commands to nine people who could not type either.
            // Two lines for the captain who has to type, one line for everybody else. What each command
            // does is only worth saying to the person who can use one.
            string panel = mine
                ? WorldLayout.MainPanel("YOU WON THE KNIFE",
                    [WorldLayout.Section.Of($".stay      STAY ON {on.ToString().ToUpperInvariant()}",
                        $".switch    SWITCH TO {other.ToString().ToUpperInvariant()}")],
                    WorldLayout.Footer($"{_sideSecondsLeft}s - STAYING IF YOU SAY NOTHING"), _hudTicks)
                : WorldLayout.MainPanel($"{winner} WON THE KNIFE",
                    [WorldLayout.Section.Of("THEY CHOOSE TO STAY OR SWITCH")],
                    WorldLayout.Footer($"{_sideSecondsLeft}s"), _hudTicks);
            WorldShow(player, panel, left, right);
        }
    }

    private static CsTeam OtherSide(CsTeam side) =>
        side == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;

    // --- the last panel: move in Discord ------------------------------------------------------------

    private void WorldOpeningPanel()
    {
        _keepCentred = false;
        // The loudest panel of the night, and the last one: after this the match is live and the overlay
        // is gone until the next pug. The three shouted lines are the owner's words, kept exactly.
        var lines = MatchRules.OpeningLines((int)_moveSide);
        var playing = OpeningPlayers();
        var waiting = playing.Where(player => !_openingReady.Contains(Id(player)))
            .Select(player => player.PlayerName)
            .ToList();

        // Whether the team boards earn their place here is the open question: by now everybody knows who
        // they are playing with, and what matters is which voice channel to be in. _openingBoards is what
        // !pug discord switches, so both can be looked at on a screen rather than argued about.
        string? left = _openingBoards ? LeftBoard() : null;
        string? right = _openingBoards ? RightBoard() : null;

        foreach (var player in HumanPlayers())
        {
            bool mine = _openingReady.Contains(Id(player));
            string panel = WorldLayout.MainPanel(lines[0],
                [
                    WorldLayout.Section.Of("STFU AND MOVE IN DISCORD"),
                    WorldLayout.Section.Of("MATCH STARTS WHEN EVERYONE IS READY",
                        $"{playing.Count - waiting.Count}/{playing.Count} READY"),
                    WorldLayout.Section.Of(waiting.Count == 0 ? null : "NOT READY:"),
                    WorldLayout.Section.List(WorldLayout.Grid(waiting.Select(WorldLayout.Clip))),
                ],
                WorldLayout.Footer(mine ? null : "TYPE .ready WHEN YOU ARE IN",
                    $"AUTO STARTING IN {_openingSecondsLeft}s"),
                _hudTicks);
            WorldShow(player, panel, left, right);
        }
    }

    // Three boards at the Discord hold, or just the one. Live, because this is the one placement nobody
    // has an opinion about until they are standing in front of it.
    private bool _openingBoards = true;

    internal string SetOpeningBoards(string arg)
    {
        if (arg is "1" or "one" or "off") _openingBoards = false;
        else if (arg is "3" or "three" or "on") _openingBoards = true;
        else return $"Discord hold shows {(_openingBoards ? "3 boards" : "1 board")} - !pug discord 1 or 3.";

        return $"Discord hold now shows {(_openingBoards ? "3 boards" : "1 board")}.";
    }

    // --- the two panels that are not part of a pug at all --------------------------------------------

    private void WorldIdlePanel()
    {
        _keepCentred = false;
        foreach (var player in HumanPlayers())
        {
            string panel = WorldLayout.MainPanel(
                $"SERVER HAS BEEN IDLE FOR {Describe(_idleWas).ToUpperInvariant()}. PICK ONE:",
                [
                    WorldLayout.Section.Of(
                        $"!RELOAD   LOAD {Server.MapName} AGAIN",
                        "!RESTART  RESTART THE SERVER",
                        "!SKIP     LEAVE IT"),
                ],
                "TYPE ONE OF THEM IN CHAT",
                _hudTicks);
            WorldShow(player, panel);
        }
    }

    private void WorldRestartPanel()
    {
        foreach (var player in HumanPlayers())
        {
            _keepCentred = true;
            string panel = WorldLayout.MainPanel("SERVER RESTARTING",
                [
                    WorldLayout.Section.Of("WHY", _restartWhy.ToUpperInvariant()),
                    WorldLayout.Section.Of("IT COMES BACK ON ITS OWN - RECONNECT TO THE",
                        "SAME ADDRESS."),
                ],
                WorldLayout.Footer($"{_restartSecondsLeft}s"),
                _hudTicks);
            WorldShow(player, panel);
        }
    }
}
