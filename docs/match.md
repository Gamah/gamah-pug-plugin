# The match, without MatchZy

GPP ran the pug up to the point where MatchZy took over. It now runs the match as well. This is what
that replaced, what it deliberately does not do, and the engine facts the whole thing rests on.

Line references to MatchZy are 0.9.1 (build f2fb11b), read 2026-10-19 — the version that was installed
on the server while this was written.

## Why it had to be all of it

There was no hybrid available. Every per-match thing MatchZy does hangs off `isMatchSetup`, and
`matchzy_loadmatch` is the only thing that sets it — and a loaded match kicks anyone outside the roster
on connect (`EventHandlers.cs` EventPlayerConnectFullHandler), forces roster players back onto their
drafted side (`Utility.cs` UpdatePlayersMap) and parks everyone else in spectator (`Utility.cs`
SwitchPlayerTeam). This server wants the opposite: once a pug is live anyone may walk in and pick a
side. "Keep MatchZy for the stats and the backups" does not exist.

So the handoff had already been reduced to `css_sleep;css_match` with no config at all, which left
MatchZy running the phases and nothing else. This change takes the phases too.

## The phase machine

Three cvars are the whole thing as far as the engine is concerned:

- a **held warmup** — `mp_warmup_start`, `mp_warmuptime 9999`, `mp_warmup_pausetimer 1` — is "nothing is
  happening". It is the state an idle pug server sits in, the one the ready phase is played out in, and
  where the side choice after the knife round happens.
- `mp_warmup_end` plus `mp_restartgame 1` starts a round for real.
- `mp_halftime` and `mp_maxrounds` let the engine run the sides and end the match.

Holding the warmup used to be `cfg/MatchZy/sleep.cfg`'s job (edited on this server, because stock sleep
mode *ends* the warmup and leaves an idle server playing live rounds). It is `cfg/gpp/warmup.cfg` now,
exec'd by the plugin on load, on every map change, at the start of the ready phase, after the knife
round and in `!pug stop`.

| phase | exec'd | what is waiting |
| --- | --- | --- |
| `Ready` | `gpp/warmup.cfg` | `.ready` from `min_ready` players, with the gate below in front of it |
| `Knife` | `gpp/knife.cfg` + `mp_restartgame 1;mp_warmup_end` | one knife round |
| `SideChoice` | `gpp/warmup.cfg` | `.stay` or `.switch` from the side that won |
| `Live` | `gpp/live.cfg` + `mp_warmup_end;mp_restartgame 1` | the match |
| — | map reload (`mp_match_end_restart`) | nothing |

A missing cfg is a warning in the log and a fallback string of cvars, because `exec` reports nothing a
plugin can read and silently carrying on with the last phase's settings is worse.

## What GPP gained

**A ready system of its own.** It used to watch MatchZy's ready commands go past and infer who had
readied, because MatchZy keeps the real list in `playerReadyStatus` and exposes no way to read it. The
list is the real one now, so the panel and the threshold cannot disagree. The gate in front of it is
unchanged and is still the reason it exists: a knife round is decided from who is alive when the round
ends, and a round can end the instant it restarts, so a knife round that starts with somebody in
spectator or dead is decided on the spot with no knives thrown.

**The knife round.** Alive players, then total health between them, then a coin flip — the engine's own
round winner is no use because a knife round that runs out of time has none. The decision is made in a
*pre*-hook on `round_end` and writes the winning side back into the event, so the end-of-round panel
names the side that won the knives. `cs_win_panel_round` would be the obvious place and has stopped
firing in CS2 (`EventHandlers.cs:130`), which is why MatchZy moved this to `round_end` too.

**Pauses.** `.pause`, `.tech`, `.unpause`, to the shape MatchZy and Get5 have: `mp_pause_match` does not
hold anything until the round ends, so the seconds a pause has lasted are only counted during freeze
time — which is also the window where the caller can take an unlanded pause back for free. Both sides
have to `.unpause`, except for a technical pause past `tech_pause_seconds`, which either side can end.
Halftime and the post-game panel refuse both commands: unpausing out of them does not come back.

**Demos.** `tv_record` with an absolute quoted path, armed at go-live and started at the first round
start *after* the restart, because a recording started before `mp_restartgame` goes with it. At the end
of a match GOTV is still sending the last round, so `mp_match_restart_delay` is stretched to cover
`tv_delay + 15` and the recording is stopped inside that window. No `tv_enable` means no demo, and one
line in the log saying so.

**Stats.** A SQLite file of our own (`gpp.db`) with two tables: a match row written when the match goes
live and completed when it ends, and a player row each for kills, deaths, assists, damage, headshots and
MVPs. MatchZy's schema carried forty columns of Get5 bookkeeping off events GPP does not follow; this is
what a pug gets asked about. The schema and its three statements are in `StatsStore.cs`, with no
CounterStrikeSharp in them, and `Gpp.Tests` runs them against a real database.

## What was given up

- **Round backups and `.restore`.** This is the real cost. A match that is abandoned, crashed out or
  ended by accident is replayed from the start or not at all.
- **The map veto**, and with it the `draft_then_veto` flow. The veto was MatchZy's and read its pool
  from a match config. The map vote does the map.
- **Series support.** One map, one match; MatchZy's BO3 bookkeeping is gone with the config that drove it.
- **Coaches, practice mode, `.stop`, backup-file uploads, the HTTP/Get5 event stream.** None of them were
  used here.

## Measured on this server, worth not rediscovering

- CounterStrikeSharp's chat triggers here are `!` and `/` (`configs/core.json`), **not** `.`. So nothing
  turns a chat `.ready` into `css_ready`, and the plugin's own `say` listener is the only thing that
  answers the dot. [SOURCE: CounterStrikeSharp 1.0.376 `src/core/managers/chat_manager.cpp`, read
  2026-10-19]
- A command name may be registered by more than one plugin: CounterStrikeSharp keeps a list of handlers
  per name and runs all of them. GPP registering `css_ready` while MatchZy is still loaded is noise, not
  a conflict. [SOURCE: CounterStrikeSharp 1.0.376 `Core/Commands/CommandManager.cs` RegisterCommand]
- Game phase 4 is halftime and 5 is the post-game panel (`CCSGameRules.GamePhase`).
- Scores live on the `cs_team_manager` entities, per side (`CT` / `TERRORIST`), so reading a *team's*
  score means knowing which side it is on. GPP tracks that in one field, flipped by `.switch` and by
  every half swap; the swap rounds are the half and every overtime half, which is the one piece of
  arithmetic that is easy to get wrong and is under test.
- `mp_weapons_allow_typecount` (engine default 5), not `mp_weapons_allow_zeus`, is the per-round buy cap.
  The plugin follows the warmup edge: `-1` in warmup, `5` once live. No cfg sets it; that line was taken
  out of `warmup.cfg` so the plugin is the only owner.
- MatchZy's `warmup.cfg` opens with `bot_quota 0`, which is why test bots used to vanish seconds after
  being added. GPP's copy does not have it.
