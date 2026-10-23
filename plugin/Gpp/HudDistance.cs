using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// !pug distance - how far out should a floated board hang?
//
// The three-board layout sits seventy units in front of the eye, which is far enough to be inside a
// wall, a door frame or a dumpster in most of the places a pug stands. A lone floated board does not
// need that distance: how big it looks is the ratio of its size to its distance, so the same board at
// twenty-four units and a third of the scale is the same board on screen - and only reaches a quarter
// as far into the world.
//
// None of that can be settled from here. The server cannot read a client's FOV or aspect, the boards
// have only ever been looked at on one 4K screen, and whether a board reads as crisp or as a smear
// when it is a foot from the face is a property of the renderer. So this is the distance equivalent of
// !pug ruler: stand somewhere awkward, walk the board in and out with the apparent size held, and read
// the answer off a screenshot that says on its face what it was showing.
//
//   !pug distance              the steps, and what each one is for
//   !pug distance walk         every distance in turn, apparent size held constant
//   !pug distance float        every lateral offset in turn, at the recommended distance
//   !pug distance <units>      hold one distance until it is stopped
//   !pug distance stop         put the geometry back
//
// Everything it moves is an override that a pug does not read, and both the overrides it touches are
// put back when it ends - however it ends.
public partial class Gpp
{
    // Eight seconds a step: long enough to turn round, walk into a doorway and screenshot it, short
    // enough that the whole walk is under a minute.
    private const int DistanceHoldSeconds = 8;

    // The recommendation is twenty-four units, with one step either side and the current seventy as the
    // control at the end. Twenty-four because the board's far corners then sit twenty-five units from
    // the eye: still outside the player's own thirty-two unit hull, so it is not clipping the viewmodel,
    // but close enough that only a surface somebody is practically touching can get in front of it.
    internal const float FloatDistance = 24f;

    private static readonly float[] DistanceWalk = [18f, FloatDistance, 32f, WorldGeometry.Distance];

    // As a fraction of half the screen width, left of centre. The derived gap in use today is 0.65,
    // which puts the board's inner edge a third of the way out; the engine's own vote panel reads as
    // nearer the middle than that, and a board at 0.25 still overlaps the crosshair, so the question is
    // which of the two in between it is.
    private static readonly float[] FloatWalk = [0.25f, 0.33f, 0.45f, 0.65f];

    private CounterStrikeSharp.API.Modules.Timers.Timer? _distanceTimer;

    // Read by PanelsFloat: a probe board is a lone floated board whatever the config says.
    private bool _distanceProbeRunning;

    // What the overrides were before the probe touched them, so a stopped probe leaves the server the
    // way the gallery leaves the follow tier rather than on whatever it happened to show last.
    private float? _distanceWas;
    private float? _floatGapWas;

    // The lateral offset the server was using, as a fraction of half the view, captured before the
    // probe overwrote it. A fraction rather than a number of units because that is what stays put on
    // screen when the distance walk moves the board in: one variable a walk, or a step that reads
    // worse tells you nothing about which of the two did it.
    private float _fractionWas = 0.65f;

    internal void DistanceProbe(string arg, int units)
    {
        arg = arg.Trim().ToLowerInvariant();

        if (arg == "stop")
        {
            StopDistanceProbe();
            Announce("Distance probe stopped, geometry back as it was.");
            return;
        }

        // A single distance, held until somebody stops it: for walking a whole map with one number
        // rather than watching the clock.
        bool number = float.TryParse(arg, out float typed);
        if (units > 0 || number)
        {
            HoldDistance(Math.Clamp(units > 0 ? units : typed, 8f, 400f));
            return;
        }

        var steps = arg switch
        {
            "walk" => DistanceWalk.Select(distance => (distance, Fraction())).ToList(),
            // The lateral walk holds the distance and moves the board, which is the other half of the
            // question and the half that cannot be derived at all.
            "float" => FloatWalk.Select(fraction => (FloatDistance, fraction)).ToList(),
            _ => [],
        };

        if (steps.Count == 0)
        {
            Announce($"!pug distance walk - {DistanceWalk.Length} distances ({string.Join(", ", DistanceWalk.Select(d => $"{d:F0}"))}), "
                + $"{DistanceHoldSeconds}s each, apparent size held constant.");
            Announce($"!pug distance float - {FloatWalk.Length} lateral offsets at {FloatDistance:F0} units "
                + $"({string.Join(", ", FloatWalk.Select(f => $"{f:F2}"))} of half the view).");
            Announce("!pug distance <units> holds one distance; !pug distance stop puts it back. "
                + "Stand in a doorway for it - the point is what the board reaches into.");
            return;
        }

        Begin();
        Announce($"Distance probe: {steps.Count} steps, {DistanceHoldSeconds}s each. "
            + "Each board says what it is showing - screenshot the one that reads best and clips least. "
            + "!pug distance stop ends it.");

        int tick = 0;
        _distanceTimer = AddTimer(1.0f, () =>
        {
            int index = tick / DistanceHoldSeconds;
            int left = DistanceHoldSeconds - (tick % DistanceHoldSeconds);
            bool first = tick % DistanceHoldSeconds == 0;
            tick++;
            _hudTicks++;

            if (index >= steps.Count)
            {
                StopDistanceProbe();
                Announce("Distance probe done. Which step, and did any of them sit inside a wall?");
                return;
            }

            var (distance, fraction) = steps[index];

            // The geometry is applied on the step's first tick and the boards are hung again, because
            // WorldUnitsPerPx is only read when a board is spawned - so a scale change is a respawn,
            // which is exactly what Rehang does.
            if (first)
            {
                Apply(distance, fraction);
                Announce($"[{index + 1}/{steps.Count}] {distance:F0} units out, "
                    + $"{WorldGeometry.LateralFor(fraction, distance):F1} left ({fraction:F2} of half the view)");
            }

            string board = DistanceBoard(index + 1, steps.Count, distance, fraction, left);
            foreach (var player in HumanPlayers()) ShowWorldPanel(player, board);
        }, TimerFlags.REPEAT);
    }

    // One distance, no clock: the board says what it is and stays there until it is stopped.
    private void HoldDistance(float distance)
    {
        Begin();
        Apply(distance, Fraction());

        string board = DistanceBoard(0, 0, distance, Fraction(), 0);
        foreach (var player in HumanPlayers()) ShowWorldPanel(player, board);

        // A 1Hz redraw for the same reason the rest of the overlay has one: a respawn or a map change
        // takes the boards with it, and a probe that quietly stops drawing looks like a verdict.
        _distanceTimer = AddTimer(1.0f, () =>
        {
            _hudTicks++;
            string held = DistanceBoard(0, 0, distance, Fraction(), 0);
            foreach (var player in HumanPlayers()) ShowWorldPanel(player, held);
        }, TimerFlags.REPEAT);

        Announce($"Holding a floated board at {distance:F0} units, scale {WorldGeometry.ScaleAt(distance):F4}, "
            + $"{WorldGeometry.LateralFor(Fraction(), distance):F1} to the left. !pug distance stop puts it back.");
    }

    // What the board says about itself, so a screenshot needs no caption. Twelve lines like every other
    // board, built by the same MainPanel the live panels are built by, so the thing being judged is the
    // real furniture rather than a mock-up of it.
    private string DistanceBoard(int index, int count, float distance, float fraction, int left)
    {
        float scale = WorldGeometry.ScaleAt(distance);
        float width = WorldGeometry.At(WorldGeometry.BoardWidth(WorldLayout.BoardWidth), distance);
        float lateral = WorldGeometry.LateralFor(fraction, distance);

        return WorldLayout.MainPanel(
            count > 0 ? $"step {index}/{count} - {distance:F0} units out" : $"held at {distance:F0} units out",
            [
                WorldLayout.Section.Of(
                    $"distance   {distance:F0}",
                    $"scale      {scale:F4}",
                    $"width      {width:F1} units",
                    $"left       {lateral:F1} ({fraction:F2} of half)"),
                WorldLayout.Section.Of(
                    "every step is the same size on screen.",
                    "what changes is what it reaches into",
                    "and how far the eye has to travel."),
                WorldLayout.Section.Of(
                    "stand in a doorway, by a wall, next to",
                    "a crate. screenshot the ones that clip."),
            ],
            count > 0 ? WorldLayout.Footer($"{left}s", "!pug distance stop") : WorldLayout.Footer("!pug distance stop"),
            _hudTicks);
    }

    private float Fraction() => _fractionWas;

    private void Begin()
    {
        StopDistanceProbe();
        _distanceWas = _distanceOverride;
        _floatGapWas = _floatGapOverride;
        // Read before anything is overridden, and at the distance it was tuned at, or the probe would
        // be measuring its own last step.
        _fractionWas = WorldGeometry.FractionFor(FloatGap, PanelDistanceNow);
        _distanceProbeRunning = true;
    }

    private void Apply(float distance, float fraction)
    {
        _distanceOverride = distance;
        _floatGapOverride = WorldGeometry.LateralFor(fraction, distance);
        Rehang();
    }

    internal void StopDistanceProbe()
    {
        _distanceTimer?.Kill();
        _distanceTimer = null;
        if (!_distanceProbeRunning) return;

        _distanceProbeRunning = false;
        _distanceOverride = _distanceWas;
        _floatGapOverride = _floatGapWas;
        _distanceWas = null;
        _floatGapWas = null;
        ClearWorldPanels();
    }
}
