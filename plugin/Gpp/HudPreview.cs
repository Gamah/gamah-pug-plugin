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
        if (WorldStyle) { WorldPreview(); return; }

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
    // The same idea for the world panel, with one difference that matters: each step is a function of
    // the clock rather than a fixed string, so the countdown really counts down. Every panel drawn in a
    // pug has a clock on it, and a clock that does not move is the bug this overlay was rewritten to
    // fix - the prototype only ever drew each panel once, so nobody had seen one tick.
    internal void WorldPreview()
    {
        var teamOne = PreviewNames.Take(5).ToList();
        var teamTwo = PreviewNames.Skip(5).Take(5).ToList();
        var pool = PreviewNames.Select((name, index) => WorldLayout.Cell(index + 1, name)).ToList();
        var ballot = Config.MapPool.Select((map, index) => WorldLayout.Cell(index + 1, map.Label)).ToList();
        var waiting = PreviewNames.Take(4).ToList();

        var steps = new List<(string Name, Func<int, string> Panel)>
        {
            ("captain sign-up", left => WorldLayout.Panel("want to captain?",
            [
                WorldLayout.Section.Of($"IN:  {string.Join("  ", teamOne.Take(2).Select(WorldLayout.Clip))}"),
                WorldLayout.Section.Of("STILL TO ANSWER"),
                WorldLayout.Section.List(WorldLayout.Grid(PreviewNames.Skip(2).Select(WorldLayout.Clip))),
            ], WorldLayout.Footer("type !captain", $"{left}s"), _hudTicks)),

            ("map vote, not voted yet", left => WorldLayout.Panel("pick a map",
            [
                WorldLayout.Section.List(WorldLayout.Grid(ballot)),
            ], WorldLayout.Footer("type the number in chat", $"{left}s"), _hudTicks)),

            ("map vote, already voted", left => WorldLayout.Panel("you voted mirage",
            [
                WorldLayout.Section.Of($"6/10 VOTED - WAITING ON"),
                WorldLayout.Section.List(WorldLayout.Grid(waiting.Select(WorldLayout.Clip))),
                WorldLayout.Section.Of("LEADING", "mirage ***    nuke **"),
            ], WorldLayout.Footer("!1-!9 changes your vote", $"{left}s"), _hudTicks)),

            ("waiting to reconnect", left => WorldLayout.Panel("waiting for players",
            [
                WorldLayout.Section.Of("6/10 BACK - STILL TO CONNECT"),
                WorldLayout.Section.List(WorldLayout.Grid(waiting.Select(WorldLayout.Clip))),
            ], WorldLayout.Footer("drafting anyway in", $"{left}s"), _hudTicks)),

            ("draft, your pick", left => WorldLayout.Panel("your pick",
            [
                WorldLayout.Section.List(WorldLayout.Grid(pool)),
                new([.. WorldLayout.Rosters("YOU", teamOne.Take(2), "THEM", teamTwo.Take(2))]),
            ], WorldLayout.Footer("type the number", $"{left}s", $"{pool.Count} left"), _hudTicks)),

            ("draft, in the pool", left => WorldLayout.Panel("Rouchard is picking",
            [
                new([.. WorldLayout.Pair("TEAM ROUCHARD", teamOne, "TEAM ADA", teamTwo)]),
                WorldLayout.Section.Of("you are in the pool - you play as soon as a captain picks you"),
            ], WorldLayout.Footer($"{left}s", "6 still in the pool"), _hudTicks)),

            ("ready", left => WorldLayout.Panel("type .ready in chat",
            [
                WorldLayout.Section.Of("6/10 READY - STILL TO READY"),
                WorldLayout.Section.List(WorldLayout.Grid(waiting.Select(WorldLayout.Clip))),
                new([.. WorldLayout.Rosters("TEAM ROUCHARD", teamOne, "TEAM ADA", teamTwo)]),
            ], WorldLayout.Footer("mirage"), _hudTicks)),

            ("side choice", left => WorldLayout.Panel("type .stay or .switch",
            [
                WorldLayout.Section.Of("CT won the knife round", ".stay keeps this side, .switch takes the other"),
                new([.. WorldLayout.Rosters("TEAM ROUCHARD", teamOne, "TEAM ADA", teamTwo)]),
            ], WorldLayout.Footer("mirage", "0-0"), _hudTicks)),

            ("move in discord", left => WorldLayout.Panel(MatchRules.OpeningLines(3)[0],
            [
                WorldLayout.Section.Of(MatchRules.OpeningLines(3)[1], MatchRules.OpeningLines(3)[2]),
                WorldLayout.Section.Of("STILL MOVING"),
                WorldLayout.Section.List(WorldLayout.Grid(waiting.Select(WorldLayout.Clip))),
                new([.. WorldLayout.Rosters("TEAM ROUCHARD", teamOne, "TEAM ADA", teamTwo)]),
            ], WorldLayout.Footer("type .ready when you are in", $"live in {left}s"), _hudTicks)),

            ("idle server", left => WorldLayout.Panel("idle server - fix it",
            [
                WorldLayout.Section.Of("the server has been idle for 3 hours,", "which makes the first round behave oddly"),
                WorldLayout.Section.Of("!reload   - load de_mirage again", "!restart  - restart the server", "!skip     - leave it as it is"),
            ], "type one of them in chat", _hudTicks)),
        };

        Announce($"World panel preview: {steps.Count} panels, {PreviewSeconds}s each. !pug preview stop ends it.");

        int tick = 0;
        StopPreview();
        _previewTimer = AddTimer(1.0f, () =>
        {
            int index = tick / PreviewSeconds;
            int left = PreviewSeconds - (tick % PreviewSeconds);
            bool first = tick % PreviewSeconds == 0;
            tick++;
            _hudTicks++;

            if (index >= steps.Count)
            {
                StopPreview();
                ClearWorldPanels();
                Announce("Preview done.");
                return;
            }

            var step = steps[index];
            if (first) Announce($"[{index + 1}/{steps.Count}] {step.Name}");
            string panel = step.Panel(left);
            foreach (var player in HumanPlayers()) ShowWorldPanel(player, panel);
        }, TimerFlags.REPEAT);
    }
}
