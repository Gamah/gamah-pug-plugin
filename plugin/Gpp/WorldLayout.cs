using System.Text;

namespace Gpp;

// The world panel's layout, with nothing from CounterStrikeSharp in it so Gpp.Tests can run every
// panel's worst case - a twenty-man pool, a seventeen-map ballot, the decorated names people really
// use - through the code that actually draws it.
//
// This is a different budget from HudLayout, not a wider one. PrintToCenter gave four lines of about
// eighty characters measured in HudMetrics units; a point_worldtext is as wide as it likes and as tall
// as the text, so the only limit is how much of the screen the block may fill. Measured on a client
// 2026-10-19: twelve lines is the ceiling. Past that the panel runs off the top of the view, and
// shrinking the type to fit was rejected - a panel that changes size reads as the panel moving rather
// than the content changing.
internal static class WorldLayout
{
    // Twelve lines, title and footer included. The two prototype panels the owner approved were nine
    // and ten, so this is a ceiling rather than a target.
    internal const int PanelLines = 12;

    // Names are clipped, not wrapped: at twelve characters it is still obvious who it is, and a column
    // that stays where it was is worth more than the last three letters of somebody's tag.
    internal const int NameWidth = 12;

    // Three across is what eighteen names were judged on. Four fits more but clips harder, and the
    // panel is allowed to be tall.
    internal const int PerRow = 3;

    // Columns are space-padded against a proportional font, so they line up to about a space. Exact
    // columns would need one entity per column; approximately-aligned cells read fine.
    private const int Gutter = 4;

    // A page every three seconds, the same cadence the centre panel used, so the two feel alike for as
    // long as both exist.
    private const int PageSeconds = 3;

    // A block of lines, and whether it is the one that may be paged when the panel is too tall.
    internal readonly record struct Section(List<string> Lines, bool Pageable = false)
    {
        internal static Section Of(params string?[] lines) =>
            new([.. lines.Where(line => !string.IsNullOrEmpty(line)).Select(line => line!)]);

        internal static Section List(IEnumerable<string> lines) => new([.. lines], true);
    }

    // "!4  Fitzwillia" - the bang is in the cell because what has to be typed is the whole point of it.
    // The number is the player's own from the pool, never their place in what is left of the list.
    internal static string Cell(int number, string name) => $"!{number,-2} {Clip(name)}";

    internal static string Clip(string name) =>
        name.Length <= NameWidth ? name : name[..NameWidth];

    // Cells laid across the panel, padded to the widest of them.
    internal static List<string> Grid(IEnumerable<string> cells, int perRow = PerRow)
    {
        var list = cells.ToList();
        if (list.Count == 0) return [];

        int width = list.Max(cell => cell.Length) + Gutter;
        var rows = new List<string>();
        for (int start = 0; start < list.Count; start += perRow)
        {
            rows.Add(string.Concat(list.Skip(start).Take(perRow).Select(cell => cell.PadRight(width))).TrimEnd());
        }
        return rows;
    }

    // Both rosters on one line each, which is what the ready and side-choice panels use. Two five-name
    // columns would be six lines of the twelve and leave one line for the list of who is holding it up -
    // and the list is the point of those panels. The panel may be as wide as it likes; it may not be
    // tall.
    internal static List<string> Rosters(string leftTitle, IEnumerable<string> left, string rightTitle, IEnumerable<string> right)
    {
        var l = left.Select(Clip).ToList();
        var r = right.Select(Clip).ToList();
        int width = Math.Max(leftTitle.Length, rightTitle.Length) + 2;

        return
        [
            $"{leftTitle.PadRight(width)}{string.Join("  ", l)}".TrimEnd(),
            $"{rightTitle.PadRight(width)}{string.Join("  ", r)}".TrimEnd(),
        ];
    }

    // Two rosters side by side under their own headings - the shape the draft's spectator panel uses,
    // where the teams filling up *are* the content and there is no list underneath them.
    internal static List<string> Pair(string leftTitle, IEnumerable<string> left, string rightTitle, IEnumerable<string> right)
    {
        var l = left.Select(Clip).ToList();
        var r = right.Select(Clip).ToList();
        int width = Math.Max(leftTitle.Length, l.Count == 0 ? 0 : l.Max(name => name.Length)) + Gutter + 2;

        var lines = new List<string> { leftTitle.PadRight(width) + rightTitle };
        for (int row = 0; row < Math.Max(l.Count, r.Count); row++)
        {
            string one = row < l.Count ? l[row] : "";
            string two = row < r.Count ? r[row] : "";
            lines.Add((one.PadRight(width) + two).TrimEnd());
        }
        return lines;
    }

    // Title, a blank line, the sections with a blank line between them, then the footer at the bottom -
    // the shape of the two panels that were judged to read well, applied to every phase so that a phase
    // change is a change of content and not of furniture.
    //
    // If it does not fit, the pageable section pages: three seconds a page, and the footer says which.
    // Nothing is ever dropped silently, because a name that is not on the panel is a person who thinks
    // the server has forgotten them.
    internal static string Panel(string title, IEnumerable<Section> sections, string? footer, int ticks)
    {
        var blocks = sections.Where(section => section.Lines.Count > 0).ToList();

        // Everything except the pageable list: title, its blank line, the blank line before the footer
        // and the footer itself, plus a blank line between each pair of blocks.
        int fixedLines = 2 + blocks.Where(block => !block.Pageable).Sum(block => block.Lines.Count)
            + Math.Max(0, blocks.Count - 1)
            + (string.IsNullOrEmpty(footer) ? 0 : 2);

        int room = Math.Max(1, PanelLines - fixedLines);
        var paged = blocks.FindIndex(block => block.Pageable);

        int page = 0, pages = 1;
        if (paged >= 0 && blocks[paged].Lines.Count > room)
        {
            var rows = blocks[paged].Lines;
            pages = (rows.Count + room - 1) / room;
            page = (ticks / PageSeconds) % pages;
            blocks[paged] = blocks[paged] with { Lines = [.. rows.Skip(page * room).Take(room)] };
        }

        var text = new StringBuilder(title.ToUpperInvariant()).Append('\n');
        foreach (var block in blocks)
        {
            text.Append('\n').AppendJoin('\n', block.Lines).Append('\n');
        }

        string tail = pages > 1
            ? string.IsNullOrEmpty(footer) ? $"page {page + 1}/{pages}" : $"{footer}        page {page + 1}/{pages}"
            : footer ?? "";
        if (tail.Length > 0) text.Append('\n').Append(tail);

        // A panel whose fixed sections alone overrun the budget is a caller bug - the tests assert every
        // real panel's worst case fits - but it must not take the title off the top of the view, because
        // the title is the instruction. So the last resort drops lines from the bottom of the body and
        // says it did.
        var lines = text.ToString().Split('\n');
        if (lines.Length > PanelLines)
        {
            return string.Join('\n', lines.Take(PanelLines - 1)) + "\n...";
        }
        return string.Join('\n', lines);
    }

    // The middle board, always the same block: twelve lines and one width, whatever is in it. The footer
    // is pinned to the bottom line rather than left to float up under short content, so the clock is
    // always in the same place on the screen.
    internal static string MainPanel(string title, IEnumerable<Section> sections, string? footer, int ticks)
    {
        var rows = Panel(title, sections, footer, ticks).Split('\n').ToList();
        if (rows.Count < PanelLines && !string.IsNullOrEmpty(footer))
        {
            string last = rows[^1];
            rows.RemoveAt(rows.Count - 1);
            while (rows.Count < PanelLines - 1) rows.Add("");
            rows.Add(last);
        }
        return Fixed(string.Join('\n', rows), PanelLines, MainBoardWidth, Align.Centre);
    }

    // The clock and the count that sit in the footer of nearly every panel. Seconds first because it is
    // the number people look for.
    internal static string Footer(params string?[] parts) =>
        string.Join("        ", parts.Where(part => !string.IsNullOrEmpty(part)));

    internal static int LineCount(string panel) => panel.Count(c => c == '\n') + 1;

    // --- fixed-size boards --------------------------------------------------------------------------
    //
    // The boards are furniture: they stay the same size and the text moves inside them. A panel that
    // grows and shrinks with its content reads as the panel itself moving, which is the thing the eye
    // follows instead of the words - and with three boards side by side, two of them resizing as picks
    // land is worse again.
    //
    // So every board is padded out to a fixed number of lines and a fixed width before it is sent. The
    // width is padded in *measured* units rather than characters, because the font is proportional:
    // twenty spaces after "Ada" and twenty after "WWW" are not the same board.
    internal const int TeamSlots = 10;          // a 10v10, which is the biggest pug this is sized for
    internal const int TeamBoardLines = 12;     // label, captain, a blank, and the ten slots

    // Both widths are in HudMetrics units and both are above the widest thing their board can hold -
    // asserted in the tests, because a board whose content outgrows its target stops being a fixed size
    // and starts breathing again. MainBoardWidth is a three-column grid of twelve-character names;
    // TeamBoardWidth is one clipped name plus room for the label.
    internal const int TeamBoardWidth = 260;
    internal const int MainBoardWidth = 900;

    // Which edge a board's text is justified to, which decides where its padding goes. Padding a
    // centre-justified board on one side only moves the text: the block gets wider to the right, so the
    // words walk to the left as the content changes. That was the middle panel wandering.
    internal enum Align { Centre, Left, Right }

    // Pads a composed panel to exactly `lines` tall and `width` wide, padding whichever line is widest
    // so nothing already written gets re-flowed.
    internal static string Fixed(string panel, int lines, int width, Align align = Align.Centre)
    {
        var rows = panel.Split('\n').ToList();

        int widest = 0;
        for (int row = 0; row < rows.Count; row++)
        {
            if (HudMetrics.Width(rows[row]) > HudMetrics.Width(rows[widest])) widest = row;
        }
        rows[widest] = Pad(rows[widest], width, align);

        while (rows.Count < lines) rows.Add("");
        if (rows.Count > lines) rows = [.. rows.Take(lines)];
        return string.Join('\n', rows);
    }

    // Spaces until the line measures at least `width`, laid either side of it or after it depending on
    // which edge the board is justified to. A space is the narrowest glyph there is, so this lands
    // within one space of the target rather than exactly on it.
    private static string Pad(string line, int width, Align align)
    {
        int have = HudMetrics.Width(line);
        if (have >= width) return line;

        // Rounded up, not down: landing just under the target leaves each board a different width by up
        // to a space, which is the difference between furniture and furniture that shuffles.
        int spaces = (int)Math.Ceiling((width - have) / (double)HudMetrics.Width(' '));
        return align switch
        {
            Align.Centre => new string(' ', spaces / 2) + line + new string(' ', spaces - (spaces / 2)),
            Align.Right => new string(' ', spaces) + line,
            _ => line + new string(' ', spaces),
        };
    }

    // One team's board: the label, the captain under it, then ten slots that are there from the first
    // pick whether or not anybody is standing in them. A board that grows as picks land changes size ten
    // times a draft; a board with the gaps already in it shows how many picks are still to come.
    //
    // Composed here rather than through Panel(): Panel puts a blank line after every block, which on a
    // board exactly as tall as its content pushed the last slot off the bottom.
    internal static string TeamBoard(string label, string captain, IEnumerable<string> picks, Align align)
    {
        var filled = picks.Select(Clip).ToList();
        var slots = new List<string>();
        for (int slot = 0; slot < TeamSlots; slot++)
        {
            slots.Add(slot < filled.Count ? filled[slot] : "-");
        }

        string text = $"TEAM {label.ToUpperInvariant()}\n{Clip(captain)}\n\n" + string.Join('\n', slots);
        return Fixed(text, TeamBoardLines, TeamBoardWidth, align);
    }
}
