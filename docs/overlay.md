# The overlay

Everything the pug asks the room for is drawn in the world, not in chat: three `point_worldtext` boards
per player, the middle one carrying whatever is being asked for and the two beside it carrying the
teams. Each player gets their own entities, filtered out of everybody else's `CheckTransmit`, so ten
people do not read ten overlapping boards hanging in the same room.

`!pug preview` plays every panel in order - ten of them, fifteen seconds each, numbers called out in
chat - which is how to look at a change without running a pug. `hud_style: center` in `Gpp.json` goes
back to the old `PrintToCenter` panels, which is there to rescue a night by flipping a setting rather
than rolling the plugin back.

## What the boards are

| | |
| --- | --- |
| Middle | The instruction, as a title, and whatever has to be read to act on it. Twelve lines. |
| Left, right | Team A and Team B, in that order for everybody. Label, captain, then ten slots. |

Team A is always the left board for every viewer, whatever side they end up on. Ten people looking at
the same board is what lets somebody say "you're on A" out loud and have it mean something.

The boards are up from `!pug start` until the room is told to move in Discord, and deliberately absent
for both knife fights - a board is a solid object in front of the face, and a knife round is the one
part of a pug that is aim. A pause mid-match keeps the centre-screen text instead.

## Measured, and not to be re-learned the hard way

All on a 4K client, 2026-10-19. Every one of these cost a screenshot and a round trip.

- **A blank line is not drawn at all.** Padding a board to twelve lines with empty lines leaves it as
  tall as its content, so it shrinks as a list empties - and a gap between two sections is not there
  either. Every blank line carries a `·`.
- **Whitespace cannot hold a board's width.** The renderer keeps trailing spaces and trims leading ones,
  so a centre-justified board padded on one end draws its words off to one side; padding with U+00A0
  instead moved the box 556 pixels the other way. The bottom line of every board is a rule of dashes -
  two visible glyphs at the extremes and nothing to trim - and that pins the height as well.
- **`MessageText` alone re-flows correctly; `MessageText` with `FontSize` does not.** Setting both
  re-renders the glyphs without re-spacing the lines and the panel comes out overlapped. Size is set
  once, at spawn, so a ticking clock costs one networked string a second.
- **`SetParent` freezes the entity's offset from its parent at the moment it is called.** A board
  parented to a `point_orient` spawned in the same frame is frozen against that orient's arbitrary spawn
  rotation and comes out skewed for as long as it lives. Parent on the next frame, then teleport.
- **Scale with `WorldUnitsPerPx`, never `FontSize`.** `FontSize` grows the glyphs without moving the
  lines apart, so past a certain size the text grows into itself.
- **`ReorientMode` must be `NONE`,** and the angles are `(0, yaw + 270, 90 - pitch)`. The roll of 90 is
  what stands the text plane up; without it the board lies flat like a rug. Taking the pitch back out of
  the roll is what stops it tilting when somebody looks up.

## The mount

The boards hang off a `point_orient` parented to the pawn and aimed down its eyeline (`eEyesForward`),
so position and facing both ride the client's own prediction of its pawn. Nothing is sent per tick and
there is nothing to interpolate.

**Viewmodel parenting is not an option and is not worth another look.** Valve removed
`CCSPlayer_ViewModelServices` and `CCSGOViewModel` from the server schema on 2025-08-03 and
CounterStrikeSharp deleted the generated types to match in 1.0.329, so there is no field left to point
at - plugins that hardcoded the offset broke, and the big menu libraries deleted the feature outright.
`Schema.GetSchemaOffset` answers 0 for a field that is not there, silently, and `entity.Handle + 0` is
the vtable: reads come back as plausible garbage and writes segfault. `ProbeFollow()` checks the schema
at load for that reason, and falls back to moving the boards by hand if `point_orient` ever goes the
same way.
[SOURCE: CounterStrikeSharp commit `af3bb52` and its generated schema, read 2026-10-19.]

Two other mounts were built, compared on a client and thrown away: parenting the orient to a
`look_straight_ahead_stand` attachment on the player model, which was indistinguishable from the plain
orient because these models do not carry it, and parenting the boards straight to the pawn, which swings
them about on every turn because the pawn's rotation is the body yaw rather than the aim.

## Sizes, and what is assumed

One width for every board, every phase. A middle board that is one size on its own and another with the
team boards beside it is three sizes of furniture, and furniture moving is what the eye follows instead
of the words.

The server cannot read a player's aspect ratio or FOV, so everything is solved from an assumed 90 degree
horizontal FOV on 16:9. At the distance the boards hang at the view is 140 world units across, and three
boards plus their margins come to about 132 - which is what sets the type size, not taste.

`WorldGeometry` holds that arithmetic and has no CounterStrikeSharp in it, so `Gpp.Tests` can assert the
thing that went wrong three times: `ThreeBoardsFitAcrossTheView` fails if the boards would overlap, or
if they are further apart than they need to be. `NoPanelEverChangesSize` measures every panel's worst
case and fails if any of them comes out a different size, or contains a line that would not be drawn.

Only one 4K client has ever looked at this. If another resolution needs different numbers,
`WorldGeometry.ScreenPercent` and `WorldLayout.BoardWidth` are the two to move - the test keeps them
honest against each other - and `!pug sides <gap> [toe-in] [back]` moves the side boards live, with
`!pug sides auto` handing the derived number back.

`WorldGeometry.PixelsPerMetric` is the only empirically measured constant in the file: a board of 900
`HudMetrics` units came out 95 world units wide on the screenshot it was taken from.

## Lifecycle

A leaked `point_worldtext` hangs in the world until the map changes, so every way a board can end goes
through `HideWorldPanel`/`ClearWorldPanels`: phase end, disconnect, map change, plugin unload. Writing
the dll *is* a reload, so `Unload` has to take the boards with it or a hot deploy leaves one in front of
everybody's face with no plugin left to clear it. `SweepStrayPanels` runs on map start and removes any
`point_worldtext` or `point_orient` left behind by a crash or a bad deploy. A respawn means a new pawn,
which means the old parent is gone - `KeepUp` notices and rehangs.
