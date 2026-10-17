# GPP — the plugin

CounterStrikeSharp plugin for 10-man nights: whole-server map vote, random captains, captain draft
with everyone held in spectator, then hand the drafted rosters to MatchZy.

Built against CounterStrikeSharp API 1.0.376 (net10.0). **Not yet run on a server** — it compiles and
the logic is reviewed, nothing more.

## Commands

| Command | Who | What |
| --- | --- | --- |
| `!pug start` | admin | Runs the configured flow: map vote, or draft if `flow` is `draft_then_veto` |
| `!pug vote` | admin | Map vote only |
| `!pug draft` | admin | Draft only |
| `!pug captains` | admin | Reroll captains, before any pick is made |
| `!pug stop` | admin | **Nuke everything.** Ends the MatchZy match, clears bots, drops every phase, frees spectators and lands in a held warmup. Works from any state |
| `!pug cancel` | admin | Abandon the running vote or draft, and nothing else |
| `!pug clear` | admin | Forget a match we think MatchZy has loaded (ends nothing) |
| `!pug status` | admin | What phase it is in |
| `!pug flow <vote_then_draft\|draft_then_veto>` | admin | Switch flow until the next plugin load |
| `!<number>` | everyone | **The main one.** Votes for that map during a vote, makes that pick during a draft. Silent outside both |
| `!vote <number\|map>` | everyone | Vote during a map vote. `!<number>` is shorter |
| `!draft <number\|name>` | captain on the clock | Make a pick by name; no argument reprints the list |
| `!players` | everyone | The numbered list of undrafted players, in chat |
| `!link-discord` | everyone | Get a code to link your Steam account to Discord (needs the optional bot) |
| `.kill` / `!kill` | everyone | Kill yourself. Warmup only, and never during a pug phase |
| `.slap <player> [damage]` / `!slap` | admin | Fling a player about. Warmup only |
| `.fent <player> [seconds]` / `!fent` | admin | Spin a player's view for up to 30s. Warmup only |
| `.drug <player> [seconds]` / `!drug` | admin | Warp a player's camera for up to 30s. Warmup only |

A bare `!<number>` is the command players actually use: during a map vote it votes, during a draft it
picks, and outside both it is ignored rather than answered — `!1` in chat mid-match is somebody
talking. It goes through the plugin's own `say` listener because CounterStrikeSharp turns `!foo` into
`css_foo`, and there is no sane way to register one console command per number. The longer `!vote` and
`!draft` still work and are what you need to pick a player by name.

The three warmup toys answer the dot as well as the bang. CounterStrikeSharp's chat triggers are `!`
and `/`; MatchZy serves its own `.` commands from a chat hook that only dispatches MatchZy's own
commands, so GPP listens to `say` itself rather than adding `.` to `core.json`, which would need a
server restart.

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

`force` as a second argument (`!pug draft force`) skips the `min_players` check, and ends a loaded
MatchZy match (`get5_endmatch`) before starting.

Admin commands need `@css/config` by default (`admin_flag`), from CounterStrikeSharp's own
`configs/admins.json` — not MatchZy's.

The pick command is `!draft`, not `!pick`, because **MatchZy already registers `css_pick`** for its map
veto.

## Flows

`vote_then_draft` (default):

    !pug start -> everyone !vote -> map changes -> draft starts ~10s after the map loads
      -> all to spectator -> two random captains -> captains !draft
      -> match config written -> matchzy_loadmatch -> .ready

`draft_then_veto`:

    !pug start -> all to spectator -> random captains -> captains !draft
      -> match config written with the whole pool and skip_veto false
      -> MatchZy's own veto: captains .ban / .pick -> .ready

## Config

`addons/counterstrikesharp/configs/plugins/Gpp/Gpp.json`, written on first load.

- `flow` — `vote_then_draft` or `draft_then_veto`
- `map_pool` — `[{ "name": "de_mirage" }, { "name": "de_season", "workshop_id": "3073892687" }]`.
  `workshop_id` is required for workshop maps: both `host_workshop_map` and MatchZy address those by id.
- `players_per_team` (5), `min_players` (10)
- `vote_seconds` (30), `pick_seconds` (30, 0 = no timeout)
- `reconnect_wait_seconds` (90) — after a map change, how long latecomers have. Picking starts as soon
  as `min_players` are back, and anyone who connects inside the window joins the pool instead of being
  locked out; teams do not lock until everyone expected is in or the window closes
- `hud` (true) — each captain gets the numbered pick list on a centre-screen panel, redrawn every
  second, instead of in chat. With it off the list is printed to chat on every pick as before
- `pick_order` — `snake` (1,2,2,1,1,2,2,1) or `alternate`
- `force_spectate` (true) — everyone not yet drafted is held in spectator for the whole draft:
  `jointeam` is blocked and a sweep puts anyone sitting in the team-select menu back
- `knife_round` (true) — written as MatchZy `side_type: always_knife`
- `min_ready` (2) — `.ready` votes needed to start the match, written into the match config as
  `min_players_to_ready`. 0 means every drafted player
- `whitelist` (true) — writes `matchzy_whitelist_enabled_default true` into the config's `cvars`, so
  only drafted players can take a team
- `auto_load_match` (true) — write the config and run `matchzy_loadmatch`
- `match_config_dir` (`MatchZy`) — relative to `csgo/`

## Build and install

    dotnet build -c Release
    # copy to the server:
    #   addons/counterstrikesharp/plugins/Gpp/Gpp.dll

CounterStrikeSharp loads a plugin from a directory matching the DLL name. `css_plugins reload GPP`
picks up changes without restarting the server.

## MatchZy integration

MatchZy exposes nothing to other plugins — no CounterStrikeSharp plugin capability, no shared state. It
does expose console commands, so GPP follows them:

- `matchzy_loadmatch`, `matchzy_loadmatch_url` and the `get5_` aliases mark a match as loaded;
  `get5_endmatch` / `.forceend` / `.rr` mark it gone. `round_announce_match_start` and
  `cs_win_panel_match` correct the flag if the plugin was loaded mid-match.
- A vote or draft refuses to start while a match is loaded, or while the server is past warmup. `force`
  runs `get5_endmatch` first and then starts.
- While a vote or draft is running, MatchZy's `.ready`, `.start`, `.map` and `.rmap` are blocked with a
  message, so nobody readies the server out from under a draft.
- The handoff sets the whitelist through the match config's `cvars`, locking teams to the drafted
  rosters.

These are command hooks, not a contract: a MatchZy release that renames a command, or a config file it
rejects, leaves the flag wrong. `!pug status` prints what GPP believes, `!pug clear` resets it, and
`force` works regardless.

## HUD widths

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

- In `draft_then_veto`, MatchZy chooses its own veto captains — the first player it finds on each team,
  which is not necessarily the drafted captain.
- Set `whitelist` to false and undrafted players can take a team after the handoff; MatchZy only locks
  rosters while its whitelist is on.
- A map vote whose winner is the workshop map already running will still reload it: a workshop id can't
  be compared against the running map name.
- `!` is CounterStrikeSharp's default chat trigger. MatchZy's `.` prefix is its own; these commands
  answer to `!` (and `/` silently) unless `core.json`'s `PublicChatTrigger` says otherwise. The three
  warmup toys are the exception and answer both.
- **A chat command must not do its work inline.** The engine gives netchan message processing a CPU
  budget and drops the client whose message overran it: `!pug start` spent ~800ms building the vote and
  the admin who typed it was disconnected with `NETWORK_DISCONNECT_OVERFLOW` while the vote came up
  normally for everyone else. Anything that moves players, execs cvars or writes a file goes through
  `AfterTick`.
- Players the pug puts in spectator are tracked and handed the team menu back when the draft ends, the
  pug is cancelled, or the match finishes. Anyone who chose to spectate is left alone.
