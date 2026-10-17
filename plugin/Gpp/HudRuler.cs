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

    // Two characters a page: four lines, two of which are the scale. The set spans the extremes and
    // covers the shapes that actually turn up in map names and player names.
    private static readonly Dictionary<string, string[]> RulerPages = new()
    {
        ["a"] = ["i", "l"],
        ["b"] = ["I", "1"],
        ["c"] = ["a", "e"],
        ["d"] = ["o", "s"],
        ["e"] = ["m", "w"],
        ["f"] = ["M", "W"],
        ["g"] = ["0", "8"],
        ["h"] = ["-", "!"],
    };

    private CounterStrikeSharp.API.Modules.Timers.Timer? _rulerTimer;

    internal void HudRulerProbe(string page)
    {
        page = page.Trim().ToLowerInvariant();

        if (page == "stop")
        {
            StopRuler();
            Announce("Ruler stopped.");
            return;
        }

        if (!RulerPages.ContainsKey(page) && page != "space")
        {
            Announce($"Ruler pages: {string.Join(" ", RulerPages.Keys)} space. "
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
            : new List<string>
            {
                new string('|', 40),
                "||",
                string.Concat(Enumerable.Repeat(RulerPages[page][0], RulerRepeats)),
                string.Concat(Enumerable.Repeat(RulerPages[page][1], RulerRepeats)),
            };

        string label = page == "space"
            ? $"RULER SPACE - {RulerRepeats} then {RulerRepeats * 2}"
            : $"RULER {page.ToUpperInvariant()} - {string.Join(" ", RulerPages[page])} x{RulerRepeats}";

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

    private void StopRuler()
    {
        _rulerTimer?.Kill();
        _rulerTimer = null;
    }
}
