using System.Text;

namespace Gpp;

// The panel layout, with nothing from CounterStrikeSharp in it so that Gpp.Tests can run every panel's
// worst case - a ten-man ready list, a seventeen-map ballot, the decorated names people actually use -
// through the code that really draws it. A test that reimplemented the layout would agree with itself
// and prove nothing.
//
// Two boxes, both addressed to one player at a time. The alert box (PrintToCenterAlert) is the larger
// face and carries what is happening; the panel below it (PrintToCenter) carries the detail and the
// lists. Neither will scroll a client-side overflow for us, so anything too wide is scrolled here.
internal static class HudLayout
{
    // Four lines of about eighty characters, bottom-anchored: more than that and the top lines are cut
    // off by the client.
    internal const int PanelLines = 4;

    // The alert box is drawn in a larger face that has never been measured, so it keeps a conservative
    // character cap rather than using the glyph widths.
    internal const int AlertWidth = 30;

    // A line too wide for the panel scrolls instead of being cut off with an ellipsis. Four lines of
    // eighty is not a lot to say a map vote and nine names in, and "still to ready: Ada, Baz, Cosm…" is
    // the part of the message that mattered.
    //
    // Driven by a paint counter rather than wall-clock time so every panel scrolls in step, and it holds
    // still at each end for a couple of seconds - a line that starts moving the instant it appears is
    // unreadable, and one that wraps round without a pause reads as a glitch.
    private static int _paintTick;

    internal static void BumpPaintTick() => _paintTick++;

    private const int ScrollUnitsPerTick = 12;   // about one glyph per redraw
    private const int ScrollHoldTicks = 3;

    internal static string Scroll(string line, int budget, int tick)
    {
        int width = Width(line);
        if (width <= budget) return line;

        // One step per tick across the overflow, with a hold at both ends.
        int steps = (width - budget + ScrollUnitsPerTick - 1) / ScrollUnitsPerTick;
        int cycle = steps + (2 * ScrollHoldTicks);
        int at = ((tick % cycle) + cycle) % cycle;
        int step = Math.Clamp(at - ScrollHoldTicks, 0, steps);

        return HudMetrics.Window(line, budget, step * ScrollUnitsPerTick, out _);
    }

    private static int Width(string text) => HudMetrics.Width(text);

    // Cells laid out in even columns, padded so they line up.
    //
    // This used to pack greedily - as many cells on a line as fit, then wrap - which on a ten-man pick
    // list gave rows of three, two, one and three, with a long decorated name sitting alone on a line of
    // its own. Nothing was wrapping; it just read as though it had. A grid of equal columns reads as a
    // list, and the numbers stay under each other.
    //
    // The column is as wide as the widest cell, capped at a third of the line so one outlier name cannot
    // collapse the grid to a single column. A cell wider than its column overflows it rather than being
    // cut off, and the row it is on scrolls when the panel is painted.
    private const string CellGap = "   ";

    internal static List<string> Columns(IEnumerable<string> cells)
    {
        var all = cells.Where(cell => !string.IsNullOrEmpty(cell)).ToList();
        if (all.Count == 0) return [];

        int gap = HudMetrics.Width(CellGap);
        int widest = all.Max(HudMetrics.Width);
        int column = Math.Min(widest, (HudMetrics.LineBudget - (2 * gap)) / 3);

        // How many of those fit on a line, at least one.
        int perRow = Math.Max(1, (HudMetrics.LineBudget + gap) / (column + gap));

        var lines = new List<string>();
        var row = new List<string>();
        int rowWidth = 0;

        foreach (var cell in all)
        {
            int cellWidth = HudMetrics.Width(cell);
            int padded = Math.Max(cellWidth, column);
            bool wouldOverflow = row.Count > 0 && rowWidth + gap + cellWidth > HudMetrics.LineBudget;

            // A full row, or one more cell than the line can really hold. The column width is a target,
            // not a promise: a name wider than its column still has to be counted at its real width or
            // the row goes over the budget and the client wraps it, which is the thing being avoided.
            if (row.Count >= perRow || wouldOverflow)
            {
                lines.Add(Pad(row, column));
                row.Clear();
                rowWidth = 0;
            }

            row.Add(cell);
            rowWidth += (row.Count > 1 ? gap : 0) + padded;
        }
        if (row.Count > 0) lines.Add(Pad(row, column));

        return lines;
    }

    // Cells joined with the gap, each padded out to the column width so the next one starts in the same
    // place on every row. A space is four units, so the alignment is good to within a space.
    private static string Pad(List<string> row, int column)
    {
        var line = new StringBuilder();
        for (int i = 0; i < row.Count; i++)
        {
            if (i > 0) line.Append(CellGap);
            line.Append(row[i]);
            if (i == row.Count - 1) continue;

            for (int width = HudMetrics.Width(row[i]); width + HudMetrics.Width(' ') <= column;
                 width += HudMetrics.Width(' '))
            {
                line.Append(' ');
            }
        }
        return line.ToString();
    }

    internal static string TwoLine(string title, params string?[] state)
    {
        string line = string.Join("  ", state.Where(part => !string.IsNullOrWhiteSpace(part)));
        return Alert(title, line.Length > 0 ? line : " ");
    }

    // The alert box takes four lines like the panel does, and stacking the state down them beats running
    // it along one line and letting the box wrap wherever it likes - a wrapped alert broke mid-word and
    // cost a line anyway. Each part is clipped on its own so a long name cannot push the clock off.
    internal static string Alert(params string?[] parts)
    {
        var lines = parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => ScrollChars(part!, AlertWidth, _paintTick))
            .Take(PanelLines);
        return string.Join("\n", lines);
    }

    // The alert box is drawn in a larger face nothing has measured, so it is held to a character count -
    // and scrolls by characters for the same reason the panel scrolls by units.
    private static string ScrollChars(string line, int width, int tick)
    {
        if (line.Length <= width) return line;

        int steps = line.Length - width;
        int cycle = steps + (2 * ScrollHoldTicks);
        int at = ((tick % cycle) + cycle) % cycle;
        int step = Math.Clamp(at - ScrollHoldTicks, 0, steps);
        return line.Substring(step, width);
    }

    // What actually goes to PrintToCenter: the lines that fit, each scrolled if it is too wide, capped
    // at the panel's four.
    internal static string Panel(IEnumerable<string> lines, int tick)
    {
        return string.Join("\n", lines
            .Where(line => !string.IsNullOrEmpty(line))
            .Select(line => Scroll(line, HudMetrics.LineBudget, tick))
            .Take(PanelLines));
    }

    internal static string Panel(IEnumerable<string> lines) => Panel(lines, _paintTick);

    internal static int PaintTick => _paintTick;
}
