# Playing a pug

Everything is typed in chat. `.ready` and `!ready` are the same. A bare number answers whatever is on
your screen.

Admin page: [running a pug](admin.md).

## What happens

1. **Map vote.** 45 seconds. Type `!1`, `!2`, `!3` for the map you want. Closes early once everyone
   has voted.
2. **Captains.** `.captain` during the vote puts your hand up. Two get drawn from whoever did. Type it
   again to take it back.
3. **Map changes.** Reconnect. The draft waits 90 seconds for the captains. Anyone who turns up while
   picks remain joins the pool.
4. **Captains knife.** 1v1 for first pick, 60 seconds.
5. **Draft.** You are held in spectator until you are picked. Captains type `!4` to take player 4.
   `.players` reprints the pool. `!draft <name>` picks by name.
6. **Ready.** Type `.ready`. Everyone has to. `.unready` takes it back.
7. **Knife round.** Winning captain types `.stay` or `.switch`.
8. **Live.** 24 rounds, overtime if it is level. Round one holds for a bit so people can get into
   Discord, so `.ready` again once you are in.
9. **Done.** Scores written, end screen, back to warmup.

Your draft number stays yours. Picks do not renumber anyone.

## Commands

| | |
| --- | --- |
| `!<number>` | Votes, or picks. The only one you need |
| `.captain` | Volunteer to captain |
| `.players` | Who is here, numbered, just to you |
| `.ready` / `.r` | Ready up. `.gaben` works too |
| `.unready` | Take it back |
| `.stay` / `.switch` | Sides, after the knife round |
| `.pause` | Pause at the end of the round |
| `.tech` | Pause because someone crashed |
| `.unpause` | Both sides have to ask |
| `.clear` | Close anything on your screen |
| `!link-discord` | Code to link Steam to Discord |

## Warmup only

| | |
| --- | --- |
| `.smokes` | Trails, smokes only, invulnerable, nades come back. `.smokes off` to stop |
| `.map` | Map list. `.map 4` asks for map 4 |
| `.kill` | Yourself |
| `.slap` | Fling yourself. No damage |
| `.fent` / `.drug` | Spin or warp your view, 30 seconds max |
| `.narcan` | Undo those |

`.map` needs an admin to `.yes` it. With no admin on, everyone has to agree.

## Gotchas

* You cannot join a team during the draft. Spectator is how it knows you are undrafted.
* Nothing times out at ready. The countdown is just the overlay. `.ready` still starts it.
* `!1` outside a vote or draft does nothing.
* Level at the end asks the room: `!1` overtime, `!2` draw.
* Join mid match and you land in spectator. Press M.
* `.kick` keeps you out for that pug only, not forever.
* `.spec` is not a kick. Press M and you are back.
