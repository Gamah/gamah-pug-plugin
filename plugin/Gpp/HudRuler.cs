using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// A ruler for the centre panel, because every width in Hud.cs is currently a guess.
//
// The panel font is proportional and CS2 ships it as stratum2.uifont, an obfuscated container rather
// than a readable TTF, so the advance widths cannot be read off disk. They can be measured through the
// renderer instead: print a line that is nothing but twenty copies of one character, screenshot it, and
// the rendered width divided by twenty is that character's advance. Two reference lines on each page
// pin the scale - "||" for the zero point and a line of forty pipes for the panel's usable width.
//
// !pug ruler [a|b|c|d]   one page every four seconds, or a single page if named.
//
// This is a measuring tool, not a feature. It is here rather than in a scratch build because the widths
// it produces are supposed to be re-measured after a CS2 update, and a tool nobody can find does not
// get re-run.
public partial class Gpp
{
    private const int RulerRepeats = 20;
    private const int RulerPageSeconds = 4;

    // Four characters a page, chosen to span the extremes and to cover the shapes that actually appear in
    // map names and player names. Page d measures the space, which cannot be seen on its own: the pipes
    // are the visible edges and "||" on the same page is what gets subtracted back off.
    private static readonly Dictionary<char, string[]> RulerPages = new()
    {
        ['a'] = ["i", "l", "I", "1"],
        ['b'] = ["a", "e", "o", "s"],
        ['c'] = ["m", "w", "M", "W"],
        ['d'] = ["0", "8", "-", "!"],
    };

    private CounterStrikeSharp.API.Modules.Timers.Timer? _rulerTimer;

    internal void HudRulerProbe(string order)
    {
        var pages = string.IsNullOrWhiteSpace(order)
            ? RulerPages.Keys.ToList()
            : order.Where(RulerPages.ContainsKey).ToList();
        if (pages.Count == 0) pages = RulerPages.Keys.ToList();

        Announce($"Ruler: pages {string.Join(" ", pages)}, {RulerPageSeconds}s each. "
            + $"Screenshot each page. Every line is {RulerRepeats} copies of one character.");

        int step = 0;
        _rulerTimer?.Kill();
        _rulerTimer = AddTimer(1.0f, () =>
        {
            int index = step / RulerPageSeconds;
            step++;

            if (index >= pages.Count)
            {
                _rulerTimer?.Kill();
                _rulerTimer = null;
                StopHud();
                Announce("Ruler done.");
                return;
            }

            char page = pages[index];
            var chars = RulerPages[page];

            // Two of the four lines are the scale, so a page is self-contained: whoever measures it does
            // not need to line it up against a different screenshot taken at a different resolution.
            var lines = new List<string>
            {
                new string('|', 40),
                "||",
            };
            lines.AddRange(chars.Take(2).Select(c => string.Concat(Enumerable.Repeat(c, RulerRepeats))));

            BroadcastRaw($"RULER {char.ToUpperInvariant(page)} - {string.Join(" ", chars.Take(2))}", lines);

            // The other two characters ride the second half of the page's dwell, so four characters fit a
            // page without the panel ever holding more than four lines.
            if (step % RulerPageSeconds == 2)
            {
                var more = new List<string> { new string('|', 40), "||" };
                more.AddRange(chars.Skip(2).Select(c => string.Concat(Enumerable.Repeat(c, RulerRepeats))));
                BroadcastRaw($"RULER {char.ToUpperInvariant(page)} - {string.Join(" ", chars.Skip(2))}", more);
            }
        }, TimerFlags.REPEAT);
    }

    // The space has no ink, so it is measured as the gap between two pipes: twenty spaces fenced by a
    // pipe at each end, against a bare "||" for the pipes' own width.
    internal void HudSpaceProbe()
    {
        BroadcastRaw("RULER SPACE - fenced 20, then bare", new List<string>
        {
            new string('|', 40),
            "||",
            "|" + new string(' ', RulerRepeats) + "|",
            "|" + new string(' ', RulerRepeats * 2) + "|",
        });
        Announce("Space page is up for as long as the HUD holds it. Screenshot it.");
    }
}
