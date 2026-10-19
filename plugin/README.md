# GPP — the plugin

CounterStrikeSharp plugin for 10-man nights: whole-server map vote, random captains, captain draft with
everyone held in spectator, then the match itself — ready up, knife for sides, go live, pauses, a demo
and a stats row.

MatchZy used to run everything from `.ready` onwards. It does not any more: see
[../docs/match.md](../docs/match.md) for what replaced it and what was given up with it (round backups —
a match that breaks mid-way cannot be restored).

Built against CounterStrikeSharp API 1.0.376 (net10.0). The engine-free parts — the ready threshold,
pause limits, side swaps, knife scoring, demo naming and the stats SQL — are covered by
[`../Gpp.Tests`](../Gpp.Tests) (`dotnet test`). Everything that needs the engine has been reviewed and
not yet run with ten people on it.

## Commands

| Command | Who | What |
| --- | --- | --- |
| `!pug start` | admin | Map vote, then the draft, then the match |
| `!pug vote` | admin | Map vote only |
| `!pug draft` | admin | Draft only |
| `!pug captains` | admin | Reroll captains at random, before any pick is made |
| `!pug captains <a> <b>` | admin | Pin exactly those two. `!pug captains humans` picks the two people on an otherwise botted server |
| `!captain` / `.captain` | everyone | Put your hand up during the sign-up window `!pug start` opens. Again to take it down |
| `!pug match` | admin | Start the match on the drafted teams (only needed with `auto_start_match` off) |
| `!pug endmatch` / `.endmatch` | admin | End the match that is running and go back to a held warmup |
| `!pug stop` | admin | **Nuke everything.** Ends the match, clears bots, drops every phase, frees spectators and lands in a held warmup. Works from any state |
| `!pug cancel` | admin | Abandon the running vote or draft, and nothing else |
| `!pug status` | admin | What phase it is in, and the score if a match is live |
| `!pug keepmap` | admin | Call off a restart that has just been announced |
| `!pug idle` | admin | Put the idle-server panel up on demand, for working on it |
| `!pug preview` | admin | Play every panel in order, 15s each, without running a pug. `!pug preview stop` ends it |
| `!pug sides <gap> [toe-in] [back]` | admin | Move the two team boards live. `!pug sides auto` hands the derived numbers back |
| `!pug discord 1\|3` | admin | Whether the Discord hold shows the two team boards or just the one panel |
| `!reload` / `!restart` / `!skip` | everyone | The three answers to the idle-server panel. They do nothing when it is not up |
| `!pug restart` | admin | Restart the whole server after a warning and a countdown |
| `!<number>` | everyone | **The main one.** Votes for that map during a vote, makes that pick during a draft. Silent outside both |
| `!vote <number\|map>` | everyone | Vote during a map vote. `!<number>` is shorter |
| `!draft <number\|name>` | captain on the clock | Make a pick by name; no argument reprints the list |
| `!players` | everyone | The numbered list of undrafted players, in chat |
| `.ready` / `.r` / `.gaben` / `!ready` | everyone | Ready up. `min_ready` of them starts the match - and, during round one's Discord hold, says you are in your channel |
| `.unready` / `.ur` / `.notready` | everyone | Take it back |
| `.forceready` / `!pug ...` | admin | Ready everybody and start, whoever is missing |
| `.stay` / `.switch` | the winning side's captain | Keep this side, or take the other one. `side_choice_captain_only` false lets their whole side answer |
| `.pause` | players in the match | Pause from the end of the round. Both sides `.unpause` to resume |
| `.tech` | players in the match | Technical pause — capped per side by `max_tech_pauses` |
| `.unpause` | players in the match | Ask to resume |
| `!link-discord` | everyone | Get a code to link your Steam account to Discord (needs the optional bot) |
| `.kill` / `!kill` | everyone | Kill yourself. Warmup only, and never during a pug phase |
| `.slap <player> [damage]` / `!slap` | admin | Fling a player about. Warmup only |
| `.fent <player> [seconds]` / `!fent` | admin | Spin a player's view for up to 30s. Warmup only |
| `.drug <player> [seconds]` / `!drug` | admin | Warp a player's camera for up to 30s. Warmup only |

A bare `!<number>` is the command players actually use: during a map vote it votes, during a draft it
picks, and outside both it is ignored rather than answered — `!1` in chat mid-match is somebody
talking. **A player's number is theirs for the whole draft**: it is their place in the pool, not their
place in what is left of it, so picking !3 does not renumber everyone below them and the next captain
reading the screen gets the player they were looking at. A player who leaves has their number retired
rather than handed on, someone who joins mid-draft is appended with the next number, and if they come
back they get their old number again. It goes through the plugin's own `say` listener because CounterStrikeSharp turns `!foo` into
`css_foo`, and there is no sane way to register one console command per number. The longer `!vote` and
`!draft` still work and are what you need to pick a player by name.

The match commands answer the dot as well as the bang, and the dot is what everyone types — it is what
MatchZy answered for years. CounterStrikeSharp's chat triggers are `!` and `/` (`configs/core.json`), so
nothing turns `.ready` into `css_ready`: GPP listens to `say` itself rather than adding `.` to
`core.json`, which would need a server restart. The warmup toys below work the same way.

`.fent` teleports the view angles ten times a second — the player's transform yanked around under
them, which is not a camera effect at all but is funny enough to keep.

`.drug` is the colour trip, the same one SourceMod's funcommands had. The screen washes through a new
random colour every 0.4s at alpha 110, and `DesiredFOV` walks a sine between 50 and 130 over about four
seconds so the world breathes behind it.

The colour comes from `CUserMessageFade` (`UM_Fade`, id 106 in CS2's base `usermessages.proto`), sent
with a `RecipientFilter` of one — the engine's screen-fade layer, addressed per player. Not the
flashbang overlay, which whites the screen out and reads as a bug. `duration` and `hold_time` are fixed
point at 512 units to the second; that scale is undocumented and the CS2 ports disagree about it
(512 vs 1024 vs 1000), so recheck it after a CS2 update if the timing drifts. `SoberUp` sends a bare
`PURGE` — without it the last colour hangs on screen after the effect ends and looks stuck.

`CColorCorrection` is a dead end: CS2 ships no lookup texture for it. Inverted controls are client
input and never leave the client.

Both effects are cleared when a vote or draft starts, at the handoff and on every map change. A warped
FOV is invisible to whoever set it and would otherwise follow the player into a live match.

`force` as a second argument (`!pug draft force`) skips the `min_players` check, and ends a match that
is already running before starting.

Admin commands need `@css/config` by default (`admin_flag`), from CounterStrikeSharp's own
`configs/admins.json`.

The pick command is `!draft`, not `!pick`: MatchZy registered `css_pick` for its map veto, and while it
is still installed alongside GPP that name is still taken.

## The night, end to end

    !pug start -> everyone !vote -> map changes -> draft starts ~10s after the map loads
      -> all to spectator -> two random captains knife 1v1 for first pick -> captains !draft
      -> teams set, warmup held -> .ready
      -> knife round for sides -> .stay / .switch
      -> LIVE (demo recording, stats counting, .pause available)
      -> match ends -> scores and players written, demo flushed, map reloads into a held warmup

There is one flow. The old `draft_then_veto` chose MatchZy's map veto instead of the vote, and the veto
went with MatchZy; `!pug draft` still drafts on whatever map the server is already on.

## Config

`addons/counterstrikesharp/configs/plugins/Gpp/Gpp.json`, written on first load.

- `map_pool` — `[{ "name": "de_mirage" }, { "name": "de_season", "workshop_id": "3073892687" }]`.
  `workshop_id` is required for workshop maps: `host_workshop_map` addresses those by id, not by name.
- `players_per_team` (5), `min_players` (10)
- `vote_seconds` (30), `pick_seconds` (30, 0 = no timeout)
- `reconnect_wait_seconds` (90) — after a map change, how long latecomers have. Picking starts as soon
  as `min_players` are back, and anyone who connects inside the window joins the pool instead of being
  locked out; teams do not lock until everyone expected is in or the window closes
- `hud` (true) — draw the overlay at all. With it off, lists go to chat on every pick as before
- `hud_style` (`world`) — `world` draws three `point_worldtext` boards in front of each player (the
  question in the middle, the two teams either side); `center` draws the older `PrintToCenter` panels,
  which is there to rescue a night by flipping a setting rather than rolling the plugin back. See
  [`../docs/overlay.md`](../docs/overlay.md)
- `hud_side_gap` (0 = work it out) — world units either side of the middle board. The plugin derives it
  from how wide the boards are, which is what it should be doing; this is for the night somebody wants
  them further apart without a redeploy
- `pick_order` — `snake` (1,2,2,1,1,2,2,1) or `alternate`
- `force_spectate` (true) — everyone not yet drafted is held in spectator for the whole draft:
  `jointeam` is blocked and a sweep puts anyone sitting in the team-select menu back
- `knife_round` (true) — knife round for sides once everybody has readied. Off goes straight live on the
  drafted sides
- `min_ready` (2) — `.ready` needed to start the match. 0 means every drafted player
- `idle_reload_minutes` (30) — how long the server has to have been empty before the first person to
  connect is offered the fix for it. 0 turns it off. See below
- `restart_warning_seconds` (30) — the countdown on `!pug restart`. The idle panel's `!restart` uses 10,
  since whoever picked it is standing there waiting
- `captain_opt_in_seconds` (30) — the sign-up window `!pug start` opens before the map vote: anyone can
  `!captain`, and the two captains are drawn from whoever did. Fewer than two volunteers and the rest of
  the draw is at random as before. 0 turns it off; `!pug start force` skips it
- `auto_start_match` (true) — start the match as soon as the draft ends. Off waits for `!pug match`
- `match` — the match itself:
  - `warmup_cfg` / `knife_cfg` / `live_cfg` (`gpp/*.cfg`, relative to `csgo/cfg`) — the three phase
    configs, in [`../../examples/cfg/gpp`](../../examples/cfg/gpp). `warmup.cfg` is the one that holds
    the warmup open, which is what keeps an idle server idle; a missing file falls back to a handful of
    built-in cvars and says so in the log
  - `side_choice_captain_only` (true) — only the winning side's captain answers the knife round
  - `ready_after_sides` (false) — a second ready check between the side choice and going live
  - `pauses_enabled` (true), `max_tech_pauses` (0 = unlimited), `tech_pause_seconds` (0 = both sides
    always have to `.unpause`)
  - `demos_enabled` (**false** on this server, see below), `demo_path` (`gpp-demos`, relative to
    `csgo/`), `demo_name_format` (`{TIME}_{MAP}_{TEAM1}_vs_{TEAM2}`). Needs `tv_enable 1`, or `tv_record`
    does nothing and the log says so once
  - `stats_enabled` (true), `stats_database` (`gpp.db`, next to the plugin dll)
  - `matchzy_installed` (true) — while MatchZy is still on the server, keep it asleep and keep its two
    kick cvars off. Set it false once its plugin folder is gone

## Build and install

    dotnet build -c Release
    dotnet test ../Gpp.Tests          # the engine-free rules and the stats SQL

The plugin is **not one file any more**: the stats database pulls in `Microsoft.Data.Sqlite`, so the
whole build output goes to the server —

    addons/counterstrikesharp/plugins/Gpp/
      Gpp.dll  Gpp.deps.json  Microsoft.Data.Sqlite.dll  SQLitePCLRaw.*.dll
      runtimes/linux-x64/native/libe_sqlite3.so

`Gpp.deps.json` is what tells the loader where that native library is; without it stats log one error
and are skipped, and the pug runs on regardless. Copy `Gpp.dll` **last** — writing it is the reload.

The phase cfgs go to `csgo/cfg/gpp/`. `examples/install.sh` does all of this.

Reload by index, never by name: `css_plugins reload 3`. `css_plugins reload Gpp` is refused.

## While MatchZy is still installed

`match.matchzy_installed` is true until its plugin folder is deleted. While it is:

- MatchZy is held in sleep mode (`matchzy_autostart_mode 0`) at load, on every map change and in
  `!pug stop`. Awake it arms a repeating "N players are not ready" chat timer and turns its own
  `.ready` on over the top of GPP's.
- `matchzy_whitelist_enabled_default` and `matchzy_kick_when_no_match_loaded` are asserted false in the
  same places. Both are MatchZy kicking people on connect, and both are cvars that outlive the match
  that set them — which is how a finished pug kicked whoever connected next, hours later.
- `css_ready`, `css_start` and friends are registered by both plugins. CounterStrikeSharp keeps a list
  of handlers per command name and runs all of them, and an asleep MatchZy ignores its own, so this is
  noise rather than a conflict. It goes away with MatchZy.

Nothing in GPP reads MatchZy state any more, and nothing in GPP kicks anyone.

## Round one holds for Discord

`opening_pause_seconds` (30) of held freeze time at the start of round one, with the side that **lost
the knife** told in capitals to get into their voice channel — the winners picked the side they wanted
and stay put. The Discord bot moving people does not replace this: it cannot move somebody who has not
joined voice yet.

It is a ready check, not just a timer. Everyone playing types `.ready` when they are in, the panel
lists who has not, and the hold ends the moment the last one does — a side that sorted itself out in
eight seconds should not stand around for the other twenty-two. The clock is the backstop. `.unpause`
skips the wait outright and is an admin's, or the ready list would mean nothing; `!pug go` does the
same from rcon.

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
the server was empty. It is not checked at all while a match is on, and otherwise every five seconds
rather than every one — the threshold is half an hour, so that is plenty, and it keeps a per-second walk
of the player list off a server that is busy.

## Restarting the server from the plugin

`!pug restart` (and `idle_restart_hours`) warn, count down, and then run `quit` on the console. Bringing
it back is systemd's job, and **that needs `Restart=always` on the unit.** The stock unit here was
`Restart=on-failure`, under which a clean `quit` would leave the server down for good — the only reason
it appeared to work is that **CS2 exits 139 (SIGSEGV) on shutdown**, so systemd saw a failure. Depending
on a crash is not a plan, so this server has a drop-in:

    /etc/systemd/system/cs2.service.d/restart-always.conf
    [Service]
    Restart=always

`cs2.sh` also relaunches the engine itself while it exits 42, which is the engine asking for a restart —
`quit 42` does **not** reach that path (the argument is not an exit code; the process still came out
139). Measured 2026-10-19.

## Demos do not work on this server yet

The plugin does its part: at go-live it waits out the restart, then runs `tv_record` with an absolute
quoted path and `tv_record_immediate 1`, and stops it inside the GOTV flush window at the end. With
`tv_enable 1` and SourceTV reporting `broadcast active`, **the engine wrote no file and printed no
error** — reproduced by hand over rcon with both an absolute and a `csgo/`-relative path, so it is not
the plugin. Demos are off (`match.demos_enabled false`) and so is GOTV, which costs a player slot;
`examples/cfg/gpp/*.cfg` carry the `tv_` lines commented out with the same note. Worth another look by
someone who knows what CS2 wants from `tv_record`. [SOURCE: measured 2026-10-19]

## HUD widths

These are the `hud_style: center` panel's measurements. The world boards are laid out by
[`docs/overlay.md`](../docs/overlay.md) and measured in the same units, but they have no width budget to
fight - a board is as wide as its rule.

The centre panel font is proportional and CS2 ships it as `stratum2.uifont`, an obfuscated container
rather than a readable TTF, so the glyph advances cannot be read off disk. `!pug ruler` measures them
through the renderer instead: it prints twenty copies of one glyph with a forty-pipe run as the scale,
you screenshot it, and `ink = N*advance - sidebearing` gives the advance. Measured 2026-10-17, every
one of them a whole number of pixels:

| glyph | advance | | glyph | advance |
| --- | --- | --- | --- | --- |
| space `\|` `i` `l` | 4 | | `w` `A` | 11 |
| `a` `e` | 8 | | `M` | 12 |
| `o` `n` `0`-`9` | 9 | | `m` `W` | 14 |

Digits are tabular, so every one is 9. The widest glyph is 3.5x the narrowest, which is why counting
characters never worked: a line holds 125 `i` or 35 `m`.

A line too wide for the panel **scrolls** rather than being cut off with an ellipsis: it slides across
at about a glyph per redraw and holds still for three seconds at each end, and every panel scrolls in
step off one shared paint counter. The alert box scrolls by character count, since that larger face has
never been measured.

`hud_line_budget` (default 340) is the usable width of a line in those units, and it is a setting
rather than a constant because **it depends on the viewer's resolution and HUD scale, not on the
font**. A ruler run clipped at ~500 units; a screenshot of the same client at a different resolution
wrapped a line measuring 348, with three separate strings from it coming out at a consistent 1.27x the
ruler scale. Same font, same ratios, different size relative to the box. 340 sits below the widest
line observed to fit. Raise it if the panel looks empty and your players all run one resolution.

## Warmup money

`warmup_money` (default 16000) tops every player's account back up once a second for as long as
warmup is running. `mp_maxmoney` and `mp_startmoney` only set what you are *given*, not what you keep,
so without this the first rifle and a couple of nades empties a wallet mid-warmup. 0 turns it off.

## Discord bot

Optional and off by default. With `discord.base_url` empty the plugin makes no network calls and
`!link-discord` says the server has no bot. See [../docs/discord.md](../docs/discord.md).

## Caveats

- **The last player in the pool is not picked, they are placed.** When one undrafted player is left
  they go to whoever is on the clock, which is also how the odd player in uneven teams is decided.
- **The draft is not enforced once the match is live.** Nothing kicks a latecomer and nothing drags a
  player back to their drafted side after go-live: an open server was the whole reason for dropping
  MatchZy's match config. The ready phase does sweep drafted players back onto their side, so the match
  starts on the right teams.
- **No round backups.** A match that is abandoned, crashed out or ended by accident cannot be restored
  mid-way; it is replayed from the start or not at all.
- A map vote whose winner is the workshop map already running will still reload it: a workshop id can't
  be compared against the running map name.
- `!` is CounterStrikeSharp's default chat trigger; `.` is not one, and the plugin's own `say` listener
  is the only thing that answers it. So the admin commands answer `!` (and `/` silently) unless
  `core.json`'s `PublicChatTrigger` says otherwise, while the match commands and the warmup toys answer
  both.
- **A hot reload leaves the old `say` listener live.** CounterStrikeSharp registers one per plugin
  load, so after two reloads every `.` command answered twice until the server restarted. The listener
  now ignores the same player saying the same thing twice inside 250ms, which makes it independent of
  how CSS cleans up.
- **`.pause` does not pause anything until the round ends** — `mp_pause_match` is a request. The panel
  says `PAUSE AT END OF ROUND` until the engine actually freezes and `Paused.` lands in chat when it
  does; a technical pause is only counted against a team once it has landed, so taking one back before
  the round ends is free.
- **A chat command must not do its work inline.** The engine gives netchan message processing a CPU
  budget and drops the client whose message overran it: `!pug start` spent ~800ms building the vote and
  the admin who typed it was disconnected with `NETWORK_DISCONNECT_OVERFLOW` while the vote came up
  normally for everyone else. Anything that moves players, execs cvars or writes a file goes through
  `AfterTick`.
- Players the pug puts in spectator are tracked and handed the team menu back when the draft ends, the
  pug is cancelled, or the match finishes. Anyone who chose to spectate is left alone.
