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

    // The rule that holds the board's width takes the bottom line, so everything a panel composes has
    // to fit in the eleven above it.
    internal const int BodyLines = PanelLines - 1;

    // Names are clipped, not wrapped: at twelve characters it is still obvious who it is, and a column
    // that stays where it was is worth more than the last three letters of somebody's tag.
    internal const int NameWidth = 12;

    // Three across, which is what the ballot and the pick list were judged on and kept. Two was tried
    // when the middle board was too wide for the team boards to sit beside it, but the width was never
    // the columns - it was a board padded out to 900 units with nothing in most of it.
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

        // How much room the list has: the title, the footer and any block that is not the list, with no
        // separator blanks counted - those are spacing, and spacing is what gets dropped first when a
        // panel is tall. Counting them here paged a list that would otherwise have fitted exactly.
        // The list that pages is the longest one, not the first. A panel with two lists on it - who has
        // answered and who has not - paged the short one and let the long one run off the bottom, which
        // is how the sign-up panel lost half the room it was nagging.
        int paged = -1;
        for (int block = 0; block < blocks.Count; block++)
        {
            if (blocks[block].Pageable && (paged < 0 || blocks[block].Lines.Count > blocks[paged].Lines.Count))
            {
                paged = block;
            }
        }

        int fixedLines = 1
            + blocks.Where((block, index) => index != paged).Sum(block => block.Lines.Count)
            + blocks.Count(block => !block.Pageable)            // a gap before each heading
            + (string.IsNullOrEmpty(footer) ? 0 : 1);

        int room = Math.Max(1, BodyLines - fixedLines);

        int page = 0, pages = 1;
        if (paged >= 0 && blocks[paged].Lines.Count > room)
        {
            var list = blocks[paged].Lines;
            pages = (list.Count + room - 1) / room;
            page = (ticks / PageSeconds) % pages;
            blocks[paged] = blocks[paged] with { Lines = [.. list.Skip(page * room).Take(room)] };
        }

        // Spacing is given up a line at a time, worst first, rather than all at once.
        //
        // A gap goes before a heading, never between a heading and its own list - that gap is what split
        // "STILL TO ANSWER" from the names underneath it. The blank under the title is the first to be
        // dropped when a panel is tall, since a title is already set apart by being a title; the gaps
        // between sections are the last, because they are what make two lists read as two lists.
        var gaps = new bool[blocks.Count];
        for (int block = 0; block < blocks.Count; block++) gaps[block] = !blocks[block].Pageable;

        int wanted = 1 + blocks.Sum(block => block.Lines.Count) + gaps.Count(gap => gap)
            + (string.IsNullOrEmpty(footer) ? 1 : 2);
        int over = Math.Max(0, wanted - BodyLines);

        bool underTitle = gaps.Length > 0 && gaps[0];
        if (over > 0 && underTitle) { underTitle = false; gaps[0] = false; over--; }

        bool aboveFooter = !string.IsNullOrEmpty(footer);
        if (over > 0 && aboveFooter) { aboveFooter = false; over--; }

        for (int block = blocks.Count - 1; block > 0 && over > 0; block--)
        {
            if (!gaps[block]) continue;
            gaps[block] = false;
            over--;
        }

        var rows = new List<string> { title.ToUpperInvariant() };
        for (int block = 0; block < blocks.Count; block++)
        {
            if (gaps[block]) rows.Add("");
            rows.AddRange(blocks[block].Lines);
        }

        string tail = pages > 1
            ? string.IsNullOrEmpty(footer) ? $"page {page + 1}/{pages}" : $"{footer}        page {page + 1}/{pages}"
            : footer ?? "";
        if (tail.Length > 0)
        {
            if (aboveFooter) rows.Add("");
            rows.Add(tail);
        }

        var text = new StringBuilder(string.Join('\n', rows));

        // A panel whose fixed sections alone overrun the budget is a caller bug - the tests assert every
        // real panel's worst case fits - but it must not take the title off the top of the view, because
        // the title is the instruction. So the last resort drops lines from the bottom of the body and
        // says it did.
        var lines = text.ToString().Split('\n');
        if (lines.Length > BodyLines)
        {
            return string.Join('\n', lines.Take(BodyLines - 1)) + "\n...";
        }
        return string.Join('\n', lines);
    }

    // The middle board, always the same block: twelve lines and one width, whatever is in it. The footer
    // is pinned to the bottom line rather than left to float up under short content, so the clock is
    // always in the same place on the screen.
    internal static string MainPanel(string title, IEnumerable<Section> sections, string? footer, int ticks)
    {
        var rows = Panel(title, sections, footer, ticks).Split('\n').ToList();
        // The footer sits on the last line above the rule, so the clock is in the same place on every
        // panel whatever is above it.
        if (rows.Count < BodyLines && !string.IsNullOrEmpty(footer))
        {
            string last = rows[^1];
            rows.RemoveAt(rows.Count - 1);
            while (rows.Count < BodyLines - 1) rows.Add("");
            rows.Add(last);
        }
        return Fixed(string.Join('\n', rows), PanelLines, MainBoardWidth);
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
    // Both measured rather than guessed: the widest line either board can hold, plus headroom. The old
    // 900 was a number picked out of the air, and the middle board was two thirds of the screen wide
    // with nothing in most of it - which is why the team boards had nowhere to sit.
    // Widest lines in three columns, 2026-10-19: an eighteen-name draft grid 415, a seventeen-map
    // ballot 344, a clipped name 126. WidestLineIsPrinted in the tests reprints them.
    internal const int TeamBoardWidth = 180;
    internal const int MainBoardWidth = 450;

    // Pads a composed panel to exactly `lines` tall and `width` wide, padding whichever line is widest
    // so nothing already written gets re-flowed.
    internal static string Fixed(string panel, int lines, int width)
    {
        var rows = panel.Split('\n').ToList();
        if (rows.Count > lines - 1) rows = [.. rows.Take(lines - 1)];
        while (rows.Count < lines - 1) rows.Add("");
        rows.Add(Rule(width));
        return string.Join('\n', rows);
    }

    // A board's width is held by a rule - a line of dashes as wide as the board - and not by padding
    // the text with spaces.
    //
    // Padding was tried twice and measured both times. Ordinary spaces: the renderer keeps trailing ones
    // and trims leading ones, so a centre-justified board is drawn as its content plus whatever is on
    // the right, and the words sit left of the box. Non-breaking spaces: the box moved 556 pixels right
    // of the crosshair at 4K. Either way the board's own width depends on which end of a line the
    // renderer decided to believe, which is not something to build furniture on.
    //
    // A rule is two visible glyphs at the extremes and no ambiguity: the box is exactly as wide as the
    // rule, the rule is the same on every board, and it centres because it is symmetrical. It costs the
    // bottom line of the twelve, which also pins the board's height - a run of blank lines at the end
    // would be trimmed the same way the spaces were.
    // [SOURCE: both behaviours measured from screenshots of a 4K client, 2026-10-19.]
    private const char RuleChar = '-';

    internal static string Rule(int width)
    {
        int count = Math.Max(4, (int)Math.Ceiling(width / (double)HudMetrics.Width(RuleChar)));
        return new string(RuleChar, count);
    }

    // One team's board: the label, the captain under it, then ten slots that are there from the first
    // pick whether or not anybody is standing in them. A board that grows as picks land changes size ten
    // times a draft; a board with the gaps already in it shows how many picks are still to come.
    //
    // Composed here rather than through Panel(): Panel puts a blank line after every block, which on a
    // board exactly as tall as its content pushed the last slot off the bottom.
    internal static string TeamBoard(string label, string captain, IEnumerable<string> picks)
    {
        var filled = picks.Select(Clip).ToList();
        var slots = new List<string>();
        for (int slot = 0; slot < TeamSlots; slot++)
        {
            slots.Add(slot < filled.Count ? filled[slot] : "-");
        }

        string text = $"TEAM {label.ToUpperInvariant()}\n{Clip(captain)}\n\n" + string.Join('\n', slots);
        return Fixed(text, TeamBoardLines, TeamBoardWidth);
    }
}
