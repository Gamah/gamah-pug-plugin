using Gpp;
using Xunit;
using Xunit.Abstractions;

namespace Gpp.Tests;

// What is left of the centre channel: the alert line. The panel it used to sit under is gone - the world
// boards carry everything it said - and so are the tests for its columns, paging and scrolling. These
// three are the alert's own invariants, and the first two are the ones that went wrong on a screen: a
// line wider than the box (the client wraps it mid-word) and an ellipsis where the text was cut off
// instead of scrolled.
public class HudLayoutTests
{
    private readonly ITestOutputHelper _out;

    public HudLayoutTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void AnAlertStaysWithinItsWidth()
    {
        string alert = HudLayout.Alert("☆Supreme☆Toxicity☆ IS PICKING", "30s");
        var lines = alert.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.True(line.Length <= HudLayout.AlertWidth,
            $"alert line is {line.Length} characters: \"{line}\""));
        _out.WriteLine(alert);
    }

    // The captain knife's alert is the one place three lines are wanted.
    [Fact]
    public void AnAlertCanCarryThreeLines()
    {
        string alert = HudLayout.Alert("CAPTAINS ARE KNIFING", "Gamah vs Madzie", "first blood");
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
