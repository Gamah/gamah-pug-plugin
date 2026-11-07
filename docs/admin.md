# Running a pug

Admin is `@css/config` in `configs/admins.json`, checked against your SteamID. `css_admins_reload`
picks up an edit live.

`!` and `.` both work. Player page: [playing a pug](pug.md).

## The five you need

| | |
| --- | --- |
| `!pug start` | Run the night |
| `!pug status` | Phase, and the score |
| `.forceready` | Start without the last ready |
| `.kick <number> [reason]` | Remove someone for the rest of the pug |
| `!pug stop` | Nuke everything, back to warmup. Safe from any state |

## Starting

| | |
| --- | --- |
| `!pug start` | Vote, captains, map change, draft, match |
| `!pug vote` | Just the vote |
| `!pug draft` | Just the draft, on this map |
| `!pug captains` | Reroll both, before any pick |
| `!pug captains <a> <b>` | Pin two. `humans` picks the two real players on a botted server |
| `!pug match` | Start the match yourself, if `auto_start_match` is off |

`force` as a second word skips the player count and the captain sign up, and ends a running match
first.

Teams come from whoever turned up. Twelve people get 6v6, nobody sits out.

## Running

| | |
| --- | --- |
| `!pug status` | Phase and score |
| `.forceready` | Ready everyone and go |
| `!pug go` | Skip round one's Discord hold |
| `!pug cancel` | Drop the vote or draft, nothing else |
| `!pug endmatch` | End the match, no stats row |
| `!pug stop` | Everything: match, bots, phases, spectators |

## People

`.players` numbers are positional, 1 to n as printed, so they move when someone leaves. Read the list,
then type. Draft pool numbers are separate and do not move.

| | |
| --- | --- |
| `.players` | Who is here, numbered |
| `.kick <player\|number> [reason]` | Kick, works mid pug, holds them out for the rest of it |
| `.unkick [name\|steamid]` | Let one back in. Bare lists who is out |
| `.spec <player\|number> [reason]` | Park an afk player in spectator. They press M and they are back |

`.kick` is remembered for the pug. `.spec` is remembered for nothing.

The block is SteamIDs in memory and only applies while a pug is live. A deploy, map change or restart
loses it. It is not a ban. The server has no whitelist, so anyone can join mid match and pick a side.

## Maps

| | |
| --- | --- |
| `.map` | The list, numbered |
| `.map <number\|name>` | Change to it. Warmup only |
| `.yes` / `.no` | Answer someone else's `.map` |
| `.clear` | Close the list |

Your own `.yes` does not count, so one admin alone needs a second admin, or no admin at all. With
nobody admin on, the whole room votes and silence is a no.

## Server

| | |
| --- | --- |
| `!pug restart` | Restart, after a countdown |
| `!pug keepmap` | Call off a restart |
| `!reload` / `!restart` / `!skip` | Answers to the idle server panel |

## Not for a pug night

Plugin work and overlay tuning, keep them away from a real match. `!pug test` turns bots into players,
which breaks the next real draft.

`!pug test`, `!pug bots`, `!pug preview`, `!pug idle`, `!pug sides`, `!pug ruler`, `!pug discord`,
`.sound`. The [README](https://github.com/Gamah/gamah-pug-plugin/blob/master/plugin/README.md) has
them.

## Who did what

Every command is one line in `addons/counterstrikesharp/logs/log-Gpp<date>.txt`: who, what, and the
phase. Read that, not the journal.
