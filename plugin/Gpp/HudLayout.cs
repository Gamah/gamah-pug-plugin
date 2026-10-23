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
    // What is left of the centre channel after the panel itself went: the alert line. The world boards
    // carry everything a panel used to say; this is the one line that still reaches somebody with no
    // pawn of their own to hang a board on.

    // The panel's four lines, kept because the ruler still prints four and the boards are sized against
    // the same number.
    internal const int PanelLines = 4;

    // The alert box is drawn in a larger face than anything HudMetrics measured, so it is held to a
    // character count rather than a measured width.
    internal const int AlertWidth = 30;

    // One character every TicksPerChar paints, still at each end. The tick is bumped once a second now
    // rather than ten times, so a long alert crawls rather than scrolls - there is one of them (the
    // captain knife's "vs <name>") and it fits anyway.
    private const int TicksPerChar = 3;
    private const int HoldTicks = 15;

    private static int _paintTick;

    internal static void BumpPaintTick() => _paintTick++;

    internal static string Alert(params string?[] parts)
    {
        var lines = parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => ScrollChars(part!, AlertWidth, _paintTick))
            .Take(PanelLines);
        return string.Join("\n", lines);
    }

    private static string ScrollChars(string line, int width, int tick)
    {
        if (line.Length <= width) return line;

        int last = line.Length - width;
        int steps = last * TicksPerChar;
        int cycle = steps + (2 * HoldTicks);
        int at = ((tick % cycle) + cycle) % cycle;
        int chars = Math.Clamp((at - HoldTicks) / TicksPerChar, 0, last);
        return line.Substring(chars, width);
    }
}
