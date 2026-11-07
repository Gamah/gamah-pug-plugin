# How a pug runs: the player page

*If you are running it rather than playing it, [this is your page](admin.md).*

Teams get picked, you knife for sides, you play. The overlay tells you what it wants at every step;
this is the same thing written down, so you can read it before you are standing in it.

Type commands in chat. `.ready` and `!ready` are the same - `.` is what everyone uses. A bare number
(`!3`) is the answer to whatever is on screen.

## The flow

**1. The map vote.** An admin runs `!pug start` and the ballot goes up for 45 seconds. Type `!1`,
`!2`, `!3` for the map you want. It closes early once everybody has voted.

**2. Captains, at the same time.** `.captain` while the vote is up puts your hand up. Two get drawn
from whoever volunteered - fewer than two and the rest is at random, so *somebody* is captaining
whether anyone offered or not. `.captain` again takes your hand back down.

**3. The map changes** and you reconnect. The draft waits up to 90 seconds for the two captains to
load; if one never comes back the armband goes to somebody who is actually here. Anyone who connects
while picks remain joins the pool.

**4. The captains knife.** A 1v1 for first pick, up to 60 seconds. Everybody else watches.

**5. The draft.** Everyone undrafted is held in spectator - you cannot join a team, and the overlay
shows the pool with a number against each name. When it is your turn as captain, `!4` picks player 4.
`.players` reprints the pool, `!draft <name>` picks by name.

Your number is yours for the whole draft. Picking `!3` does not renumber the people below them.

**6. Ready up.** Teams are set, the warmup is held open, and the overlay counts 90 seconds while
people land. Type `.ready` - everybody has to, so the panel is waiting on whoever has not. `.unready`
takes it back. An admin can `.forceready` past somebody who has gone to make a sandwich.

**7. The knife round.** One round, knives only, for which side you play. The winning side's captain
then types `.stay` to keep it or `.switch` to take the other one.

**8. LIVE.** 24 rounds, 15 second halftime, overtime if it needs it. The demo is recording and the
stats are counting.

**Round one holds for a moment** so everyone can get into the Discord channel. `.ready` again when
you are in it, and the round starts when the room has.

**9. It ends.** Scores and players are written, the engine shows its end-of-match screen for 15
seconds, and the server goes back to a held warmup, ready for the next one.

## What you can type

| | |
| --- | --- |
| `!<number>` | Votes during a map vote, picks during a draft. The one command you actually need |
| `.captain` | Put your hand up to captain, while the sign-up is open |
| `.players` / `.who` | Who is here, numbered - privately to you. The draft pool, during a draft |
| `.ready` / `.r` | Ready up. `.gaben` also works, for reasons |
| `.unready` / `.ur` | Take it back |
| `.stay` / `.switch` | Keep or swap sides after the knife round. The winning captain's call |
| `.pause` | Pause from the end of the round. Both sides have to `.unpause` to come back |
| `.tech` | A technical pause, for somebody whose game has fallen over |
| `.unpause` | Ask to resume - your side asking is half of it |
| `.clear` | Close whatever the plugin has put on your screen |
| `!link-discord` | A code to tie your Steam account to Discord |

## Warmup only

Between pugs the server is yours to mess about on. None of this works once a pug is running.

| | |
| --- | --- |
| `.smokes` | Smoke practice: grenade trails, smokes only, invulnerable, and your throws come back. `.smokes off` to stop |
| `.map` | The map list, numbered, on a board beside you. `.map <number>` asks for that one |
| `.kill` | Yourself |
| `.slap` | Fling yourself about. No damage |
| `.fent` / `.drug` | Spin or warp your own view, up to 30 seconds |
| `.narcan` | Undo `.fent` and `.drug` |

`.map` needs an admin to `.yes` it, or - with no admin connected - everybody to agree.

## Things that catch people out

- **You cannot join a team during the draft.** That is deliberate: being in spectator is how the draft
  knows you are undrafted. You get put on a side the moment you are picked.
- **Nothing times out at the ready step.** The 90 second countdown is the overlay nagging; `.ready`
  still starts the match long after it has run out.
- **A bare `!1` outside a vote or a draft does nothing at all.** It is not a command that failed - mid
  match it is just somebody talking.
- **If the match is level at the end** you get asked whether to play overtime: `!1` for overtime, `!2`
  for a draw. The match is paused while the room decides.
- **Join mid-match and you land in spectator.** Press M and pick a side. Picking during freeze time
  spawns you into the round about to run; mid-round you wait for the next one.
- **An admin's `.kick` keeps you out for the rest of that pug.** It is not a ban - when the pug is
  over you can walk back in.
- **`.spec` is not a kick.** It parks AFK people in spectator; press M and you are back on a side.
