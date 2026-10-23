using Gpp;
using Xunit;
using Xunit.Abstractions;

namespace Gpp.Tests;

// The world panel's one hard limit is twelve lines: past that it runs off the top of the view, and
// nobody sees the title - which on this panel is the instruction. So every phase's worst case is
// asserted to fit, and the list is asserted to page rather than to be cut off, because a name missing
// from a pick list is a person who thinks the server forgot them.
public class WorldLayoutTests
{
    private readonly ITestOutputHelper _out;

    public WorldLayoutTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] Names =
    [
        "Ada", "Baz", "Cosmo", "Fitzwilliam", "Rouchard", "Slingshot", "Enforcer",
        "☆Supreme☆Toxicity☆", "Madzie", "Kittana", "Tani Rani", "giant panda!",
        "SeventeenMidgets", "BadWolfKnight", "peterwrinkle", "da iceman", "Breezy", "Tea.",
    ];

    private static readonly string[] Maps =
    [
        "office", "Assault", "dust2", "mirage", "inferno", "nuke", "ancient", "anubis", "train",
        "overpass", "vertigo", "cache", "Cobblestone2018", "Tuscan", "Season", "Agency", "Militia",
    ];

    private void Show(string what, string panel)
    {
        _out.WriteLine($"--- {what} ({WorldLayout.LineCount(panel)} lines)");
        _out.WriteLine(panel);
    }

    [Fact]
    public void EighteenNamesFitInThreeColumns()
    {
        var cells = Names.Select((name, i) => WorldLayout.Cell(i + 1, name));
        string panel = WorldLayout.Panel("your pick",
            [WorldLayout.Section.List(WorldLayout.Grid(cells))],
            WorldLayout.Footer("30s", "18 left"), 0);

        Show("draft, 18 in the pool", panel);
        Assert.True(WorldLayout.LineCount(panel) <= WorldLayout.PanelLines);
        // Six rows of three, so the whole pool is on one page: this is the twenty-man case and it must
        // not page, because a captain who cannot see everybody picks whoever is on screen.
        Assert.DoesNotContain("page ", panel);
        foreach (var name in Names) Assert.Contains(WorldLayout.Clip(name), panel);
    }

    [Fact]
    public void SeventeenMapBallotFits()
    {
        var cells = Maps.Select((map, i) => WorldLayout.Cell(i + 1, map));
        string panel = WorldLayout.Panel("pick a map",
            [WorldLayout.Section.List(WorldLayout.Grid(cells))],
            WorldLayout.Footer("type the number in chat", "28s"), 0);

        Show("map vote, 17 maps", panel);
        Assert.True(WorldLayout.LineCount(panel) <= WorldLayout.PanelLines);
        Assert.DoesNotContain("page ", panel);
    }

    [Fact]
    public void TooTallPagesRatherThanClips()
    {
        // One name per line is the pathological case - no grid, no columns, forty people.
        var lines = Enumerable.Range(1, 40).Select(n => $"player number {n}");
        string first = WorldLayout.Panel("waiting for players",
            [WorldLayout.Section.Of("still to join"), WorldLayout.Section.List(lines)],
            "28s", 0);

        Show("40 lines, page 1", first);
        Assert.True(WorldLayout.LineCount(first) <= WorldLayout.PanelLines);
        Assert.Contains("page 1/", first);

        // Three seconds later it is a different page, and between them every name has been on screen.
        var seen = new HashSet<string>();
        for (int ticks = 0; ticks < 60; ticks += 3)
        {
            foreach (var line in WorldLayout.Panel("waiting for players",
                [WorldLayout.Section.Of("still to join"), WorldLayout.Section.List(lines)],
                "28s", ticks).Split('\n'))
            {
                seen.Add(line.Trim());
            }
        }
        foreach (var line in lines) Assert.Contains(line, seen);
    }

    [Fact]
    public void EveryFixedSectionSurvivesPaging()
    {
        // The ready panel at its worst: ten still to ready, both rosters, and a map name. The rosters
        // are not the pageable section, so they must never be the thing that disappears.
        var unready = Names.Take(10).Select(WorldLayout.Clip);
        string panel = WorldLayout.Panel("type .ready in chat",
        [
            WorldLayout.Section.Of("6/10 ready - still to ready"),
            WorldLayout.Section.List(WorldLayout.Grid(unready)),
            new([.. WorldLayout.Rosters("TEAM ADA", Names.Take(5), "TEAM COSMO", Names.Skip(5).Take(5))]),
        ], "mirage", 0);

        Show("ready, 10 unready and both rosters", panel);
        Assert.True(WorldLayout.LineCount(panel) <= WorldLayout.PanelLines);
        Assert.Contains("TEAM ADA", panel);
        Assert.Contains("TEAM COSMO", panel);
        Assert.Contains("mirage", panel);
    }

    [Fact]
    public void TitleIsAlwaysTheFirstLine()
    {
        // The title is the instruction, so it is the one line that may never be paged away.
        string panel = WorldLayout.Panel("your pick",
            [WorldLayout.Section.List(Enumerable.Range(1, 30).Select(n => $"line {n}"))], "30s", 99);
        Assert.StartsWith("YOUR PICK\n", panel);
    }

    [Fact]
    public void EmptySectionsDoNotLeaveHoles()
    {
        // Nobody left to ready, nobody in the pool: the blank line that would have separated the empty
        // block from the next one has to go with it, or the panel grows a gap where a list used to be.
        string panel = WorldLayout.Panel("everybody is ready",
            [WorldLayout.Section.List([]), WorldLayout.Section.Of("starting the match")], "mirage", 0);

        Show("nothing in the list", panel);
        Assert.DoesNotContain("\n\n\n", panel);
        Assert.Equal(5, WorldLayout.LineCount(panel));
    }

    [Fact]
    public void BoardsAreAlwaysTheSameSize()
    {
        // The whole point of the fixed boards: an empty team and a full one are the same block, so the
        // furniture does not move while the draft runs.
        string empty = WorldLayout.TeamBoard("A", "Ada", []);
        string full = WorldLayout.TeamBoard("A", "Ada", Names.Take(9));

        Show("team board, first pick", empty);
        Show("team board, full", full);
        Assert.Equal(WorldLayout.TeamBoardLines, WorldLayout.LineCount(empty));
        Assert.Equal(WorldLayout.LineCount(empty), WorldLayout.LineCount(full));
        Assert.Equal(Widest(empty), Widest(full));
    }

    // The hold's board carries a state against every name, which is the longest line either team board
    // has ever had to hold - and a board whose content outgrows its width stops being furniture.
    [Fact]
    public void TheReadyBoardIsTheSameFurnitureAsTheTeamBoard()
    {
        string team = WorldLayout.TeamBoard("A", "Ada", Names.Take(4));
        string hold = WorldLayout.ReadyBoard("A",
            Names.Take(5).Select((name, index) => (name, index % 2 == 0)));

        Show("ready board, mid-hold", hold);
        Assert.Equal(WorldLayout.LineCount(team), WorldLayout.LineCount(hold));
        Assert.Equal(Widest(team), Widest(hold));
        Assert.Contains("NOT READY", hold);
    }

    [Fact]
    public void MainPanelIsAlwaysTwelveLines()
    {
        string shortest = WorldLayout.MainPanel("everybody is ready", [WorldLayout.Section.Of("here we go")], "mirage", 0);
        string longest = WorldLayout.MainPanel("your pick",
            [WorldLayout.Section.List(WorldLayout.Grid(Names.Select((n, i) => WorldLayout.Cell(i + 1, n))))],
            "30s", 0);

        Show("main, nearly empty", shortest);
        Assert.Equal(WorldLayout.PanelLines, WorldLayout.LineCount(shortest));
        Assert.Equal(WorldLayout.PanelLines, WorldLayout.LineCount(longest));
        // The footer is the bottom line of both, so the clock does not wander up the screen when a list
        // gets short.
        // The bottom line is the rule that holds the board's width; the footer is the line above it.
        Assert.EndsWith("mirage", shortest.Split('\n')[^2].TrimEnd());
        Assert.All(new[] { shortest, longest }, panel =>
            Assert.Equal(WorldLayout.Rule(WorldLayout.MainBoardWidth), panel.Split('\n')[^1]));
    }

    private static int Widest(string panel) => panel.Split('\n').Max(HudMetrics.Width);

    // Every panel in a pug, at its worst, measured: same height, same width, and the text centred in the
    // same place. A board that is one space wider than the last one walks across the screen as it
    // updates, which is exactly what "the centre panel is wandering to the left" was.
    [Fact]
    public void NoPanelEverChangesSize()
    {
        var pool = Names.Select((name, i) => WorldLayout.Cell(i + 1, name)).ToList();
        var panels = new Dictionary<string, string>
        {
            ["sign-up"] = WorldLayout.MainPanel("want to captain?",
            [
                WorldLayout.Section.Of("IN"),
                WorldLayout.Section.List(WorldLayout.Grid(Names.Take(2))),
                WorldLayout.Section.Of("STILL TO ANSWER"),
                WorldLayout.Section.List(WorldLayout.Grid(Names.Skip(2))),
            ], WorldLayout.Footer("type !captain", "20s"), 0),

            ["ballot"] = WorldLayout.MainPanel("pick a map",
                [WorldLayout.Section.List(WorldLayout.Grid(Maps.Select((m, i) => WorldLayout.Cell(i + 1, m))))],
                WorldLayout.Footer("type the number in chat", "28s"), 0),

            ["draft"] = WorldLayout.MainPanel("your pick - type the number",
                [WorldLayout.Section.List(WorldLayout.Grid(pool))],
                WorldLayout.Footer("30s", "10 still to pick"), 0),

            ["ready"] = WorldLayout.MainPanel("type .ready in chat",
            [
                WorldLayout.Section.Of("6/10 READY - STILL TO READY"),
                WorldLayout.Section.List(WorldLayout.Grid(Names.Take(4))),
            ], WorldLayout.Footer("mirage"), 0),

            ["side choice"] = WorldLayout.MainPanel("type .stay or .switch",
                [WorldLayout.Section.Of("CT won the knife round", ".stay keeps this side")],
                WorldLayout.Footer("mirage"), 0),

            ["restart"] = WorldLayout.MainPanel("server restarting",
                [WorldLayout.Section.Of("an admin asked for it")], "10s", 0),
        };

        foreach (var (name, panel) in panels)
        {
            Show(name, panel);
            Assert.Equal(WorldLayout.PanelLines, WorldLayout.LineCount(panel));
            // And every one of those lines is drawn. An empty line is not rendered at all, so a board
            // padded with blanks is as tall as its content - which is a board that shrinks as a list
            // empties, and the whole point of these is that they do not move.
            Assert.All(panel.Split('\n'), line => Assert.NotEqual(0, line.Length));
            // Every one of them measures the same, which is only true while the widest line any panel
            // can hold still fits inside MainBoardWidth. When that stops being true this fails rather
            // than the boards quietly going back to breathing.
            // Every board measures the rule's width exactly - which is only true while the widest line a
            // panel can hold still fits inside it. When that stops being true this fails, rather than
            // the boards quietly going back to breathing.
            Assert.Equal(HudMetrics.Width(WorldLayout.Rule(WorldLayout.MainBoardWidth)), Widest(panel));
        }
    }

    // What the widest line any board can hold actually measures, printed so the board widths are set
    // from a measurement rather than a guess. If this grows past MainBoardWidth the boards start
    // breathing again, which is what NoPanelEverChangesSize fails on.
    [Fact]
    public void WidestLineIsPrinted()
    {
        var pool = Names.Concat(Names).Take(18).Select((name, i) => WorldLayout.Cell(i + 1, name));
        int draft = WorldLayout.Grid(pool).Max(HudMetrics.Width);
        int ballot = WorldLayout.Grid(Maps.Select((m, i) => WorldLayout.Cell(i + 1, m))).Max(HudMetrics.Width);
        int names = WorldLayout.Grid(Names.Select(WorldLayout.Clip)).Max(HudMetrics.Width);
        int team = Names.Select(WorldLayout.Clip).Max(HudMetrics.Width);

        _out.WriteLine($"draft grid  {draft}");
        _out.WriteLine($"ballot grid {ballot}");
        _out.WriteLine($"name grid   {names}");
        _out.WriteLine($"team name   {team}");
        _out.WriteLine($"MainBoardWidth {WorldLayout.MainBoardWidth}, TeamBoardWidth {WorldLayout.TeamBoardWidth}");
    }

    // The invariant that has gone wrong more than any other: three boards, side by side, across a view
    // that is only so wide. When this fails the panels overlap on somebody's screen - which is what the
    // owner has had to report by screenshot three times.
    [Fact]
    public void ThreeBoardsFitAcrossTheView()
    {
        float board = WorldGeometry.BoardWidth(WorldLayout.BoardWidth);
        float span = WorldGeometry.ThreeBoardSpan();
        float screen = WorldGeometry.ScreenWidth();

        _out.WriteLine($"board {board:F1} world units, three of them span {span:F1}, screen is {screen:F1}");
        _out.WriteLine($"a board is {100 * board / screen:F0}% of the screen, the three together {100 * span / screen:F0}%");

        Assert.True(span < screen, $"three boards span {span:F1} across a {screen:F1} view - they will overlap");
        // And not so small that the type is unreadable across a room: the three should use most of the
        // width they have, or the boards are further apart than they need to be.
        Assert.True(span > screen * 0.75f, $"three boards only use {100 * span / screen:F0}% of the view");
    }

    // Every board is one width, whatever is on it and whatever phase it is - a middle board that is one
    // size alone and another size with the team boards beside it is three sizes of furniture.
    [Fact]
    public void EveryBoardIsTheSameWidth()
    {
        string main = WorldLayout.MainPanel("pick a map",
            [WorldLayout.Section.List(WorldLayout.Grid(Maps.Select((m, i) => WorldLayout.Cell(i + 1, m))))],
            "28s", 0);
        string sparse = WorldLayout.MainPanel("server restarting", [WorldLayout.Section.Of("an admin asked")], "10s", 0);
        string team = WorldLayout.TeamBoard("A", "Ada", Names.Take(4));
        string empty = WorldLayout.TeamBoard("B", "Baz", []);

        foreach (var panel in new[] { main, sparse, team, empty })
        {
            Assert.Equal(WorldLayout.PanelLines, WorldLayout.LineCount(panel));
            Assert.Equal(WorldLayout.Rule(WorldLayout.BoardWidth), panel.Split('\n')[^1]);
        }
    }
}
