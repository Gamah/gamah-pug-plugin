namespace Gpp;

// How wide a string actually is on the centre panel.
//
// Everything here was measured through the renderer with !pug ruler (see HudRuler.cs) on 2026-10-17,
// client at 3840x2160. CS2 ships the panel font as stratum2.uifont, an obfuscated container rather than
// a readable TTF, so the advances cannot be read off disk - they were measured by printing twenty
// copies of one glyph, screenshotting, and solving ink = N*advance - sidebearing, with a forty-pipe
// run on every page pinning the scale. Every measured advance landed on a whole number of pixels,
// which is the main reason to believe the model rather than merely fit it.
//
// The units are 4K pixels, but only their ratio matters: the pipe came out at exactly 4px and the line
// clips at ~125 pipes, so a different resolution should scale both together and leave the budget alone.
// That has NOT been checked at another resolution yet. [SOURCE: measured, not documented. Re-run the
// ruler after a CS2 update - a font change would silently make all of this wrong.]
//
// What the measurements actually showed, and why counting characters could never have worked: the
// widest glyph is three and a half times the narrowest. A line holds 125 letter i and 35 letter m.
internal static class HudMetrics
{
    // Measured exactly.
    private const int Narrow = 4;    // space | i l
    private const int Mid = 8;       // a e
    private const int Round = 9;     // o n and every digit (they are tabular - 0, 1 and 8 all measured 9)
    private const int Wide = 11;     // w A
    private const int Wider = 12;    // M
    private const int Widest = 14;   // m W

    // Measured: space | i l a e o n w A M m W and 0-9. Everything else below is placed in the class its
    // shape belongs to and rounded **up**, because guessing narrow makes a line wrap while guessing wide
    // only wastes a little space. The uppercase letters are the weakest part of this - four of twenty-six
    // were measured - so they all default wide.
    private const int UppercaseGuess = 12;   // between A (11) and M (12)
    private const int UnknownGuess = Widest; // anything else, including the decorated names people use

    // How much of a line is usable, in the same units as the table above.
    //
    // This is the one number that is NOT a property of the font, and it is why there is a config key
    // for it. The ruler run clipped at about 500 units. But the screenshot that started all of this -
    // taken on the same client at a different resolution - wrapped a line that measures 348 units, and
    // three strings from it come out at a consistent 1.27x the ruler scale. Same font, same ratios,
    // different size relative to the box. So the usable width in these units depends on the viewer's
    // resolution and HUD scale, which the server cannot know and which differs per player anyway.
    //
    // 340 because 348 is the widest line observed to FIT at the worse of the two scales, and 406 is the
    // narrowest observed to wrap. Anything in between is a guess, so the budget sits below the known-good
    // figure. Too high brings back the wrap this was all meant to stop; too low only wastes a little space.

    // A single cell longer than this is truncated rather than allowed to eat a whole line.
    internal const int CellBudget = 200;

    internal static int Width(char c) => c switch
    {
        // U+00A0 is the padding character the world panel holds its width with - see WorldLayout.Pad -
        // so it has to measure as the space it is drawn as.
        // U+00B7 is the mark the world panel puts on a line that would otherwise be blank - see
        // WorldLayout.Filler - because an empty line is not drawn at all and the board loses its height.
        ' ' or '\u00a0' or '\u00b7' or '|' or 'i' or 'l' or 'j' or 'I' or '.' or ',' or '\'' or ';' or ':' or '!' => Narrow,
        'a' or 'e' or 'f' or 'r' or 't' or 's' or 'c' or 'z' => Mid,
        'o' or 'n' or 'b' or 'd' or 'g' or 'h' or 'k' or 'p' or 'q' or 'u' or 'v' or 'x' or 'y' => Round,
        >= '0' and <= '9' => Round,
        '-' or '+' or '=' or '(' or ')' or '[' or ']' or '/' => Round,
        'w' or 'A' => Wide,
        'M' => Wider,
        'm' or 'W' => Widest,
        >= 'A' and <= 'Z' => UppercaseGuess,
        _ => UnknownGuess,
    };

    internal static int Width(string text)
    {
        int total = 0;
        foreach (char c in text) total += Width(c);
        return total;
    }

    // A window into a line too long to fit, starting `skip` units in: the scrolling alternative to
    // cutting it off. Returns as much as fits from there, and how many units were actually skipped, so
    // the caller can tell when it has reached the end.
    internal static string Window(string text, int budget, int skip, out int skipped)
    {
        int from = 0;
        skipped = 0;
        foreach (char c in text)
        {
            if (skipped >= skip) break;
            skipped += Width(c);
            from++;
        }

        int used = 0;
        int taken = 0;
        foreach (char c in text[from..])
        {
            int next = Width(c);
            if (used + next > budget) break;
            used += next;
            taken++;
        }
        return text.Substring(from, taken);
    }

    // Cut to a width rather than to a character count, keeping room for the ellipsis that says so.
    internal static string Clip(string text, int budget)
    {
        if (Width(text) <= budget) return text;

        int room = budget - Width('…');
        int used = 0;
        int taken = 0;
        foreach (char c in text)
        {
            int next = Width(c);
            if (used + next > room) break;
            used += next;
            taken++;
        }
        return text[..taken] + "…";
    }
}
