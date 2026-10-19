using Gpp;
using Xunit;
using Xunit.Abstractions;

namespace Gpp.Tests;

// Every panel the pug draws, at its worst, through the code that really draws it. The two things that
// have actually gone wrong on a screen are both invariants here: a line wider than the budget (the
// client wraps it in the middle of a word) and an ellipsis where the text was cut off instead of
// scrolled.
public class HudLayoutTests
{
    private readonly ITestOutputHelper _out;

    public HudLayoutTests(ITestOutputHelper output) => _out = output;

    // The names that break things: the widest glyphs in the font, the decorated names people really
    // use, and a couple of ordinary ones for contrast.
    private static readonly string[] Names =
    [
        "Ada", "Baz", "Cosmo", "Wwwwwwwwwww", "mmmmmmmmmm",
        "☆Supreme☆Toxicity☆", "Fitzwilliam", "Rouchard", "Slingshot", "Enforcer",
    ];

    private static readonly string[] Maps =
    [
        "office", "Assault", "dust2", "mirage", "inferno", "nuke", "ancient", "anubis", "train",
        "overpass", "vertigo", "cache", "Cobblestone2018", "Tuscan", "Season", "Agency", "Militia",
    ];

    // What matters is what is sent: a row of cells must fit the budget, and a single cell too wide to
    // fit anywhere is allowed to overflow its own line, because the panel scrolls that line as it paints.
    private static void AssertFits(string what, IEnumerable<string> lines)
    {
        var all = lines.ToList();
        foreach (var line in all)
        {
            int width = HudMetrics.Width(line);
            bool oneCell = !line.Contains("   ");
            Assert.False(width > HudMetrics.LineBudget && !oneCell,
                $"{what}: row is {width} units, over the {HudMetrics.LineBudget} budget: \"{line}\"");
            Assert.DoesNotContain("\u2026", line);
        }

        foreach (var painted in HudLayout.Panel(all, tick: 0).Split('\n'))
        {
            Assert.True(HudMetrics.Width(painted) <= HudMetrics.LineBudget,
                $"{what}: painted line is {HudMetrics.Width(painted)} units: \"{painted}\"");
        }
    }

    [Fact]
    public void PickListFitsAndKeepsEveryName()
    {
        var cells = Names.Select((name, i) => $"!{i + 1} - {name}").ToList();
        var lines = HudLayout.Columns(cells);

        AssertFits("pick list", lines);
        foreach (var name in Names)
        {
            Assert.Contains(lines, line => line.Contains(name));
        }
        _out.WriteLine($"pick list -> {lines.Count} lines, widest {lines.Max(HudMetrics.Width)} units");
        foreach (var line in lines) _out.WriteLine($"  [{HudMetrics.Width(line),3}] {line}");
    }

    [Fact]
    public void BallotFitsWithTheWholePoolOnIt()
    {
        var cells = Maps.Select((map, i) => $"!{i + 1} - {map}").ToList();
        var lines = HudLayout.Columns(cells);

        AssertFits("ballot", lines);
        _out.WriteLine($"ballot -> {lines.Count} lines, widest {lines.Max(HudMetrics.Width)} units");
        foreach (var line in lines) _out.WriteLine($"  [{HudMetrics.Width(line),3}] {line}");
    }

    [Fact]
    public void ReadyListOfATenManFits()
    {
        var lines = HudLayout.Columns(Names);
        AssertFits("ready list", lines);
        _out.WriteLine($"ready list -> {lines.Count} lines, widest {lines.Max(HudMetrics.Width)} units");
    }

    // The one that was wrong on screen: three choices packed onto two lines of run-on text.
    [Fact]
    public void IdleMenuIsThreeLinesOneChoiceEach()
    {
        var menu = new List<string>
        {
            "!reload - load de_dust2 again",
            "!restart - restart the server",
            "!skip - leave it as it is",
        };

        string panel = HudLayout.Panel(menu, tick: 0);
        var lines = panel.Split('\n');

        Assert.Equal(3, lines.Length);
        AssertFits("idle menu", lines);
        Assert.StartsWith("!reload", lines[0]);
        Assert.StartsWith("!restart", lines[1]);
        Assert.StartsWith("!skip", lines[2]);
    }

    // A cell too wide to share a line used to be cut off with an ellipsis. It gets a line of its own and
    // scrolls there instead.
    [Fact]
    public void AnOverWideCellKeepsItsTextAndTakesItsOwnLine()
    {
        string monster = "!1 - " + new string('m', 40);
        var lines = HudLayout.Columns(["!0 - Ada", monster, "!2 - Baz"]);

        Assert.Contains(lines, line => line == monster);
        Assert.DoesNotContain(lines, line => line.Contains('…'));

        // and the panel scrolls that line rather than sending it over-wide
        string first = HudLayout.Panel(lines, tick: 0);
        Assert.All(first.Split('\n'), line =>
            Assert.True(HudMetrics.Width(line) <= HudMetrics.LineBudget, line));
    }

    [Fact]
    public void ScrollHoldsThenMovesThenComesBack()
    {
        // Numbered rather than repeated: "word word word" scrolls through the same handful of windows
        // because the text repeats, which says nothing about whether the scroll advances.
        string line = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"word{i:00}"));
        int budget = HudMetrics.LineBudget;

        string atStart = HudLayout.Scroll(line, budget, 0);
        string stillHeld = HudLayout.Scroll(line, budget, 2);
        string moved = HudLayout.Scroll(line, budget, 10);

        Assert.Equal(atStart, stillHeld);               // holds at the start
        Assert.NotEqual(atStart, moved);                // then moves
        Assert.True(line.StartsWith(atStart));          // and starts at the beginning

        // every window fits, and it comes back round
        var seen = new HashSet<string>();
        for (int tick = 0; tick < 500; tick++)
        {
            string window = HudLayout.Scroll(line, budget, tick);
            Assert.True(HudMetrics.Width(window) <= budget, $"tick {tick}: {HudMetrics.Width(window)} units");
            seen.Add(window);
        }
        Assert.Equal(atStart, HudLayout.Scroll(line, budget, 0));
        Assert.True(seen.Count > 5, "a long line should scroll through more than a handful of windows");
    }

    [Fact]
    public void ShortLinesAreLeftAlone()
    {
        const string line = "!skip - leave it as it is";
        for (int tick = 0; tick < 20; tick++)
        {
            Assert.Equal(line, HudLayout.Scroll(line, HudMetrics.LineBudget, tick));
        }
    }

    [Fact]
    public void PanelNeverSendsMoreThanFourLines()
    {
        var many = Enumerable.Range(1, 12).Select(i => $"line {i}").ToList();
        Assert.Equal(4, HudLayout.Panel(many, tick: 0).Split('\n').Length);
    }

    [Fact]
    public void AlertIsTwoLinesAndStaysWithinItsWidth()
    {
        string alert = HudLayout.TwoLine("☆Supreme☆Toxicity☆ IS PICKING", "30s", "9 left", "2/3");
        var lines = alert.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.True(line.Length <= HudLayout.AlertWidth,
            $"alert line is {line.Length} characters: \"{line}\""));
        _out.WriteLine(alert);
    }

    // The idle panel's alert is the one place three lines are wanted.
    [Fact]
    public void AlertCanCarryThreeLines()
    {
        string alert = HudLayout.Alert("IDLE SERVER FIX", "server has been idle for", "10 hours");
        Assert.Equal(3, alert.Split('\n').Length);
    }

    [Fact]
    public void ALongAlertPartScrollsRatherThanBeingCut()
    {
        const string title = "☆Supreme☆Toxicity☆☆☆ IS PICKING RIGHT NOW";
        string first = HudLayout.Alert(title);
        string later = HudLayout.Alert(title);   // same tick, same window

        Assert.Equal(first, later);
        Assert.True(first.Length <= HudLayout.AlertWidth);
        Assert.DoesNotContain("…", first);

        // The panel holds still for the first couple of seconds on purpose - at four redraws a second
        // that is eight of these - so a handful of ticks is not enough to see it move.
        for (int tick = 0; tick < 12; tick++) HudLayout.BumpPaintTick();
        Assert.NotEqual(first, HudLayout.Alert(title));
    }
}
