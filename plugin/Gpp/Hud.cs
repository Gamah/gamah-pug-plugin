using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// What the room needs to do next has to be on screen, not in chat: nobody opens chat, and the engine's own
// warmup text sits over anything subtle. Measured on a connected client, 2026-10-15:
//
//   PrintToChat            read by nobody in time, and buried by the next line
//   PrintToCenterHtml      the warmup HUD draws straight over it - invisible for most of a pug
//   PrintToCenterAlert     shows, but larger and in the way
//   PrintToCenter          shows over warmup, holds a 1Hz redraw without flicker  <- this one
//
// PrintToCenter is small, single-weight text with no markup, bottom-anchored, and it will draw about four
// lines of about eighty characters - more than that and the top lines are cut off. Every panel below is
// built to that budget, so the countdown and the list are always the part that survives.
public partial class Gpp
{
    private const int HudLines = 4;

    // The panel font is proportional, so widths come from HudMetrics, which holds advances measured
    // through the renderer rather than a character count. The alert box is drawn in a larger face that
    // has not been measured, so it keeps a conservative character cap - its lines are short now that the
    // state is stacked down four of them.
    private const int AlertWidth = 30;

    // Seconds the panel has been up, which is what pages the pick list.
    private int _hudTicks;

    private CounterStrikeSharp.API.Modules.Timers.Timer? _hudTimer;

    private void StartHud()
    {
        if (!Config.Hud) return;
        _hudTicks = 0;
        _hudTimer?.Kill();
        _hudTimer = AddTimer(1.0f, HudTick, TimerFlags.REPEAT);
        HudTick();
    }

    private void StopHud()
    {
        _hudTimer?.Kill();
        _hudTimer = null;
        Clear();
    }

    private void Clear()
    {
        foreach (var player in HumanPlayers())
        {
            player.PrintToCenter(" ");
            player.PrintToCenterAlert(" ");
        }
    }

    private void HudTick()
    {
        _hudTicks++;
        if (_phase == PugPhase.Idle || (_phase == PugPhase.Draft && _pickIndex >= _pickOrder.Count))
        {
            StopHud();
            return;
        }

        foreach (var player in HumanPlayers())
        {
            switch (_phase)
            {
                case PugPhase.MapVote:
                    PaintVote(player);
                    break;
                case PugPhase.CaptainKnife:
                    PaintKnife(player);
                    break;
                case PugPhase.Draft:
                    PaintDraft(player);
                    break;
            }
        }
    }

    // Both lists page by *line*, three lines at a time, a new page every three seconds. Three rather
    // than four because the client shrinks the panel's text to fit a fourth line, and small text on a
    // 1080p monitor across the room is not a list anybody reads. Paging by line rather than by a count of
    // cells is also what stops a page coming out one line shorter than the last and jumping about.
    //
    // Numbers run across the whole list, not the page, so a map or a player you saw two pages ago is
    // still that number.
    private const int ListLines = 3;
    private const int PageSeconds = 3;

    // Lays the cells out, then returns the page of at most three lines that is this player's turn to see.
    private static List<string> Page(IEnumerable<string> cells, int ticks, out int page, out int pages)
    {
        var lines = Columns(cells);
        pages = Math.Max(1, (lines.Count + ListLines - 1) / ListLines);
        page = pages > 1 ? (ticks / PageSeconds) % pages : 0;
        return lines.Skip(page * ListLines).Take(ListLines).ToList();
    }

    // The alert box is exactly two lines in every phase: what is happening, and the state. It used to be
    // one line per part, which meant it grew a third line whenever a clock or a page counter appeared and
    // shrank again when it went - and the whole box jumps when it does.
    private static string TwoLine(string title, params string?[] state)
    {
        string line = string.Join("  ", state.Where(part => !string.IsNullOrWhiteSpace(part)));
        return Alert(title, line.Length > 0 ? line : " ");
    }

    private void PaintVote(CCSPlayerController player)
    {
        var tally = new int[_ballot.Count];
        foreach (var index in _votes.Values) tally[index]++;

        // "!1 - Dust2" rather than "1 Dust2": the number on its own left people typing the map name, or
        // "vote 1". Carrying the bang in every cell means the list *is* the instruction.
        var cells = _ballot.Select((map, index) =>
            $"!{index + 1} - {map.Label}{(tally[index] > 0 ? " " + new string('*', Math.Min(tally[index], 5)) : "")}").ToList();
        var panel = Page(cells, _hudTicks, out int page, out int pages);

        bool voted = _votes.TryGetValue(Id(player), out int mine);
        Paint(player, TwoLine(
            voted ? $"VOTED {_ballot[mine].Label}" : "VOTE NOW!",
            $"{_voteSecondsLeft}s",
            $"{_votes.Count}/{HumanPlayers().Count} votes",
            pages > 1 ? $"{page + 1}/{pages}" : null), panel);
    }

    private void PaintKnife(CCSPlayerController player)
    {
        bool duelling = Id(player) == _captain1 || Id(player) == _captain2;
        string clock = _duelArmed ? $"{_knifeSecondsLeft}s" : "starting";
        ulong other = Id(player) == _captain1 ? _captain2 : _captain1;

        Paint(player, TwoLine(
            duelling ? "KNIFE FOR FIRST PICK" : "CAPTAINS ARE KNIFING",
            duelling ? $"vs {Name(other)}" : null,
            clock),
            duelling
                ? ["first blood takes first pick", "nobody else can join in"]
                : [$"{Name(_captain1)} vs {Name(_captain2)}", WhySpectating(Id(player))]);
    }

    private void PaintDraft(CCSPlayerController player)
    {
        ulong onClock = CurrentCaptain();
        bool captain = Id(player) == _captain1 || Id(player) == _captain2;

        // The list is for whoever can pick from it. Everyone else is in spectator waiting to be picked,
        // and a scrolling list of names they cannot do anything with is not information - what they want
        // to know is why they are not playing yet.
        if (!captain)
        {
            Paint(player, TwoLine($"{Name(onClock)} IS PICKING",
                _pickSecondsLeft > 0 ? $"{_pickSecondsLeft}s" : null,
                $"{UndraftedCount()} left"),
                [WhySpectating(Id(player))]);
            return;
        }

        // The number is the player's own, from the pool, not their place in what is left of the list.
        var cells = PickList().Select(entry => $"!{entry.Number} - {Name(entry.Id)}").ToList();
        var panel = Page(cells, _hudTicks, out int page, out int pages);

        Paint(player, TwoLine(
            Id(player) == onClock ? "PICK NOW!" : $"{Name(onClock)} IS PICKING",
            _pickSecondsLeft > 0 ? $"{_pickSecondsLeft}s" : null,
            $"{UndraftedCount()} left",
            pages > 1 ? $"{page + 1}/{pages}" : null), panel);
    }

    // Being stuck in spectator with no explanation is the complaint behind half of this overlay, so every
    // phase says which thing is holding you there and what ends it.
    private string WhySpectating(ulong id)
    {
        return _phase switch
        {
            PugPhase.MapVote => "warm up while you vote - the pug starts when the map is picked",
            PugPhase.CaptainKnife => "you play once the captains have drafted you",
            PugPhase.Draft when _team1.Contains(id) || _team2.Contains(id) =>
                "you are drafted - waiting for the rest of the picks",
            PugPhase.Draft when _gone.Contains(id) => "you left the pool - an admin can !pug cancel",
            PugPhase.Draft => "you are in the pool - you play as soon as a captain picks you",
            _ => "",
        };
    }

    private static string Join(List<string> names)
    {
        string joined = string.Join(", ", names);
        return joined.Length <= 64 ? joined : $"{joined[..61]}...";
    }

    // ".ready in chat" is exactly the instruction a room full of people not reading chat will sit on
    // forever, so the ready phase gets a panel of its own naming who is still holding it up.
    private CounterStrikeSharp.API.Modules.Timers.Timer? _readyHudTimer;
    private int _readyHudSecondsLeft;
    private string _readyHudMap = "";

    private void StartReadyHud(string mapLabel)
    {
        if (!Config.Hud) return;
        _readyHudMap = mapLabel;
        _readyHudSecondsLeft = 300;
        _readyHudTimer?.Kill();
        _readyHudTimer = AddTimer(1.0f, ReadyHudTick, TimerFlags.REPEAT);
        ReadyHudTick();
    }

    private void StopReadyHud()
    {
        _readyHudTimer?.Kill();
        _readyHudTimer = null;
        // The ready roster is only here to drive this panel once the gate has stopped sweeping it.
        _roster.Clear();
        Clear();
    }

    private void ReadyHudTick()
    {
        if (--_readyHudSecondsLeft <= 0)
        {
            // Five minutes of a panel nobody has acted on is a panel nobody is reading. The phase stays
            // open - .ready still starts the match - but the overlay comes down rather than turning into
            // the repeating nag that was the whole complaint about MatchZy's ready phase.
            AfterTick(() =>
            {
                StopReadyHud();
                if (_matchPhase == MatchPhase.Ready) Prompt("Still waiting on readies.", ".ready", Everybody);
            });
            return;
        }

        // Bots cannot ready up, so they are neither counted nor named.
        var roster = _roster.Keys.Where(steamId => !IsBotId(steamId)).ToList();
        var unready = roster.Where(steamId => !_readied.Contains(steamId)).Select(Name).ToList();
        int ready = roster.Count - unready.Count;

        foreach (var player in HumanPlayers())
        {
            bool drafted = _roster.ContainsKey(Id(player));
            bool mine = _readied.Contains(Id(player));

            Paint(player, Alert(
                !drafted ? "MATCH STARTING" : mine ? "YOU ARE READY" : "TYPE .ready IN CHAT",
                roster.Count > 0 ? $"{ready}/{roster.Count} ready" : null),
            [
                $"teams are set on {_readyHudMap}",
                unready.Count == 0 ? "everybody is ready - starting" : "still to ready:",
                .. Columns(unready, 4),
            ]);
        }
    }

    // The match's own panel, which only goes up when the room is being asked for something: the knife
    // round, the side choice after it, and a pause. A live round deliberately draws nothing - the overlay
    // sits where the engine puts its own round text, and a pug does not need it narrated.
    private CounterStrikeSharp.API.Modules.Timers.Timer? _matchHudTimer;

    private void StartMatchHud()
    {
        if (!Config.Hud) return;
        if (_matchHudTimer != null) return;
        _matchHudTimer = AddTimer(1.0f, MatchHudTick, TimerFlags.REPEAT);
        MatchHudTick();
    }

    private void StopMatchHud()
    {
        _matchHudTimer?.Kill();
        _matchHudTimer = null;
        Clear();
    }

    private void MatchHudTick()
    {
        bool wanted = _matchPhase == MatchPhase.Knife
            || _matchPhase == MatchPhase.SideChoice
            || (_matchPhase == MatchPhase.Live && _paused);
        if (!wanted)
        {
            AfterTick(StopMatchHud);
            return;
        }

        var (score1, score2) = _matchPhase == MatchPhase.Live ? Score() : (0, 0);

        foreach (var player in HumanPlayers())
        {
            switch (_matchPhase)
            {
                case MatchPhase.Knife:
                    Paint(player, Alert("KNIFE ROUND", "winner picks the side"),
                        ["knives only - whoever is left standing picks the side to start on"]);
                    break;

                case MatchPhase.SideChoice:
                {
                    bool mine = Id(player) == WinningCaptain()
                        || (!Config.Match.SideChoiceCaptainOnly && player.Team == _knifeSide);
                    Paint(player, TwoLine(
                        mine ? "TYPE .stay OR .switch" : $"{Name(WinningCaptain())} IS PICKING",
                        $"{SideName(_knifeSide)} won the knife"),
                    [
                        mine
                            ? ".stay keeps this side, .switch takes the other"
                            : "waiting on their call",
                    ]);
                    break;
                }

                default:
                    Paint(player, Alert("PAUSED", PauseLine()),
                    [
                        "both sides have to type .unpause to resume",
                        $"{_team1Name} {score1} - {score2} {_team2Name}",
                    ]);
                    break;
            }
        }
    }

    // Lay cells out in rows of `perRow`, padded so the columns line up in a fixed-width HUD font.
    // Pack cells onto as few lines as fit, never splitting one. The old version padded each cell to a
    // fixed character width and grouped a fixed number per row, which only lines up in a monospace font -
    // this panel is not one, so the columns drifted and long cells spilled into a wrap. Three spaces
    // between cells and a hard stop at the budget is uglier in theory and holds together in practice.
    private const string CellGap = "   ";

    internal static List<string> Columns(IEnumerable<string> cells, int perRow = 0)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        int width = 0;
        int gap = HudMetrics.Width(CellGap);

        foreach (var raw in cells)
        {
            string cell = HudMetrics.Clip(raw, HudMetrics.CellBudget);
            int cellWidth = HudMetrics.Width(cell);

            if (line.Length > 0 && width + gap + cellWidth > HudMetrics.LineBudget)
            {
                lines.Add(line.ToString());
                line.Clear();
                width = 0;
            }

            if (line.Length > 0)
            {
                line.Append(CellGap);
                width += gap;
            }
            line.Append(cell);
            width += cellWidth;
        }

        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    // Two channels, both addressed to one player at a time (ClientPrint takes a slot, so every message
    // already goes to exactly one person). Alert is the bigger of the two and carries the one line that
    // says what is happening and what this player types - with the countdown in it, so a clock is on
    // screen in every phase. Center carries the body underneath, which leaves all four of its lines for
    // content instead of spending one on the instruction.
    private static void Paint(CCSPlayerController player, string alert, List<string> panel)
    {
        player.PrintToCenterAlert(alert);
        Send(player, panel);
    }

    // The alert box takes four lines like the panel does, and stacking the state down them beats running
    // it along one line and letting the box wrap wherever it likes - a wrapped alert broke mid-word and
    // cost a line anyway. Each part is clipped on its own so a long name cannot push the clock off.
    internal static string Alert(params string?[] parts)
    {
        var lines = parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Length > AlertWidth ? part[..AlertWidth] : part)
            .Take(HudLines);
        return string.Join("\n", lines);
    }

    private static void Broadcast(List<string> panel)
    {
        foreach (var player in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsHLTV)) Send(player, panel);
    }

    // Ruler only. Send() clips each line to HudWidth, which is exactly the number the ruler exists to
    // check - clipping here would hide the wrap that is being measured.
    internal static void BroadcastRaw(string alert, List<string> panel)
    {
        string body = string.Join("\n", panel.Take(HudLines));
        foreach (var player in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsHLTV))
        {
            player.PrintToCenterAlert(alert);
            player.PrintToCenter(body);
        }
    }

    // Four lines of eighty, bottom-anchored: anything past that pushes the first line off the top, so the
    // panel is clipped here rather than by the client.
    private static void Send(CCSPlayerController player, List<string> panel)
    {
        var lines = panel
            .Where(line => !string.IsNullOrEmpty(line))
            .Select(line => HudMetrics.Clip(line, HudMetrics.LineBudget))
            .Take(HudLines);
        player.PrintToCenter(string.Join("\n", lines));
    }

    // A twelve-name pick list does not fit the four-line budget as one block, so this draws the same list
    // several ways, twelve seconds each, to be judged on a screen rather than argued about. Names are the
    // ones from real pugs on this server, kept because their widths are the thing being tested - a name
    // like the one with the stars in it is what breaks a column layout.
    //   !pug hudlist          all four, A B C D
    //   !pug hudlist db       just those two, in that order
    private static readonly string[] SampleNames =
    [
        "Ada", "Baz", "Cosmo", "\u2606Delta\u2606Wide\u2606Name\u2606", "Echo.", "Fitzwilliam",
        "Romanov", "Crasswater", "Aspirant", "Slingshot", "Enforcer", "Baroud",
    ];

    private const int ProbeSeconds = 12;

    internal void HudListProbe(string order)
    {
        var layouts = order
            .Where(letter => "abcd".Contains(letter))
            .Distinct()
            .ToList();
        if (layouts.Count == 0) layouts = ['a', 'b', 'c', 'd'];

        var cells = SampleNames.Select((name, index) => $"!{index + 1} - {name}").ToList();
        int step = 0;

        Announce($"Pick list probe: {string.Join(" ", layouts.Select(char.ToUpperInvariant))}, {ProbeSeconds}s each.");
        _probeTimer?.Kill();
        _probeTimer = AddTimer(1.0f, () =>
        {
            int index = step / ProbeSeconds;
            int tick = step % ProbeSeconds;
            step++;

            if (index >= layouts.Count)
            {
                AfterTick(() =>
                {
                    _probeTimer?.Kill();
                    _probeTimer = null;
                    StopHud();
                    Announce($"Probe done - which of {string.Join(" ", layouts.Select(char.ToUpperInvariant))} read best?");
                });
                return;
            }

            Broadcast(ProbePanel(layouts[index], tick, cells));
        }, TimerFlags.REPEAT);
    }

    private static List<string> ProbePanel(char layout, int tick, List<string> cells)
    {
        switch (layout)
        {
            case 'a':
                // All twelve at once, four across. Fits the budget exactly and nothing moves.
                return ["A - PICK NOW! 30s", .. Columns(cells, 4)];

            case 'b':
            {
                // Six at a time, two rows of three, a new page every three seconds.
                int page = (tick / 3) % 2;
                return [$"B - PICK NOW! 30s - page {page + 1}/2",
                    .. Columns(cells.Skip(page * 6).Take(6), 3)];
            }

            case 'c':
            {
                // Four at a time on one row, a new page every two seconds.
                int page = (tick / 2) % 3;
                return [$"C - PICK NOW! 30s - page {page + 1}/3",
                    .. Columns(cells.Skip(page * 4).Take(4), 4)];
            }

            default:
            {
                // A three-row window sliding up one row every two seconds, wrapping round.
                var rows = Columns(cells, 3);
                int offset = (tick / 2) % rows.Count;
                var window = Enumerable.Range(0, 3).Select(i => rows[(offset + i) % rows.Count]);
                return ["D - PICK NOW! 30s - scrolling", .. window];
            }
        }
    }

    private CounterStrikeSharp.API.Modules.Timers.Timer? _probeTimer;

    // Center (dest 4) and Alert (dest 6) are different HUD destinations, and every ClientPrint is
    // addressed to one slot, so in principle each player can be shown two panels at once with their own
    // text in each. Whether the two actually draw together or one replaces the other is a client
    // question. !pug hudboth answers it: eight seconds of both channels redrawn together, each naming the
    // player it is addressed to, then four seconds of Alert alone as a control.
    internal void HudBothProbe()
    {
        Announce("Both-channel probe: 8s of CENTER+ALERT together, then 4s of ALERT alone.");
        for (int second = 0; second < 12; second++)
        {
            int left = 12 - second;
            bool together = second < 8;
            AddTimer(1.0f + second, () =>
            {
                foreach (var player in HumanPlayers())
                {
                    player.PrintToCenterAlert($"ALERT for {player.PlayerName} - {left}s");
                    if (!together) continue;
                    Send(player,
                    [
                        $"CENTER for {player.PlayerName} - {left}s",
                        "!1 - Ada     !2 - Baz     !3 - Cosmo",
                        "4 ScoobyOG.  5 Voltzmann  6 Crasswater",
                    ]);
                }
            });
        }
    }

    // How much text the Center and Alert channels will actually carry, and whether they hold still, is up
    // to the client - CSS puts the string in a TextMsg param with no length of its own, so there is nothing
    // to read off the server. !pug hudsize walks the room through the cases we would rely on.
    internal void HudSizeProbe()
    {
        const string ruler = "A ....5...10....5...20....5...30....5...40....5...50....5...60....5...70....5...80";
        string sixLines = string.Join("\n",
            "B line 1 of 6 - MAP VOTE 25s",
            "B line 2 - 1 de_mirage    **",
            "B line 3 - 2 de_inferno   *",
            "B line 4 - 3 de_nuke",
            "B line 5 - 4 de_ancient",
            "B line 6 - vote with !1, !2, ...");
        string longText = "C " + string.Join("\n",
            Enumerable.Range(1, 12).Select(i => $"line {i:00} 123456789 123456789 123456789 12345"));

        Announce("HUD size probe: A ruler, B six lines, C twelve long lines, D the same on ALERT, E a redraw.");
        AddTimer(1.0f, () => { foreach (var p in HumanPlayers()) p.PrintToCenter(ruler); });
        AddTimer(6.0f, () => { foreach (var p in HumanPlayers()) p.PrintToCenter(sixLines); });
        AddTimer(11.0f, () => { foreach (var p in HumanPlayers()) p.PrintToCenter(longText); });
        AddTimer(16.0f, () => { foreach (var p in HumanPlayers()) p.PrintToCenterAlert(sixLines.Replace("B ", "D ")); });

        // Does it hold on its own, and does re-sending it every second flicker? The lists need a countdown.
        for (int second = 0; second < 8; second++)
        {
            int left = 8 - second;
            AddTimer(21.0f + second, () =>
            {
                foreach (var p in HumanPlayers()) p.PrintToCenter($"E redraw test - {left}s left\nsecond line stays put");
            });
        }
    }

}
