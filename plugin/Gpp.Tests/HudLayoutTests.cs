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

    // Written in redraws rather than in the constants, so tuning the cadence does not fail the test:
    // what matters is that it holds still at first, moves within a second or two, never overflows, and
    // comes back round to the beginning.
    [Fact]
    public void ScrollHoldsThenMovesThenComesBack()
    {
        string line = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"word{i:00}"));
        int budget = HudMetrics.LineBudget;
        const int redrawsPerSecond = 10;

        string atStart = HudLayout.Scroll(line, budget, 0);
        Assert.StartsWith(atStart, line);
        Assert.Equal(atStart, HudLayout.Scroll(line, budget, 1));   // holds

        int movedAt = Enumerable.Range(1, 4 * redrawsPerSecond)
            .FirstOrDefault(tick => HudLayout.Scroll(line, budget, tick) != atStart);
        Assert.True(movedAt > 0, "a long line should start moving within four seconds");
        Assert.True(movedAt > 2, "it should hold still for a moment before it starts");

        var seen = new HashSet<string>();
        for (int tick = 0; tick < 2000; tick++)
        {
            string window = HudLayout.Scroll(line, budget, tick);
            Assert.True(HudMetrics.Width(window) <= budget, $"tick {tick}: {HudMetrics.Width(window)} units");
            seen.Add(window);
        }
        Assert.Equal(atStart, HudLayout.Scroll(line, budget, 0));
        Assert.True(seen.Count > 20, $"a long line should scroll through plenty of windows, saw {seen.Count}");

        // Every window is the line itself, seen through a slot - never a summary of it.
        foreach (var window in seen) Assert.Contains(window, line);
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

        // It holds still for a moment on purpose, so bump until it moves rather than guessing how many.
        bool moved = false;
        for (int tick = 0; tick < 60 && !moved; tick++)
        {
            HudLayout.BumpPaintTick();
            moved = HudLayout.Alert(title) != first;
        }
        Assert.True(moved, "a long alert line should scroll within a few seconds");
    }
}
