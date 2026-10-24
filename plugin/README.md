# GPP — the plugin

CounterStrikeSharp plugin for 10-man nights: whole-server map vote, captain sign-up, a captain knife for
first pick, a draft with everyone held in spectator, then the match itself — ready up, knife for sides,
go live, pauses, a demo and a stats row. [`../docs/match.md`](../docs/match.md) is the match engine.

Built against CounterStrikeSharp API 1.0.376 (net10.0). The engine-free parts — the ready threshold,
pause limits, side swaps, knife scoring, demo naming, the board geometry and the stats SQL — are covered
by [`../Gpp.Tests`](../Gpp.Tests) (`dotnet test`).

## Commands

| Command | Who | What |
| --- | --- | --- |
| `!pug start` | admin | Map vote, then the draft, then the match |
| `!pug vote` | admin | Map vote only |
| `!pug draft` | admin | Draft only, on the map the server is already on |
| `!pug captains` | admin | Reroll captains at random, before any pick is made |
| `!pug captains <a> <b>` | admin | Pin exactly those two. `!pug captains humans` picks the two people on an otherwise botted server |
| `!captain` / `.captain` | everyone | Put your hand up while the map vote is up, or during the window `!pug draft` opens. Again to take it down |
| `!pug match` | admin | Start the match on the drafted teams (only needed with `auto_start_match` off) |
| `!pug endmatch` / `.endmatch` | admin | End the match that is running and go back to a held warmup |
| `!pug stop` | admin | **Nuke everything.** Ends the match, clears bots, drops every phase, frees spectators and lands in a held warmup. Works from any state |
| `!pug cancel` | admin | Abandon the running vote or draft, and nothing else |
| `!pug status` | admin | What phase it is in, and the score if a match is live |
| `!pug go` | admin | End round one's Discord hold without waiting for the readies |
| `!pug keepmap` | admin | Call off a restart that has just been announced |
| `!pug restart` | admin | Restart the whole server after a warning and a countdown |
| `!pug test` | admin | Fill the room to ten with bots and walk the whole flow alone. `!pug test sweep` also kills the bots every round, so a match plays out in minutes; `!pug test off` ends it |
| `!pug bots <n>` | admin | Keep `n` bots on the server. Needs `count_bots` |
| `!pug idle` | admin | Put the idle-server panel up on demand, for working on it |
| `!pug preview` | admin | Play every panel in order, 15s each, without running a pug. `!pug preview stop` ends it |
| `!pug sides <gap> [toe-in] [back]` | admin | Move the two team boards live. `!pug sides float <units>` moves a lone board; `auto` in either place hands the derived number back |
| `!pug ruler <glyph> [n]` | admin | Print `n` copies of a glyph for measuring the font. See below |
| `!pug discord 1\|3` | admin | Whether the Discord hold shows the two team boards or just the one panel |
| `!reload` / `!restart` / `!skip` | everyone | The three answers to the idle-server panel. They do nothing when it is not up |
| `!<number>` | everyone | **The main one.** Votes for that map during a vote, makes that pick during a draft. Silent outside both |
| `!vote <number\|map>` | everyone | Vote during a map vote. `!<number>` is shorter |
| `!draft <number\|name>` | captain on the clock | Make a pick by name; no argument reprints the list |
| `!players` | everyone | The numbered list of undrafted players, in chat |
| `.ready` / `.r` / `.gaben` / `!ready` | everyone | Ready up. `min_ready` of them starts the match — and, during round one's Discord hold, says you are in your channel |
| `.unready` / `.ur` / `.notready` | everyone | Take it back |
| `.forceready` | admin | Ready everybody and start, whoever is missing |
| `.stay` / `.switch` | the winning side's captain | Keep this side, or take the other one. `side_choice_captain_only` false lets their whole side answer |
| `.pause` | players in the match | Pause from the end of the round. Both sides `.unpause` to resume |
| `.tech` | players in the match | Technical pause — capped per side by `max_tech_pauses` |
| `.unpause` | players in the match | Ask to resume |
| `!link-discord` | everyone | Get a code to link your Steam account to Discord (needs the optional bot) |
| `.kill` / `!kill` | everyone | Kill yourself. Warmup only, and never during a pug phase |
| `.slap <player> [damage]` / `!slap` | admin | Fling a player about. Warmup only |
| `.fent <player> [seconds]` / `!fent` | admin | Spin a player's view for up to 30s. Warmup only |
| `.drug <player> [seconds]` / `!drug` | admin | Warp a player's camera for up to 30s. Warmup only |
| `.smokes [on\|off]` / `!smokes` | everyone | Smoke practice for yourself: trails, smokes only, invulnerable, one popped smoke, and your throws come back. Warmup only |

`force` as a second argument (`!pug draft force`) skips the `min_players` check, and ends a match that
is already running before starting.

Admin commands need `@css/config` by default (`admin_flag`), from CounterStrikeSharp's own
`configs/admins.json`, checked against the controller's own SteamID.

### Numbers, and the dot

A bare `!<number>` is the command players actually use: during a map vote it votes, during a draft it
picks, and outside both it is ignored rather than answered — `!1` in chat mid-match is somebody talking.
**A player's number is theirs for the whole draft**: it is their place in the pool, not their place in
what is left of it, so picking `!3` does not renumber everyone below them and the next captain reading
the screen gets the player they were looking at. A player who leaves has their number retired rather
than handed on, someone who joins mid-draft is appended with the next number, and if they come back
they get their old number again. The longer `!vote` and `!draft` still work, and `!draft` is what picks
a player by name.

The match commands and the warmup toys answer the dot as well as the bang, and the dot is what everyone
types. CounterStrikeSharp's chat triggers are `!` and `/` (`configs/core.json`), so nothing turns
`.ready` into `css_ready`: the plugin listens to `say` itself rather than adding `.` to `core.json`,
which would need a server restart. Numbers go through the same listener, because `!1` would otherwise
need one registered console command per number.

The pick command is `!draft`, not `!pick`.

## The night, end to end

    !pug start -> everyone !vote, anyone !captain -> map changes -> draft starts ~10s after the map loads
      -> all to spectator -> two captains knife 1v1 for first pick -> captains !draft
      -> teams set, warmup held -> .ready
      -> knife round for sides -> .stay / .switch
      -> LIVE (demo recording, stats counting, .pause available)
      -> match ends -> scores and players written, the engine's end screen, back to a held warmup

## Config

`addons/counterstrikesharp/configs/plugins/Gpp/Gpp.json`, written on first load and **read only at
load**. [`../examples/Gpp.json`](../examples/Gpp.json) is a filled-in one.

- `map_pool` — `[{ "name": "de_mirage" }, { "name": "de_season", "workshop_id": "3073892687" }]`.
  `workshop_id` is required for workshop maps: `host_workshop_map` addresses those by id, not by name.
  `display` renames one on the ballot
- `players_per_team` (0 = size the teams from whoever is there, and leave nobody out)
- `min_players` (2) — needed before `!pug start` runs. Two captains and nothing to draft is still a pug
- `vote_seconds` (45) — the map vote, which is also the captain sign-up. Closes early the moment
  everybody has voted, captains volunteered or not. `ballot_size` (0 = the whole pool on the ballot), `pick_seconds` (30, 0 = no
  timeout)
- `reconnect_wait_seconds` (90) — after a map change, how long the room waits for the two captains to
  load before handing an armband to somebody who is actually there. Nothing waits on anybody else: a map
  change disconnects nobody, so the players who are missing are usually players who voted for the map and
  then sat this one out. Anyone who connects while picks remain joins the pool. The armband goes to a
  leftover `!captain` volunteer if one is still here, then to somebody who voted the map in, then to
  anybody present - a bot captain is replaced at once rather than waited out, since a map change kicks
  the lot
- `pick_order` — `snake` (1,2,2,1,1,2,2,1) or `alternate`
- `hud` (true) — draw the overlay at all. With it off, lists go to chat on every pick
- `hud_side_gap` (0 = derive it) — world units either side of the middle board
- `hud_float_left` (true) — move a lone board out of the middle of the screen, where the player is
  aiming. `hud_float_gap` (0 = same as `hud_side_gap`) is how far
- `force_spectate` (true) — everyone not yet drafted is held in spectator for the whole draft:
  `jointeam` is blocked and a sweep puts anyone sitting in the team-select menu back
- `captain_opt_in_seconds` (30) — whether `!captain` is asked for, and the window `!pug draft` opens for
  it. `!pug start` carries the sign-up on the map vote instead, for `vote_seconds`. Either way the two
  captains are drawn from whoever put their hand up; fewer than two volunteers and the rest of the draw
  is at random. 0 turns it off; `!pug start force` skips it
- `captain_knife` (true), `captain_knife_seconds` (60) — the captains' 1v1 for first pick. Off is a coin
  flip
- `auto_start_match` (true) — start the match as soon as the draft ends. Off waits for `!pug match`
- `knife_round` (true) — knife round for sides once everybody has readied. Off goes straight live on the
  drafted sides
- `min_ready` (2) — `.ready` needed to start the match. 0 means every drafted player
- `idle_reload_minutes` (30) — how long the server has to have been empty before the first person to
  connect is offered the fix for it. 0 turns it off. See below
- `restart_warning_seconds` (30) — the countdown on `!pug restart`. The idle panel's `!restart` uses 10,
  since whoever picked it is standing there waiting
- `warmup_money` (16000) — see below
- `smoke_trail_seconds` (8) — how long `.smokes` draws a practising player's grenade trail for. 0 turns
  the feature off, command and all. See below
- `count_bots` (false) — bots count as players, so one person can walk the flow. `!pug test` turns it on
  for one session without touching the config; leave the key false for real pugs or a stray bot joins
  the draft
- `admin_flag` (`@css/config`), `chat_prefix`
- `match` — the match itself:
  - `warmup_cfg` / `knife_cfg` / `live_cfg` (`gpp/*.cfg`, relative to `csgo/cfg`) — the three phase
    configs, in [`../examples/cfg/gpp`](../examples/cfg/gpp). `warmup.cfg` is the one that holds the
    warmup open, which is what keeps an idle server idle; a missing file falls back to a handful of
    built-in cvars and says so in the log
  - `side_choice_captain_only` (true) — only the winning side's captain answers the knife round
  - `ready_after_sides` (false) — a second ready check between the side choice and going live
  - `opening_pause_seconds` (30) — round one's Discord hold. See below
  - `post_match_seconds` (15) — `mp_match_restart_delay`, the engine's own end-of-match screen
  - `pauses_enabled` (true), `max_tech_pauses` (0 = unlimited), `tech_pause_seconds` (0 = both sides
    always have to `.unpause`)
  - `draw_vote` (true), `draw_vote_seconds` (30) — pause and ask the room whether a level match goes to
    overtime. See below
  - `demos_enabled`, `demo_path` (`gpp-demos`, relative to `csgo/`), `demo_name_format`
    (`{TIME}_{MAP}_{TEAM1}_vs_{TEAM2}`). Needs `tv_enable 1`, or `tv_record` does nothing and the log
    says so once
  - `stats_enabled` (true), `stats_database` (`gpp.db`, next to the plugin dll)
- `discord` — optional, off while `base_url` is empty. See [../docs/discord.md](../docs/discord.md)

## Build and install

    dotnet build -c Release
    dotnet test ../Gpp.Tests          # the engine-free rules and the stats SQL

The plugin is **not one file**: the stats database pulls in `Microsoft.Data.Sqlite`, so the whole build
output goes to the server, with `Gpp.dll` **last** — writing it is the reload.
[`../docs/deploy.md`](../docs/deploy.md) has the rest, including the refusal to install mid-match.

## Round one holds for Discord

`opening_pause_seconds` (30) of held freeze time at the start of round one, with the side that **lost
the knife** told in capitals to get into their voice channel — the winners picked the side they wanted
and stay put. The Discord bot moving people does not replace this: it cannot move somebody who has not
joined voice yet.

It is a ready check, not just a timer. Everyone playing types `.ready` when they are in, the panel
lists who has not, and the hold ends the moment the last one does. The clock is the backstop.
`.unpause` skips the wait outright and is an admin's, or the ready list would mean nothing; `!pug go`
does the same from rcon.

## The idle-server panel

A server left alone for hours plays a strange first round — the warmup has been held open the whole time
and every phase that came and went restarted the round. The fix is a map load or a restart, and the first
person to connect to a server that has been empty longer than `idle_reload_minutes` gets asked which:

    IDLE SERVER FIX                 <- the alert box explains
    server has been idle for
    10 hours

    !reload - load de_mirage again  !restart - restart the server  !skip - leave it

**It asks rather than acts.** Reloading the map under whoever just walked in, or restarting the server on
them, is the same complaint from the other side. The panel stays up until one of the three is picked —
there is no timer on it — and **anyone** can pick, admin or not. The three commands do nothing at all
while the panel is not up, so `!restart` is not a command players have; it is one of three answers to a
question the server asked. `!skip` leaves it until the next time the server has sat empty.

The panel also comes down by itself if a pug starts (that answers the question another way) or if
everybody leaves again, in which case the server is idling afresh and will ask the next person.

Occupancy is followed on the empty→occupied transition rather than from the connect hook, because those
two race: by the time a connect is handled, the tick has already seen the player and forgotten how long
the server was empty. It is not checked while a match is on, and otherwise every five seconds.

## Restarting the server from the plugin

`!pug restart` warns, counts down, and then runs `quit` on the console. Bringing the server back is
systemd's job, and **that needs `Restart=always` on the unit**: under `Restart=on-failure` a clean
`quit` leaves the server down for good. `cs2.sh` relaunches the engine itself while it exits 42, which
is the engine asking for a restart, but `quit 42` does not reach that path — the argument is not an exit
code. [SOURCE: measured on this server, 2026-10-19]

## Warmup money

`warmup_money` (16000) tops every player's account back up once a second for as long as warmup is
running. `mp_maxmoney` and `mp_startmoney` only set what you are *given*, not what you keep, so without
this the first rifle and a couple of nades empties a wallet mid-warmup. 0 turns it off.

## Smoke practice

`.smokes` is per-player and warmup-only. It replicates two cvars to that one client —
`sv_grenade_trajectory_prac_trailtime` (`smoke_trail_seconds`) and
`sv_grenade_trajectory_prac_pipreview` — so the trail and the landing inset are drawn on their screen
and nobody else's. Both are `FCVAR_REPLICATED` and neither is a cheat, which is the whole reason it
works this way: `sv_rethrow_last_grenade` and `sv_kill_smokegrenade` would have needed `sv_cheats 1`,
and that hands every connected client the client-side cheats as well.
[SOURCE: `find grenade_traj`, CS2 1.41.9.2, read 2026-10-24. The flags are documented; that a
per-client replication is enough to draw one player a trail is read off the behaviour.]

Each throw re-arms them with a smoke and prints, to them alone, where they threw from and the view
angles they threw at — the half of a lineup that pixels cannot give you — and a second line with where
it popped. One popped smoke each: when their next one lands, the previous cloud is removed. The cull
lands at detonation rather than at the throw, so a second grenade may fly while they are still looking
at the first cloud. The cloud *is* the `smokegrenade_projectile` entity (the voxel field the client
renders is networked from it), so `Remove()` on it takes the cloud with it; entity indices are reused,
so the tracked one is checked for a live `smokegrenade_projectile` before anything is removed.

While it is on the toggle owns the player: their loadout is a knife and smokes, and anything else they
buy or pick up is taken back on the next frame (`item_purchase` and `item_pickup`, after the fact - the
weapon is already theirs by the time either fires). Warmup money is unlimited, so without the lock the
first thing anyone does is buy an AWP and practice becomes a deathmatch with trails on. They are also
invulnerable (`m_bTakesDamage` cleared on the pawn; damage is worked out server-side, so nothing has to
reach the client) - a lineup takes a few goes and somebody else warming up should not be able to end
one.

The re-arm only fires when they are holding no smoke, and a give fires `item_pickup` - which is one of
the two hooks holding the lock. Without that check the hook hands them another smoke, which fires
another pickup, for as long as the server is up.

Nothing survives into a pug: a vote, a draft, the handoff, a map change, warmup ending and a plugin
unload all write the two cvars back to 0 and make everyone mortal again. An invulnerable player left in
a live match is the one failure here nobody would notice until it decided a round.

## The warmup toys

`.fent` teleports the view angles ten times a second — the player's transform yanked around under them,
which is not a camera effect at all but is funny enough to keep.

`.drug` is the colour trip. The screen washes through a new random colour every 0.4s at alpha 110, and
`DesiredFOV` walks a sine between 50 and 130 over about four seconds so the world breathes behind it.
The colour comes from `CUserMessageFade` (`UM_Fade`, id 106 in CS2's base `usermessages.proto`), sent
with a `RecipientFilter` of one — the engine's screen-fade layer, addressed per player, not the
flashbang overlay. `duration` and `hold_time` are fixed point at 512 units to the second; that scale is
undocumented and the CS2 ports disagree about it (512 vs 1024 vs 1000), so recheck it after a CS2 update
if the timing drifts. `SoberUp` sends a bare `PURGE`, or the last colour hangs on screen.
`CColorCorrection` is no use: CS2 ships no lookup texture for it.

Both effects are cleared when a vote or draft starts, at the handoff and on every map change. A warped
FOV is invisible to whoever set it and would otherwise follow the player into a live match.

## HUD widths

The font is proportional and CS2 ships it as `stratum2.uifont`, an obfuscated container rather than a
readable TTF, so the glyph advances cannot be read off disk. `!pug ruler` measures them through the
renderer instead: it prints twenty copies of one glyph with a forty-pipe run as the scale, you
screenshot it, and `ink = N*advance - sidebearing` gives the advance. Measured 2026-10-17, every one of
them a whole number of pixels:

| glyph | advance | | glyph | advance |
| --- | --- | --- | --- | --- |
| space `\|` `i` `l` | 4 | | `w` `A` | 11 |
| `a` `e` | 8 | | `M` | 12 |
| `o` `n` `0`-`9` | 9 | | `m` `W` | 14 |

Digits are tabular, so every one is 9. The widest glyph is 3.5x the narrowest, which is why nothing may
lay out by character count: a line holds 125 `i` or 35 `m`. `HudMetrics` holds the table and the boards
pad themselves in these units; re-run the ruler after a CS2 update.

The alert line is the exception — that larger face has never been measured, so a line too long to fit
scrolls by character count, one character every three paints.

## Caveats

- **The last player in the pool is not picked, they are placed.** When one undrafted player is left
  they go to whoever is on the clock, which is also how the odd player in uneven teams is decided.
- **The draft is not enforced once the match is live.** Nothing kicks a latecomer and nothing drags a
  player back to their drafted side after go-live: the server is open on purpose. The ready phase does
  sweep drafted players back onto their side, so the match starts on the right teams.
- **No round backups.** A match that is abandoned, crashed out or ended by accident cannot be restored
  mid-way; it is replayed from the start or not at all.
- A map vote whose winner is the workshop map already running will still reload it: a workshop id cannot
  be compared against the running map name.
- **A hot reload leaves the old `say` listener live.** CounterStrikeSharp registers one per plugin load,
  so after two reloads every `.` command answered twice until the server restarted. The listener ignores
  the same player saying the same thing twice inside 250ms, which makes it independent of how
  CounterStrikeSharp cleans up.
- **`.pause` does not pause anything until the round ends** — `mp_pause_match` is a request. The panel
  says `PAUSE AT END OF ROUND` until the engine actually freezes, and a technical pause is only counted
  against a team once it has landed, so taking one back before the round ends is free.
- **A chat command must not do its work inline.** The engine gives netchan message processing a CPU
  budget and drops the client whose message overran it: building a vote inline disconnected the admin
  who typed `!pug start` with `NETWORK_DISCONNECT_OVERFLOW` while the vote came up normally for
  everyone else. Anything that moves players, execs cvars or writes a file goes through `AfterTick`.
- **A five-second halftime left the MVP anthem playing into the second half.** The anthem is started by
  `round_mvp` and stopped by the client, and with `mp_halftime_duration 5` the stop never arrived. Read
  off the logs rather than guessed: `gpp.db`'s MVP counts equal the rounds played exactly, so nothing
  fires twice, and every complaint in the chat log is in the second half within minutes of the half swap,
  with nothing through first halves of 21 and 10 minutes. `live.cfg` now asks for 15, which is the
  engine's own default and what a real match runs - nothing in Valve's gamemode configs touches it, and
  MatchZy says 20. The five seconds were ours alone.
  [SOURCE: `help mp_halftime_duration` and the shipped gamemode cfgs, CS2 1.41.9.2, read 2026-10-24]
- **Overtime is the room's call, and the match pauses to ask.** The engine reads `mp_overtime_enable`
  the moment a round makes the score level, so the question goes up at the *start* of the round that can
  draw - 12-11, and 15-14 and 18-17 for the overtimes after it, every score a single round can draw from,
  which `MatchRules.DrawNextRound` knows and is tested on. `mp_pause_match` holds that round's freeze
  time, the board asks `!1` to play it out or `!2` to call it a draw, and the pause lifts the moment
  everyone connected has answered - the 30s is the backstop, not the gate. It is the plugin's pause, the
  same shape as the Discord hold: it ignores `pauses_enabled` and counts against nobody's technical
  pauses. Anyone in the server votes, because a pug has no roster the server itself believes in. The cvar
  is written on every vote rather than at the close, because the deciding round can end first. Nobody
  voting, or an even split, is a draw: that is the answer that stops. Going live resets it, so a match
  always starts with the config's own overtime.
- Players the pug puts in spectator are tracked and handed the team menu back when the draft ends, the
  pug is cancelled, or the match finishes. Anyone who chose to spectate is left alone.
