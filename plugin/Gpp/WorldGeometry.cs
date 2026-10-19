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
}
