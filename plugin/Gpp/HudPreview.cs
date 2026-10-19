using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// !pug preview - every panel the pug can draw, back to back, without running a pug.
//
// The panels are built by calling the same Columns() and Paint() the live code calls, with the real
// map pool out of the config. A preview that rendered its own approximation of the layout would agree
// with itself and tell us nothing; the point is to see what a vote will actually look like before ten
// people are waiting on it.
//
// Each step holds for PreviewSeconds on a 1Hz redraw, because PrintToCenter is transient and a single
// print fades before anyone can look at it, let alone screenshot it.
public partial class Gpp
{
    private const int PreviewSeconds = 10;

    // Names chosen to break things: the widest glyphs in the font (m, W), a decorated name of the kind
    // people actually use, and a couple of ordinary ones for contrast.
    private static readonly string[] PreviewNames =
    [
        "Ada", "Baz", "Cosmo", "Wwwwwwwwwww", "mmmmmmmmmm",
        "☆Supreme☆Toxicity☆", "Fitzwilliam", "Rouchard", "Slingshot", "Enforcer",
    ];

    private CounterStrikeSharp.API.Modules.Timers.Timer? _previewTimer;

    internal void StopPreview()
    {
        _previewTimer?.Kill();
        _previewTimer = null;
    }

    internal void HudPreview()
    {
        var steps = BuildPreview();

        Announce($"Preview: {steps.Count} panels, {PreviewSeconds}s each "
            + $"({steps.Count * PreviewSeconds}s total). !pug preview stop ends it.");

        int tick = 0;
        StopPreview();
        _previewTimer = AddTimer(1.0f, () =>
        {
            int index = tick / PreviewSeconds;
            bool first = tick % PreviewSeconds == 0;
            tick++;

            if (index >= steps.Count)
            {
                StopPreview();
                StopHud();
                Announce("Preview done.");
                return;
            }

            var (name, alert, panel) = steps[index];
            if (first) Announce($"[{index + 1}/{steps.Count}] {name}");
            BroadcastRaw(alert, panel);
        }, TimerFlags.REPEAT);
    }

    private List<(string Name, string Alert, List<string> Panel)> BuildPreview()
    {
        var steps = new List<(string, string, List<string>)>();

        // The ballot, paged exactly as a vote pages it, with the server's own map pool.
        var ballot = Config.MapPool.Count > 0 ? Config.MapPool : new List<MapEntry>();
        var mapCells = ballot
            .Select((map, index) => $"!{index + 1} - {map.Label}")
            .ToList();

        const int votePage = 6;
        int votePages = Math.Max(1, (mapCells.Count + votePage - 1) / votePage);
        for (int page = 0; page < votePages; page++)
        {
            steps.Add((
                $"vote page {page + 1}/{votePages}",
                Alert("VOTE NOW!", "69s", $"0/{PreviewNames.Length} votes",
                    votePages > 1 ? $"page {page + 1}/{votePages}" : null),
                Columns(mapCells.Skip(page * votePage).Take(votePage))));
        }

        // Someone who has already voted sees the same list under a calmer header.
        steps.Add((
            "vote, already voted",
            Alert("VOTED", "69s", $"7/{PreviewNames.Length} votes", "page 1/" + votePages),
            Columns(mapCells.Take(votePage))));

        // The pick list, including the names that are hardest to fit.
        var pickCells = PreviewNames.Select((name, index) => $"!{index + 1} - {name}").ToList();
        steps.Add((
            "pick, your turn",
            Alert("PICK NOW!", "30s", $"{pickCells.Count} left", "page 1/2"),
            Columns(pickCells.Take(6))));

        steps.Add((
            "pick, someone else on the clock",
            Alert("☆Supreme☆Toxicity☆ IS PICKING", "30s", "4 left", null),
            Columns(pickCells.Skip(6))));

        // The knife and the ready gate, which are the two panels nobody has looked at since the widths
        // changed underneath them.
        steps.Add((
            "captain knife",
            Alert("KNIFE FOR FIRST PICK", "vs ☆Supreme☆Toxicity☆", "45s"),
            ["first blood takes first pick", "nobody else can join in"]));

        steps.Add((
            "ready gate",
            Alert("TYPE .ready IN CHAT", "4/10 ready"),
            ["teams are set on Cobblestone2018",
             "still to ready:",
             .. Columns(PreviewNames.Take(6))]));

        steps.Add((
            "ready gate, already readied",
            Alert("YOU ARE READY", "9/10 ready"),
            ["teams are set on Cobblestone2018",
             "still to ready:",
             .. Columns(PreviewNames.Take(1))]));

        return steps;
    }
}
