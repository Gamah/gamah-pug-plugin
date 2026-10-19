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
    // The centre panel stays behind a config switch for the first live pug, so a bad night is a setting
    // away from being rescued rather than a rollback.
    private bool WorldStyle => Config.Hud && Config.HudStyle.Equals("world", StringComparison.OrdinalIgnoreCase);

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
                mine ? "you are up for captain" : "want to captain?",
                [
                    WorldLayout.Section.Of(volunteers.Count == 0 ? "nobody yet - captains drawn at random" : "IN"),
                    WorldLayout.Section.List(WorldLayout.Grid(volunteers.Select(WorldLayout.Clip))),
                    WorldLayout.Section.Of(quiet.Count == 0 ? null : "STILL TO ANSWER"),
                    WorldLayout.Section.List(WorldLayout.Grid(quiet.Select(WorldLayout.Clip))),
                ],
                WorldLayout.Footer(mine ? "!captain takes it back" : "type !captain", $"{_optInSecondsLeft}s"),
                _hudTicks);
            WorldShow(player, panel);
        }
    }

    // --- map vote ----------------------------------------------------------------------------------

    private void WorldVotePanel()
    {
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
                ? WorldLayout.MainPanel($"you voted {_ballot[mine].Label}",
                    [
                        new([.. WorldLayout.Grid(Tally(tally))]),
                        WorldLayout.Section.Of($"{_votes.Count}/{voters.Count} VOTED - WAITING ON"),
                        WorldLayout.Section.List(WorldLayout.Grid(missing.Select(WorldLayout.Clip))),
                    ],
                    WorldLayout.Footer($"{_voteSecondsLeft}s"),
                    _hudTicks)
                : WorldLayout.MainPanel("pick a map",
                    [WorldLayout.Section.List(WorldLayout.Grid(ballot))],
                    WorldLayout.Footer("type the number in chat", $"{_voteSecondsLeft}s"),
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
        var here = HumanPlayers().Select(Id).ToHashSet();
        var missing = _expectedRoster.Where(entry => !here.Contains(entry.Key)).Select(entry => entry.Value).ToList();
        int back = _expectedRoster.Count - missing.Count;

        foreach (var player in HumanPlayers())
        {
            string panel = WorldLayout.MainPanel("waiting for players",
                [
                    WorldLayout.Section.Of($"{back}/{_expectedRoster.Count} BACK - STILL TO CONNECT"),
                    // Two columns, not three: there are ten lines spare and nobody is reading this one
                    // in a hurry - it wants to be legible across the room, not compact.
                    WorldLayout.Section.List(WorldLayout.Grid(missing.Select(WorldLayout.Clip), 2)),
                    WorldLayout.Section.Of(missing.Count == 0 ? "everybody is back - drafting now" : null),
                ],
                WorldLayout.Footer("drafting anyway in", $"{_waitSecondsLeft}s"),
                _hudTicks);
            WorldShow(player, panel);
        }
    }

    // --- the draft ---------------------------------------------------------------------------------

    private void WorldDraftPanel()
    {
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
        string footer = WorldLayout.Footer(clock, $"{UndraftedCount()} still to pick");

        string left = LeftBoard();
        string right = RightBoard();

        foreach (var player in HumanPlayers())
        {
            string title = Id(player) == onClock
                ? "your pick - type the number"
                : $"{Name(onClock)} is picking";
            WorldShow(player, WorldLayout.MainPanel(title, body, footer, _hudTicks), left, right);
        }
    }

    private List<ulong> TeamOf(ulong captain) => captain == _captain1 ? _team1 : _team2;

    private ulong Other(ulong captain) => captain == _captain1 ? _captain2 : _captain1;

    private static string Clip(string name) => WorldLayout.Clip(name);

    // --- ready -------------------------------------------------------------------------------------

    private void WorldReadyPanel()
    {
        var roster = ReadyRoster();
        int ready = roster.Count(_readied.Contains);

        // Everybody, with their state against their name, rather than a list of who is missing. The
        // roster is a fixed ten lines whatever happens, so the board does not shrink a line every time
        // somebody readies - and seeing your own name with a cross next to it is harder to ignore than
        // being absent from a list.
        var marked = roster.Select(steamId => (_readied.Contains(steamId) ? "+ " : "X ") + Name(steamId));
        var absent = AbsentRoster();

        string left = LeftBoard();
        string right = RightBoard();

        foreach (var player in HumanPlayers())
        {
            bool drafted = _roster.ContainsKey(Id(player));
            bool mine = _readied.Contains(Id(player));

            string panel = WorldLayout.MainPanel(
                !drafted ? "match starting" : mine ? "you are ready" : "type .ready in chat",
                [
                    WorldLayout.Section.Of($"{ready}/{roster.Count} READY"),
                    new([.. WorldLayout.Slots(marked, WorldLayout.TeamSlots, 2)]),
                    // Named, not waited on: the match can start without them, and the room decides
                    // whether to hold for somebody who has gone.
                    WorldLayout.Section.Of(absent.Count == 0 ? null : $"NOT HERE: {string.Join("  ", absent.Select(WorldLayout.Clip))}"),
                ],
                WorldLayout.Footer(_readyHudMap, drafted ? null : "you are not in this match"),
                _hudTicks);
            WorldShow(player, panel, left, right);
        }
    }

    // --- side choice after the knife round ----------------------------------------------------------

    private void WorldSidePanel()
    {
        string left = LeftBoard();
        string right = RightBoard();
        string winner = TeamName(WinningCaptain());
        string loser = TeamName(WinningCaptain() == _captain1 ? _captain2 : _captain1);

        foreach (var player in HumanPlayers())
        {
            bool mine = Id(player) == WinningCaptain()
                || (!Config.Match.SideChoiceCaptainOnly && player.Team == _knifeSide);

            string panel = WorldLayout.MainPanel(
                mine ? "type .stay or .switch" : $"{Name(WinningCaptain())} is picking the side",
                [
                    WorldLayout.Section.Of($"{WorldLayout.Clip(winner)} won the knife round",
                        $"they are on {SideName(_knifeSide)} right now"),
                    WorldLayout.Section.Of("WHAT THE TWO DO",
                        $".stay     {WorldLayout.Clip(winner)} starts {SideName(_knifeSide)}",
                        $".switch   {WorldLayout.Clip(winner)} starts {SideName(OtherSide(_knifeSide))}"),
                    WorldLayout.Section.Of("NEXT",
                        mine ? "the match goes live the moment you pick" : $"{WorldLayout.Clip(loser)} takes whatever is left",
                        "then everybody moves in Discord"),
                ],
                WorldLayout.Footer(_matchMap),
                _hudTicks);
            WorldShow(player, panel, left, right);
        }
    }

    private static CsTeam OtherSide(CsTeam side) =>
        side == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;

    // --- the last panel: move in Discord ------------------------------------------------------------

    private void WorldOpeningPanel()
    {
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
                    WorldLayout.Section.Of(lines[1], lines[2]),
                    WorldLayout.Section.Of(waiting.Count == 0 ? "everybody is in" : "STILL MOVING"),
                    WorldLayout.Section.List(WorldLayout.Grid(waiting.Select(WorldLayout.Clip))),
                ],
                WorldLayout.Footer(mine ? "you are in" : "type .ready when you are in", $"live in {_openingSecondsLeft}s"),
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
        foreach (var player in HumanPlayers())
        {
            string panel = WorldLayout.MainPanel("idle server - fix it",
                [
                    WorldLayout.Section.Of($"the server has been idle for {Describe(_idleWas)},",
                        "which makes the first round behave oddly"),
                    WorldLayout.Section.Of(
                        $"!reload   - load {Server.MapName} again",
                        "!restart  - restart the server",
                        "!skip     - leave it as it is"),
                ],
                "type one of them in chat",
                _hudTicks);
            WorldShow(player, panel);
        }
    }

    private void WorldRestartPanel()
    {
        foreach (var player in HumanPlayers())
        {
            string panel = WorldLayout.MainPanel("server restarting",
                [
                    WorldLayout.Section.Of("WHY", _restartWhy),
                    WorldLayout.Section.Of("WHAT HAPPENS",
                        "the server goes down and comes back on its own.",
                        "nothing else needs doing and nobody needs to tell an admin."),
                    WorldLayout.Section.Of("WHEN IT IS BACK",
                        $"reconnect to the same address - {Server.MapName} will be",
                        "loaded and the warmup held open the way it is now."),
                ],
                WorldLayout.Footer($"{_restartSecondsLeft}s"),
                _hudTicks);
            WorldShow(player, panel);
        }
    }
}
