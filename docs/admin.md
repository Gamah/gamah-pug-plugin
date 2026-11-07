# Running a pug — the admin page

Everything an admin needs to run a night, and nothing else. The full list, including the commands that
only exist for working on the plugin, is in [`../plugin/README.md`](../plugin/README.md).

Admin means `@css/config` in CounterStrikeSharp's own `configs/admins.json`, checked against your
SteamID. `css_admins_reload` applies an edit without a restart.

Commands take `!` or `.` — `!pug start` and `.pug start` are the same thing. `.` is what people type.

## The short version

| | |
| --- | --- |
| `!pug start` | Run the night: map vote, captains, draft, match |
| `!pug status` | What phase it is in, and the score |
| `.forceready` | Start the match without waiting for the last ready |
| `.kick <number> [reason]` | Remove somebody and hold them out for the rest of the pug |
| `!pug stop` | Nuke everything and land back in a held warmup |

If you only ever learn those five you can run a night.

## Starting one

`!pug start` is the whole flow and the one to use. The rest are for a night that has gone off-script:

| Command | What it does |
| --- | --- |
| `!pug start` | Map vote (which is also the captain sign-up), map change, draft, match |
| `!pug vote` | The map vote on its own. Stops once the map has changed |
| `!pug draft` | The draft on its own, on the map the server is already on |
| `!pug captains` | Reroll both captains at random. Only before a pick has been made |
| `!pug captains <a> <b>` | Pin exactly those two. `!pug captains humans` picks the two people on a botted server |
| `!pug match` | Start the match on the drafted teams — only needed with `auto_start_match` off |

Add `force` as a second word (`!pug start force`) to skip the `min_players` check, skip the captain
sign-up, and end a match that is already running first.

Teams are sized from whoever turned up. Five a side is the usual night; `players_per_team` 0 (the
default) means twelve people get 6v6 and nobody sits out.

## While it is running

| Command | What it does |
| --- | --- |
| `!pug status` | Phase, and the score if a match is live |
| `.forceready` | Ready everybody and start, whoever is still missing |
| `!pug go` | End round one's Discord hold without waiting for the readies |
| `!pug cancel` | Abandon the running vote or draft, and nothing else |
| `!pug endmatch` | End the live match and go back to a held warmup. The stats row is not written |
| `!pug stop` | Everything: ends the match, clears bots, drops every phase, frees spectators. Works from any state |

`!pug stop` is the one to reach for when you cannot tell what state the server is in. It is safe from
anywhere.

## People

Numbers come from `.players`, and they are positional — 1..n down the list as printed, so they
renumber when somebody leaves. Read the list, then act, rather than reusing a number from five minutes
ago. (The draft pool is numbered separately and does not renumber; see the README.)

| Command | What it does |
| --- | --- |
| `.players` | Who is here, numbered, privately to you. Works in every phase |
| `.kick <player\|number> [reason]` | Kick them, **during a pug too**, and hold the door shut for the rest of it |
| `.unkick [name\|steamid]` | Let one back in. With no argument, lists who is being held out |
| `.spec <player\|number> [reason]` | Move somebody to spectator. For AFK people — they press M and they are back |

The difference between the two: `.kick` is remembered for the pug, `.spec` is remembered for nothing.

The block is a set of SteamIDs in memory and only bites while a pug is live. It is **not a ban**: a
deploy, a map change or a server restart loses it, and once the pug is over they can walk back in. A
real ban is not this plugin's job.

The server is otherwise open — no whitelist, and anyone may join mid-match and pick a side.

## The map

| Command | What it does |
| --- | --- |
| `.map` | The map list on a board beside you, numbered |
| `.map <number\|name>` | Change to it. Warmup only |
| `.yes` / `.no` | Answer somebody else's `.map` |
| `.clear` | Close the list (or anything else the plugin has put on your screen) |

An admin's `.map` waits for an admin `.yes` — your own does not count, so one admin alone needs a
second one, or nobody. With no admin connected at all the whole room votes and silence is a no.

## The server itself

| Command | What it does |
| --- | --- |
| `!pug restart` | Restart the server, after a warning and a countdown |
| `!pug keepmap` | Call off a restart that has just been announced |
| `!reload` / `!restart` / `!skip` | The three answers to the idle-server panel. Silent when it is not up |

A restart is the fix for a server that has sat empty for hours and feels wrong. The idle panel offers
it to the first person who connects.

## Not for a pug night

These exist for working on the plugin and tuning the overlay, and will get in the way of a real match.
`!pug test` in particular turns on bots counting as players, which is exactly what you do not want when
people are arriving.

`!pug test [sweep|off]`, `!pug bots <n>`, `!pug preview`, `!pug idle`, `!pug sides`, `!pug ruler`,
`!pug discord`, `.sound`. The README documents them.

## Who did what

Every command typed at GPP is one line in `addons/counterstrikesharp/logs/log-Gpp<date>.txt`: who
typed it, what they typed, and the phase it was in. That is the file to read when somebody asks who
kicked whom. Read that log, not the journal — journald splices the game's output mid-word.
