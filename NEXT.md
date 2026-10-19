# Next session: move the overlay to world text

The pug's overlay is `PrintToCenter` text, which is four lines of about eighty characters, fades unless
redrawn, and cannot do colour or size. Everything in `HudMetrics.cs`, `hud_line_budget`, the scroller,
`!pug wrap` and the 10Hz redraw loop exists to work around that.

A prototype of the replacement is in `plugin/Gpp/WorldPanel.cs` (`!pug panel`, admin). It spawns a
`point_worldtext` - a stock entity, nothing downloaded, no workshop addon - and draws a map vote and an
eighteen-name draft list. The owner has looked at it on a 4K client and called it passable. **The job is
to make it the plugin's overlay.**

## Why the panels exist at all

This plugin's purpose is to get ten to twenty people's attention and keep a pug moving. The panel is how
the server tells the room what it is waiting for. So:

- **Use a panel for every phase**, including the ones that are just waiting on people. "Nothing on screen"
  is the failure mode the whole overlay was built to fix.
- **There is far more room now.** Show more of what the plugin already knows rather than the minimum:
  who has readied and who has not, who is still to connect, the score, which side has to move in Discord,
  how long is left. Use data the plugin already has - this is a presentation job, not a new feature.
- **Chat stays the input.** `!1`, `.ready`, `!captain`. Only the display changes.

## What the prototype already established

Measured on the server, 2026-10-19. Do not re-learn these the hard way:

- **Scale the block with `WorldUnitsPerPx`, never `FontSize`.** `FontSize` grows the glyphs without
  moving the lines apart, so past a certain size the text grows into itself. `FontSize` stays at 64 and
  `WorldUnitsPerPx` does the sizing.
- **One text size for every panel.** Sizing to fit made a long list smaller than a short one, which reads
  as the panel moving rather than the content changing. A panel with more lines is taller, not smaller.
- **Changing text on a live entity broke the layout.** Setting `MessageText` *and* `FontSize` on an
  existing entity re-rendered the glyphs without re-flowing the lines, so the second panel came out
  overlapping. The prototype now deletes the entity and spawns a new one per panel. **See the open
  question below - this matters enormously for ticking clocks.**
- **Orientation**: `ReorientMode` must be `NONE`, and the angles are `(0, eyeYaw + 270, 90)`. The roll of
  90 is what stands the text plane up; without it the panel lies flat like a rug. Auto-reorient around
  the up axis does not help, because a flat panel's up axis is wrong.
- **It trails your view by about a tenth of a second** and no update rate fixes that: it is a networked
  entity and the client interpolates its position. The only real fix is parenting to the viewmodel, which
  CounterStrikeSharp 1.0.376 does not expose (`CCSPlayer_ViewModelServices` is absent from its generated
  schema) - it needs raw offsets. `!pug panel` follows the view; the prototype also has a mode that
  plants the panel in the room and leaves it, which has no lag because nothing moves.
- **Tuned numbers that looked right**: 75% of screen height, 70 units in front of the eye, 9 units above
  the crosshair, background on. Screen share is solved from an assumed 90 degree FOV on 16:9, so anyone
  on a different aspect or FOV sees something different - the server cannot read either.
- **Columns are space-padded against a proportional font**, so they drift by up to a space. Three across
  with names clipped to twelve characters reads fine. Exact columns need one entity per column.
- **Numbers carry the bang** (`!4  Fitzwillia`) because that is what has to be typed.

## The open question to answer first

The prototype respawns the entity whenever the text changes, because changing `MessageText` *and*
`FontSize` together broke the line flow. **Nobody has tested changing `MessageText` alone.** Everything
below depends on which it is:

- If `MessageText` alone re-flows correctly, a ticking clock is one networked string update a second and
  this is all straightforward.
- If it does not, every clock tick means deleting and respawning an entity per player - ten players is
  ten entity spawns a second, which is entity-index churn the server should not be doing during a pug.
  In that case the design has to change: split the clock onto its own small entity (one that only ever
  shows a number, so re-flow does not matter), or stop showing a live countdown.

Test this before writing any of the refactor. It is twenty minutes with `!pug panel` and a timer.

**The owner explicitly wants the timers to tick** - the countdowns never updated in any of the tests so
far, because the prototype only ever drew each panel once.

## Hard requirements before this goes near a real pug

1. **One entity per viewer, transmitted only to them.** `CheckTransmit` is available in CSS 1.0.376
   (`RegisterListener<Listeners.CheckTransmit>`). Without it every player sees everybody's panel floating
   in the room - with ten people that is ten overlapping boards. This is not optional.
2. **Lifecycle.** Entities must be removed on: panel change, phase end, player disconnect, map change,
   plugin unload. A leaked `point_worldtext` stays in the world until the map does.
3. **Nothing on screen during a live round.** The panel is a world object in front of the player's face;
   it must be gone when a round is live. Today's match HUD only draws during knife, side choice and
   pauses - keep that discipline.
4. **Keep the centre panel behind a config switch** (`hud_style: center | world`) for the first live pug,
   so the night can be rescued by flipping a setting rather than a rollback.
5. **Do not break chat input.** Every command still works exactly as it does now.

## The panels to build, and what each could show

Existing painters are in `Hud.cs` (`PaintVote`, `PaintDraft`, `PaintKnife`, `ReadyHudPaint`,
`MatchHudPaint`), `CaptainOptIn.cs`, `IdleReload.cs`, `OpeningPause.cs`, `Restart.cs`. Each has the data
already; the extra state below needs no new plumbing.

| Panel | Today | Could also show |
| --- | --- | --- |
| Captain sign-up | who volunteered, clock | who is connected but has not answered |
| Map vote | ballot, votes each, clock | who has not voted yet |
| Captain knife | who is duelling, clock | the pool waiting to be drafted |
| Draft | pick list, whose turn, clock | both teams as they fill, who is left, pick order |
| Ready | who has not readied, count | both rosters, the map, who is not connected |
| Knife round / side choice | what to type | the knife result, both team names |
| Pause | who paused, whether it has landed | which side still has to `.unpause`, the score |
| Discord hold (round 1) | the shouted three lines, who is in | both rosters, the score at 0-0 |
| Idle server | the three options | how long it has been idle, who just connected |
| Restart | the countdown | why |

## How to work on it

- Build: `dotnet build -c Release plugin/Gpp/Gpp.csproj` (dotnet is at `~/.local/share/toolchains/dotnet10`).
  **Check the exit code, not the output text** - a grep for "error" succeeds when the build fails, which
  redeployed a stale dll twice in one night.
- Tests: `dotnet test plugin/Gpp.Tests/Gpp.Tests.csproj` (48, all green). `MatchRules`, `StatsStore` and
  `HudLayout` are engine-free on purpose so they can be run here. Keep anything new that is arithmetic
  on that side of the line.
- Deploy: copy `Gpp.dll` to `addons/counterstrikesharp/plugins/Gpp/` on `cs2` (helpers in `~/gpp`, see
  `CLAUDE.md` in the cs2server notes for sudo). Writing the dll *is* the reload.
- **Read `addons/counterstrikesharp/logs/log-Gpp<date>.txt`, not the journal** - journald mangles the
  game's stdout and plugin lines come out spliced mid-word or as `[162B blob data]`.
- Bots for testing: `~/gpp/bot-test.sh` (`bots`, `captains`, `draft`, `ready`, `stay`, `short`, `status`,
  `stop`, `walk`). `count_bots` must be true in `Gpp.json` for bots to count as players - **set it back to
  false afterwards** or a stray bot joins the next real pug's draft.

## Traps that have already cost a night

- **A chat command must not do its work inline.** Netchan message processing has a CPU budget and drops
  the client whose message overran it. Everything goes through `AfterTick`.
- **Never kill a repeating timer from inside another timer's tick.** CSS walks its timer vector by index;
  killing a different timer corrupts the walk and segfaults the server next frame.
- **A hot reload leaves the previous `say` listener registered**, so dot commands answer once per load
  until the server restarts. There is a 250ms dedupe guarding it.
- **`!pug stop` is the recovery command.** It ends everything and lands in a held warmup.

## Ask

The owner would rather answer questions than review a wrong guess, and has said so. Ask about anything
material - in particular: whether a panel should ever be visible during a live round, how much of the
roster to show while waiting, whether the panel should follow the view or stand in the room, and what
happens on a twenty-man pug where even three columns of eighteen names is a tall panel.
