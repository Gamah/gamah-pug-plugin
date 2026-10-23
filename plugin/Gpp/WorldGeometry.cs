namespace Gpp;

// Where the boards are and how big they come out, with nothing from CounterStrikeSharp in it so the
// tests can check the one thing that has gone wrong over and over: three boards that do not fit across
// the view, and so end up on top of each other.
//
// None of this can be read off the client. The server cannot see a player's FOV or aspect ratio, so the
// screen is assumed to be 16:9 at 90 degrees horizontal, and everything is solved from that.
internal static class WorldGeometry
{
    // The panel is a block of text at a known distance, so the share of the screen it fills comes from
    // the angle it subtends: a block of height H at distance D fills 2*atan(H/2D).
    internal const float AssumedVerticalFov = 59f;
    internal const float AssumedHorizontalFov = 90f;
    internal const float LineHeightFactor = 1.2f;
    internal const float BaseFontSize = 64f;

    // How far in front of the eye the boards float, and how much of the screen height the twelve lines
    // fill. The type is small because three boards of the width a pick list needs only fit at this size.
    internal const float Distance = 70f;
    internal const float ScreenPercent = 48f;

    // Margin between one board and the next, in world units.
    internal const float Margin = 4f;

    // World units per text pixel: what the entity's WorldUnitsPerPx is set to, so twelve lines of
    // FontSize come out filling ScreenPercent of the view.
    internal static float Scale()
    {
        double half = AssumedVerticalFov * Math.PI / 360.0;
        double worldHeight = 2.0 * Distance * Math.Tan(half) * (ScreenPercent / 100.0);
        return (float)(worldHeight / (WorldLayout.PanelLines * BaseFontSize * LineHeightFactor));
    }

    // A board's width in world units. The two pixels per HudMetrics unit is the only measured part of
    // this: a board of 900 HudMetrics units came out 95 world units wide on a 4K screenshot.
    // [SOURCE: measured from a client screenshot, 2026-10-19.]
    internal const float PixelsPerMetric = 2f;

    internal static float BoardWidth(int metrics) => metrics * PixelsPerMetric * Scale();

    // How wide the view is in world units at the distance the boards hang at.
    internal static float ScreenWidth()
    {
        double half = AssumedHorizontalFov * Math.PI / 360.0;
        return (float)(2.0 * Distance * Math.Tan(half));
    }

    // Centre to centre between the middle board and a side board: one whole board plus a margin,
    // because every board is the same width. Any less and they overlap; any more and the outer two walk
    // off the edge of the view.
    internal static float SideGap() => BoardWidth(WorldLayout.BoardWidth) + Margin;

    // What the three boards occupy altogether, edge to edge.
    internal static float ThreeBoardSpan() => (2f * SideGap()) + BoardWidth(WorldLayout.BoardWidth);

    // --- moving a board nearer without changing the size it looks ----------------------------------

    // How big something looks is the ratio of its size to its distance, so a board brought closer keeps
    // the size it looks only if everything measured off the eye comes with it: the type scale, the lift
    // above the crosshair, the gap to the next board and the margin between them. All of those are
    // degree one in the distance - a board of height H at D subtends the same angle as kH at kD - and
    // this is the one place that says so, so nothing has to remember to scale by hand.
    internal static float At(float value, float distance) => value * distance / Distance;

    // What WorldUnitsPerPx becomes at another distance. Scale() is itself linear in Distance, so this
    // is the same number Scale() would answer if Distance were the argument.
    internal static float ScaleAt(float distance) => At(Scale(), distance);

    internal static float SideGapAt(float distance) => At(SideGap(), distance);

    // How far to one side a flat board's centre has to go to land at `fraction` of half the screen
    // width. Screen position is linear in the tangent of the angle rather than in the angle, so this is
    // a multiplication and not an arc - and the whole of the FOV assumption lives in the tangent, which
    // is why it is an argument: at 90 degrees the half width is the distance itself, and at 106 (which
    // is what 90 on 16:9 becomes on a 21:9 screen, the aspect being Hor+) it is 1.327 times that.
    internal static float LateralFor(float fraction, float distance, float horizontalFov = AssumedHorizontalFov) =>
        (float)(fraction * distance * Math.Tan(horizontalFov * Math.PI / 360.0));

    // The same thing read backwards: where a board that is this far to one side actually lands, as a
    // fraction of half the view. A board tuned at 90 and looked at on 106 sits at 1/1.327 of where it
    // was put, which is the failure this is here to make arithmetic rather than a surprise.
    internal static float FractionFor(float lateral, float distance, float horizontalFov = AssumedHorizontalFov) =>
        (float)(lateral / (distance * Math.Tan(horizontalFov * Math.PI / 360.0)));
}
