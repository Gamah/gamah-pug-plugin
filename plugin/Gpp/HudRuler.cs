using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// A ruler for the centre panel, because every width in Hud.cs is currently a guess.
//
// The panel font is proportional and CS2 ships it as stratum2.uifont, an obfuscated container rather
// than a readable TTF, so the advance widths cannot be read off disk. They can be measured through the
// renderer instead: print a line that is nothing but twenty copies of one character, screenshot it, and
// the rendered width divided by twenty is that character's advance. Two reference lines on every page
// pin the scale - a bare "||" for the pipe's own width and forty pipes for the panel's usable width -
// so a page is self-contained and pages shot at different resolutions still compare.
//
//   !pug ruler            list the pages
//   !pug ruler b          hold page b on screen for twenty seconds
//
// One page at a time, held by a 1Hz redraw: PrintToCenter is transient, so a single print fades before
// anyone can take a screenshot. Twenty seconds is enough to alt-tab and still catch it.
//
// This is a measuring tool, not a feature. It lives here rather than in a scratch build because the
// widths it produces want re-measuring after a CS2 update, and a tool nobody can find never gets re-run.
public partial class Gpp
{
    private const int RulerRepeats = 20;
    private const int RulerHoldSeconds = 20;

    // Three characters a page. The first line is always forty pipes, which is the scale: the pipe
    // advance came out at exactly 4px, so every other width can be expressed in pipe units and two
    // screenshots taken at different resolutions still compare.
    private static readonly Dictionary<string, string[]> RulerPages = new()
    {
        ["a"] = ["i", "l", "I"],
        ["b"] = ["a", "e", "o"],
        ["c"] = ["s", "r", "t"],
        ["d"] = ["m", "w", "n"],
        ["e"] = ["M", "W", "A"],
        ["f"] = ["0", "8", "1"],
        ["g"] = ["-", "!", "."],
        ["h"] = ["C", "S", "T"],
    };

    private CounterStrikeSharp.API.Modules.Timers.Timer? _rulerTimer;

    internal void HudRulerProbe(string page, int from = 0)
    {
        page = page.Trim().ToLowerInvariant();

        if (page == "stop")
        {
            StopRuler();
            Announce("Ruler stopped.");
            return;
        }

        if (!RulerPages.ContainsKey(page) && page != "space" && page != "wrap")
        {
            Announce($"Ruler pages: {string.Join(" ", RulerPages.Keys)} space wrap. "
                + $"Each holds {RulerHoldSeconds}s - screenshot it. !pug ruler stop ends one early.");
            foreach (var (key, chars) in RulerPages) Announce($"  {key}: {string.Join(" and ", chars)}");
            return;
        }

        // The space has no ink, so it is measured as a gap between fences rather than as a run: twenty
        // spaces between two pipes, and forty between two more, so the pipes cancel out of the
        // difference and the space's advance falls out of it.
        var lines = page == "space"
            ? new List<string>
            {
                new string('|', 40),
                "||",
                "|" + new string(' ', RulerRepeats) + "|",
                "|" + new string(' ', RulerRepeats * 2) + "|",
            }
            : page == "wrap"
                // Where does the panel actually break a line? Four runs of pipes, climbing past the
                // budget: whichever is the first to appear on two rows is the limit, read straight off
                // in pipe units. Nothing else measures this - a line that fits tells you nothing except
                // that the limit is somewhere above it.
                ? WrapRun(from <= 0 ? 44 : from)
                : new List<string>
                {
                    new string('|', 40),
                    string.Concat(Enumerable.Repeat(RulerPages[page][0], RulerRepeats)),
                    string.Concat(Enumerable.Repeat(RulerPages[page][1], RulerRepeats)),
                    string.Concat(Enumerable.Repeat(RulerPages[page][2], RulerRepeats)),
                };

        // Deliberately three lines: this is also the test of whether the alert box honours a newline,
        // which the four-line vote and pick headers now depend on. If this comes out as one run, they
        // need a different separator.
        string label = page switch
        {
            "space" => $"RULER\nSPACE\n{RulerRepeats} then {RulerRepeats * 2}",
            "wrap" => $"RULER\nWRAP\n{string.Join(" ", WrapCounts(from <= 0 ? 44 : from))}",
            _ => $"RULER\n{page.ToUpperInvariant()}\n{string.Join(" ", RulerPages[page])} x{RulerRepeats}",
        };

        int left = RulerHoldSeconds;
        StopRuler();
        _rulerTimer = AddTimer(1.0f, () =>
        {
            if (--left <= 0)
            {
                StopRuler();
                StopHud();
                Announce("Ruler page done.");
                return;
            }
            BroadcastRaw(label, lines);
        }, TimerFlags.REPEAT);

        BroadcastRaw(label, lines);
        Announce($"{label} - holding {RulerHoldSeconds}s. Screenshot it.");
    }

    // Four runs a page, climbing by an eighth of the start so the steps stay proportionate however far
    // up the scale you have had to go. !pug ruler wrap 120 picks up where the last page gave out.
    private static int[] WrapCounts(int from) =>
        [from, from + from / 8, from + from / 4, from + (from * 3) / 8];

    private static List<string> WrapRun(int from) =>
        WrapCounts(from).Select(n => new string('|', n)).ToList();

    private void StopRuler()
    {
        _rulerTimer?.Kill();
        _rulerTimer = null;
    }
}
