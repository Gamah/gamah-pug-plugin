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

    private void WorldShow(CCSPlayerController player, string panel) => ShowWorldPanel(player, panel);

    // --- captain sign-up ---------------------------------------------------------------------------

    private void WorldOptInPanel()
    {
        var volunteers = _volunteers.Where(steamId => PlayerBySteamId(steamId) != null).Select(Name).ToList();
        var quiet = HumanPlayers()
            .Where(player => !player.IsBot && !_volunteers.Contains(Id(player)))
            .Select(player => player.PlayerName)
            .ToList();

        foreach (var player in HumanPlayers())
        {
            bool mine = _volunteers.Contains(Id(player));
            string panel = WorldLayout.Panel(
                mine ? "you are up for captain" : "want to captain?",
                [
                    WorldLayout.Section.Of(volunteers.Count == 0
                        ? "nobody yet - captains get drawn at random"
                        : $"IN:  {string.Join("  ", volunteers.Select(WorldLayout.Clip))}"),
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
                ? WorldLayout.Panel($"you voted {_ballot[mine].Label}",
                    [
                        WorldLayout.Section.Of($"{_votes.Count}/{voters.Count} VOTED - WAITING ON"),
                        WorldLayout.Section.List(WorldLayout.Grid(missing.Select(WorldLayout.Clip))),
                        WorldLayout.Section.Of("LEADING", LeadingMaps(tally)),
                    ],
                    WorldLayout.Footer("!1-!9 changes your vote", $"{_voteSecondsLeft}s"),
                    _hudTicks)
                : WorldLayout.Panel("pick a map",
                    [WorldLayout.Section.List(WorldLayout.Grid(ballot))],
                    WorldLayout.Footer("type the number in chat", $"{_voteSecondsLeft}s"),
                    _hudTicks);
            WorldShow(player, panel);
        }
    }

    private string LeadingMaps(int[] tally)
    {
        int best = tally.Length == 0 ? 0 : tally.Max();
        if (best == 0) return "no votes yet";
        return string.Join("    ", _ballot
            .Select((map, index) => (map.Label, Votes: tally[index]))
            .Where(entry => entry.Votes == best)
            .Select(entry => $"{entry.Label} {new string('*', Math.Min(entry.Votes, 5))}"));
    }

    // --- waiting for the room to come back after the map change -------------------------------------

    private void WorldWaitPanel()
    {
        var here = HumanPlayers().Select(Id).ToHashSet();
        var missing = _expectedRoster.Where(entry => !here.Contains(entry.Key)).Select(entry => entry.Value).ToList();
        int back = _expectedRoster.Count - missing.Count;

        foreach (var player in HumanPlayers())
        {
            string panel = WorldLayout.Panel("waiting for players",
                [
                    WorldLayout.Section.Of($"{back}/{_expectedRoster.Count} BACK - STILL TO CONNECT"),
                    WorldLayout.Section.List(WorldLayout.Grid(missing.Select(WorldLayout.Clip))),
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

        foreach (var player in HumanPlayers())
        {
            bool captain = Id(player) == _captain1 || Id(player) == _captain2;
            string panel;

            if (captain)
            {
                // The captain gets the list, and under it both teams as they fill - a captain picking
                // blind to what the other side has taken is how a pug ends up with five AWPers.
                bool theirs = Id(player) == onClock;
                panel = WorldLayout.Panel(
                    theirs ? "your pick" : $"{Name(onClock)} is picking",
                    [
                        WorldLayout.Section.List(WorldLayout.Grid(pool)),
                        new([.. WorldLayout.Rosters(
                            $"YOU", TeamOf(Id(player)).Select(Name),
                            $"THEM", TeamOf(Other(Id(player))).Select(Name))]),
                    ],
                    WorldLayout.Footer(theirs ? "type the number" : "waiting on them", clock, $"{UndraftedCount()} left"),
                    _hudTicks);
            }
            else
            {
                // Everybody else is in spectator and cannot act on the list at all. What they want to
                // know is whether they have been picked yet and how long this will take.
                panel = WorldLayout.Panel($"{Name(onClock)} is picking",
                    [
                        new([.. WorldLayout.Pair(
                            Clip(TeamName(_captain1)), _team1.Select(Name),
                            Clip(TeamName(_captain2)), _team2.Select(Name))]),
                        WorldLayout.Section.Of(WhySpectating(Id(player))),
                    ],
                    WorldLayout.Footer(clock, $"{UndraftedCount()} still in the pool"),
                    _hudTicks);
            }

            WorldShow(player, panel);
        }
    }

    private List<ulong> TeamOf(ulong captain) => captain == _captain1 ? _team1 : _team2;

    private ulong Other(ulong captain) => captain == _captain1 ? _captain2 : _captain1;

    private static string Clip(string name) => WorldLayout.Clip(name);

    // --- ready -------------------------------------------------------------------------------------

    private void WorldReadyPanel()
    {
        var roster = ReadyRoster();
        var unready = roster.Where(steamId => !_readied.Contains(steamId)).Select(Name).ToList();
        int ready = roster.Count - unready.Count;

        foreach (var player in HumanPlayers())
        {
            bool drafted = _roster.ContainsKey(Id(player));
            bool mine = _readied.Contains(Id(player));

            string panel = WorldLayout.Panel(
                !drafted ? "match starting" : mine ? "you are ready" : "type .ready in chat",
                [
                    WorldLayout.Section.Of(unready.Count == 0
                        ? "everybody is ready - here we go"
                        : $"{ready}/{roster.Count} READY - STILL TO READY"),
                    WorldLayout.Section.List(WorldLayout.Grid(unready.Select(WorldLayout.Clip))),
                    new([.. WorldLayout.Rosters(
                        Clip(TeamName(_captain1)), _team1.Select(Name),
                        Clip(TeamName(_captain2)), _team2.Select(Name))]),
                ],
                WorldLayout.Footer(_readyHudMap, drafted ? null : "you are not in this match"),
                _hudTicks);
            WorldShow(player, panel);
        }
    }

    // --- side choice after the knife round ----------------------------------------------------------

    private void WorldSidePanel()
    {
        var (score1, score2) = (0, 0);
        foreach (var player in HumanPlayers())
        {
            bool mine = Id(player) == WinningCaptain()
                || (!Config.Match.SideChoiceCaptainOnly && player.Team == _knifeSide);

            string panel = WorldLayout.Panel(
                mine ? "type .stay or .switch" : $"{Name(WinningCaptain())} is picking the side",
                [
                    WorldLayout.Section.Of($"{SideName(_knifeSide)} won the knife round",
                        mine ? ".stay keeps this side, .switch takes the other" : "waiting on their call"),
                    new([.. WorldLayout.Rosters(
                        Clip(_team1Name), _team1.Select(Name),
                        Clip(_team2Name), _team2.Select(Name))]),
                ],
                WorldLayout.Footer(_matchMap, $"{score1}-{score2}"),
                _hudTicks);
            WorldShow(player, panel);
        }
    }

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

        foreach (var player in HumanPlayers())
        {
            bool mine = _openingReady.Contains(Id(player));
            string panel = WorldLayout.Panel(lines[0],
                [
                    WorldLayout.Section.Of(lines[1], lines[2]),
                    WorldLayout.Section.Of(waiting.Count == 0 ? "everybody is in" : "STILL MOVING"),
                    WorldLayout.Section.List(WorldLayout.Grid(waiting.Select(WorldLayout.Clip))),
                    new([.. WorldLayout.Rosters(
                        Clip(_team1Name), _team1.Select(Name),
                        Clip(_team2Name), _team2.Select(Name))]),
                ],
                WorldLayout.Footer(mine ? "you are in" : "type .ready when you are in", $"live in {_openingSecondsLeft}s"),
                _hudTicks);
            WorldShow(player, panel);
        }
    }

    // --- the two panels that are not part of a pug at all --------------------------------------------

    private void WorldIdlePanel()
    {
        foreach (var player in HumanPlayers())
        {
            string panel = WorldLayout.Panel("idle server - fix it",
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
            string panel = WorldLayout.Panel("server restarting",
                [
                    WorldLayout.Section.Of(_restartWhy),
                    WorldLayout.Section.Of("reconnect in about a minute -", "the server comes back on its own"),
                ],
                $"{_restartSecondsLeft}s",
                _hudTicks);
            WorldShow(player, panel);
        }
    }
}
