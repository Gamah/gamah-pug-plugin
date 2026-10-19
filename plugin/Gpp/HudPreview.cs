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
    // Fifteen seconds a panel: long enough to read it, watch its clock move and decide, without the
    // gallery becoming something you have to sit through twice.
    private const int PreviewSeconds = 15;

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
        var teamA = PreviewNames.Take(5).ToList();
        var teamB = PreviewNames.Skip(5).Take(5).ToList();
        var pool = PreviewNames.Select((name, index) => WorldLayout.Cell(index + 1, name)).ToList();
        var ballot = Config.MapPool.Select((map, index) => WorldLayout.Cell(index + 1, map.Label)).ToList();

        // The panels that are about a list getting shorter are the ones that have to be watched moving:
        // a still screenshot of "waiting on four people" says nothing about whether the panel reads as
        // alive while they trickle in. So the list shrinks over the ten seconds the step is up, which is
        // what it does in a real pug.
        List<string> Dwindling(IEnumerable<string> names, int left)
        {
            int keep = Math.Clamp((left * names.Count()) / PreviewSeconds, 0, names.Count());
            return names.Take(Math.Max(1, keep)).Select(WorldLayout.Clip).ToList();
        }

        // Boards grow over the step for the same reason: the whole idea of the side boards is that a
        // name leaves the middle and lands under a captain.
        string BoardAt(string label, string captain, List<string> team, int filled) =>
            WorldLayout.TeamBoard(label, captain, team.Take(Math.Clamp(filled, 0, team.Count)),
                label == "A" ? WorldLayout.Align.Right : WorldLayout.Align.Left);

        var steps = new List<(string Name, Func<int, (string Main, string? Left, string? Right)> Boards)>
        {
            // Both halves move: the list of people who have not answered shrinks, and the line of people
            // who have grows by the same name. Watching one of them move and the other sit still is what
            // made this look broken.
            // Both halves move and both are grids: the list of people who have not answered shrinks by
            // a row, and the list of people who have grows by one.
            ("captain sign-up", left =>
            {
                var quiet = Dwindling(PreviewNames.Skip(2), left);
                var answered = PreviewNames.Skip(2).Except(quiet).Prepend(teamA[0]).Prepend(teamB[0]).ToList();
                return (WorldLayout.MainPanel("want to captain?",
                [
                    WorldLayout.Section.Of("IN"),
                    WorldLayout.Section.List(WorldLayout.Grid(answered.Select(WorldLayout.Clip))),
                    WorldLayout.Section.Of("STILL TO ANSWER"),
                    WorldLayout.Section.List(WorldLayout.Grid(quiet)),
                ], WorldLayout.Footer("type !captain", $"{left}s"), _hudTicks), null, null);
            }),

            ("map vote, not voted yet", left => (WorldLayout.MainPanel("pick a map",
            [
                WorldLayout.Section.List(WorldLayout.Grid(ballot)),
            ], WorldLayout.Footer("type the number in chat", $"{left}s"), _hudTicks), null, null)),

            ("map vote, already voted", left =>
            {
                var missing = Dwindling(PreviewNames.Skip(2), left);
                int cast = PreviewNames.Length - missing.Count;

                // Votes spread over the real map pool and climb as they land, which is what somebody who
                // has already voted is actually watching.
                var tally = Config.MapPool.Select((map, index) => (map.Label, Votes: 0)).ToList();
                for (int vote = 0; vote < cast && tally.Count > 0; vote++)
                {
                    int on = (vote * 3) % tally.Count;
                    tally[on] = (tally[on].Label, tally[on].Votes + 1);
                }
                var cells = tally.Where(entry => entry.Votes > 0)
                    .OrderByDescending(entry => entry.Votes)
                    .Select(entry => $"{WorldLayout.Clip(entry.Label)} {new string('*', entry.Votes)}")
                    .ToList();

                return (WorldLayout.MainPanel("you voted mirage",
                [
                    WorldLayout.Section.Of($"{cast}/{PreviewNames.Length} VOTED - WAITING ON"),
                    WorldLayout.Section.List(WorldLayout.Grid(missing)),
                    WorldLayout.Section.Of("VOTES"),
                    new([.. WorldLayout.Grid(cells)]),
                ], WorldLayout.Footer("!1-!9 changes your vote", $"{left}s"), _hudTicks), null, null);
            }),

            ("waiting to reconnect", left =>
            {
                var missing = Dwindling(PreviewNames.Skip(2), left);
                return (WorldLayout.MainPanel("waiting for players",
                [
                    WorldLayout.Section.Of($"{PreviewNames.Length - missing.Count}/{PreviewNames.Length} BACK - STILL TO CONNECT"),
                    WorldLayout.Section.List(WorldLayout.Grid(missing, 2)),
                ], WorldLayout.Footer("drafting anyway in", $"{left}s"), _hudTicks), null, null);
            }),

            // The draft is the step the side boards exist for, so the pool empties and the two boards
            // fill as it runs.
            ("draft, picks landing", left =>
            {
                int picked = PreviewSeconds - left;
                return (WorldLayout.MainPanel(left % 4 < 2 ? "your pick - type the number" : "Ada is picking",
                [
                    WorldLayout.Section.List(WorldLayout.Grid(pool.Skip(picked))),
                ], WorldLayout.Footer($"{left}s", $"{Math.Max(0, pool.Count - picked)} still to pick"), _hudTicks),
                    BoardAt("A", teamA[0], teamA.Skip(1).ToList(), picked / 2),
                    BoardAt("B", teamB[0], teamB.Skip(1).ToList(), picked / 2));
            }),

            ("ready", left =>
            {
                var unready = Dwindling(PreviewNames.Skip(2), left);
                return (WorldLayout.MainPanel("type .ready in chat",
                [
                    WorldLayout.Section.Of($"{PreviewNames.Length - unready.Count}/{PreviewNames.Length} READY - STILL TO READY"),
                    WorldLayout.Section.List(WorldLayout.Grid(unready)),
                ], WorldLayout.Footer("mirage"), _hudTicks),
                    BoardAt("A", teamA[0], teamA.Skip(1).ToList(), 4),
                    BoardAt("B", teamB[0], teamB.Skip(1).ToList(), 4));
            }),

            ("side choice", left => (WorldLayout.MainPanel("type .stay or .switch",
            [
                WorldLayout.Section.Of("CT won the knife round", ".stay keeps this side, .switch takes the other"),
            ], WorldLayout.Footer("mirage"), _hudTicks),
                BoardAt("A", teamA[0], teamA.Skip(1).ToList(), 4),
                BoardAt("B", teamB[0], teamB.Skip(1).ToList(), 4))),

            ("move in discord, 3 boards", left => (OpeningPreview(left), 
                BoardAt("A", teamA[0], teamA.Skip(1).ToList(), 4),
                BoardAt("B", teamB[0], teamB.Skip(1).ToList(), 4))),

            ("move in discord, 1 board", left => (OpeningPreview(left), null, null)),

            ("idle server", left => (WorldLayout.MainPanel("idle server - fix it",
            [
                WorldLayout.Section.Of("the server has been idle for 3 hours,", "which makes the first round behave oddly"),
                WorldLayout.Section.Of("!reload   - load de_mirage again", "!restart  - restart the server", "!skip     - leave it as it is"),
            ], "type one of them in chat", _hudTicks), null, null)),
        };

        // The four mounts, at the end, each holding the same three boards so the only thing that changes
        // is how they track the view. Turn your head during these: what is being judged is the lag and
        // the swing, not the text.
        //
        // These are steps rather than a command to type, so the whole thing is one !pug preview from
        // beginning to end. The anchor is put back to whatever the schema probe chose when the gallery
        // finishes.
        foreach (var mount in new[] { "orient", "attachment", "pawn", "teleport" })
        {
            string which = mount;
            steps.Add(($"mount: {which} - turn your head", left =>
            {
                UseAnchor(which);
                return (WorldLayout.MainPanel($"mount: {which}",
                [
                    WorldLayout.Section.Of("turn your head, look up, look down.",
                        "the boards should stay where they are relative to you."),
                    WorldLayout.Section.Of(MountNote(which)),
                ], WorldLayout.Footer($"{left}s"), _hudTicks),
                    BoardAt("A", teamA[0], teamA.Skip(1).ToList(), 4),
                    BoardAt("B", teamB[0], teamB.Skip(1).ToList(), 4));
            }));
        }

        Announce($"Gallery: {steps.Count} panels, {PreviewSeconds}s each, "
            + $"about {(steps.Count * PreviewSeconds) / 60}m{(steps.Count * PreviewSeconds) % 60:00}s. "
            + "Numbers are called out as they come. !pug preview stop ends it.");

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
                RestoreProbedAnchor();
                Announce("Gallery done - which numbers were wrong?");
                return;
            }

            var step = steps[index];
            if (first) Announce($"[{index + 1}/{steps.Count}] {step.Name}");
            var (main, boardLeft, boardRight) = step.Boards(left);
            foreach (var player in HumanPlayers()) ShowWorldPanel(player, main, boardLeft, boardRight);
        }, TimerFlags.REPEAT);
    }

    private string OpeningPreview(int left)
    {
        var lines = MatchRules.OpeningLines(3);
        return WorldLayout.MainPanel(lines[0],
        [
            WorldLayout.Section.Of(lines[1], lines[2]),
            WorldLayout.Section.Of("STILL MOVING"),
            WorldLayout.Section.List(WorldLayout.Grid(PreviewNames.Take(Math.Max(1, left / 2)).Select(WorldLayout.Clip))),
        ], WorldLayout.Footer("type .ready when you are in", $"live in {left}s"), _hudTicks);
    }
}
