# Next session: the world panel is live

The overlay is `point_worldtext` now, not `PrintToCenter`. Three boards per viewer - the middle one is
what the room is being asked for, the two beside it are the teams - transmitted only to the player they
belong to, parented to a `point_orient` that rides their own pawn.

`!pug preview` plays the whole gallery: ten panels, fifteen seconds each, numbers called out in chat.
That is the way to look at any change to the panels without running a pug.

## What was measured, and must not be re-learned the hard way

All on a 4K client, 2026-10-19. Each of these cost a round trip of screenshots.

- **A blank line is not drawn at all.** Padding a board to twelve lines with empty lines leaves it as
  tall as its content, so it shrinks as a list empties. Every blank line carries a `·` for this reason -
  gaps between sections included, or the gap is not there either.
- **Whitespace padding cannot hold a board's width.** The renderer keeps trailing spaces and trims
  leading ones, so a centre-justified board padded on one end draws its words off to one side; padding
  with U+00A0 instead moved the box 556 pixels the other way. The bottom line of every board is a rule
  of dashes, which is two visible glyphs at the extremes and nothing to trim.
- **`MessageText` alone re-flows correctly.** Setting it *and* `FontSize` does not - the glyphs are
  re-rendered without the lines being re-spaced. So size is set once, at spawn, and a ticking clock is
  one networked string a second.
- **SetParent freezes the offset at the moment it is called.** Parenting a board to an orient spawned in
  the same frame freezes it against that orient's arbitrary spawn rotation - a board 45 degrees out, for
  as long as it lives. Parent first, next frame, then teleport.
- **`CCSPlayer_ViewModelServices` and `CCSGOViewModel` are gone from the server schema** (Valve,
  2025-08-03; CSS deleted the types to match in 1.0.329). Viewmodel parenting cannot be hand-offset back
  into existence, and every plugin that tried broke. `point_orient` is the replacement and is in
  1.0.376.
- **One width for every board, every phase.** Three boards have to fit across a view that is 140 world
  units wide at the distance they hang at. `WorldGeometry` has no engine in it so the tests can assert
  that - `ThreeBoardsFitAcrossTheView` is the check that would have caught three separate overlap bugs
  without a screenshot.
- Mounts that were built, compared and thrown away: parenting the orient to a `look_straight_ahead_stand`
  attachment (indistinguishable from the plain orient - these models do not carry it), and parenting the
  boards to the pawn (swings on every turn, because the pawn's rotation is the body yaw).

## What is resolution-dependent and should be re-checked

Everything on screen is solved from an assumed 90 degree FOV on 16:9, because the server cannot read a
player's aspect ratio or FOV. It has only been looked at on one 4K client.

- `WorldGeometry.ScreenPercent` (48) and `BoardWidth` (500) are the two numbers to move if the boards
  come out too big or too small on another resolution. The test holds them honest against each other.
- `WorldGeometry.PixelsPerMetric` (2) is the only empirically measured constant: a board of 900
  HudMetrics units came out 95 world units wide on the 4K screenshot.
- `!pug sides <gap> [toe-in] [back]` moves the side boards live, and `!pug sides auto` hands the
  derived number back.

## Still worth doing

- **Nobody has run a pug on it.** The gallery has been looked at end to end; a real ten-man has not.
  `~/gpp/bot-test.sh` walks the flow with bots - `count_bots` must be true in `Gpp.json` for that, and
  **false** again afterwards.
- `hud_style: center` still draws the old `PrintToCenter` panels, which is the way to rescue a night
  without a rollback. Nothing has exercised that path since the world panel landed.
- The idle and restart panels have the least thought in them - they are outside the pug flow and were
  never the point.
- `Gpp.json` gained `hud_style` and `hud_side_gap`; the live config has neither, so both are defaulting.
