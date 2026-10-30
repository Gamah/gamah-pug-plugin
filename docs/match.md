# The match

The plugin runs the whole pug: map vote, captains, draft, and then the match itself - ready up, knife for
sides, go live, pauses, a demo and a stats row. This is the phase machine, what each cfg owns, and the
engine facts the whole thing rests on.

## The phase machine

Three cvars are the whole thing as far as the engine is concerned:

- a **held warmup** - `mp_warmup_start`, `mp_warmuptime 9999`, `mp_warmup_pausetimer 1` - is "nothing is
  happening". It is the state an idle pug server sits in, the one the ready phase is played out in, and
  where the side choice after the knife round happens.
- `mp_warmup_end` plus `mp_restartgame 1` starts a round for real.
- `mp_halftime` and `mp_maxrounds` let the engine run the sides and end the match.

| phase | exec'd | what is waiting |
| --- | --- | --- |
| `Ready` | `gpp/warmup.cfg` | `.ready` from `min_ready` players, with the gate below in front of it |
| `Knife` | `gpp/knife.cfg` + `mp_restartgame 1;mp_warmup_end` | one knife round |
| `SideChoice` | `gpp/warmup.cfg` | `.stay` or `.switch` from the side that won |
| `Live` | `gpp/live.cfg` + `mp_warmup_end;mp_restartgame 1` | the match |
| `Over` | nothing - see below | the engine's own end-of-match screen |

`cfg/gpp/warmup.cfg` is the file that holds an idle server idle, and the plugin execs it on load, on
every map change, at the start of the ready phase, after the knife round and in `!pug stop`. A missing
cfg is a warning in the log and a fallback string of cvars, because `exec` reports nothing a plugin can
read and silently carrying on with the last phase's settings is worse.

`exec` queues a cfg's contents *behind* whatever is sent after it, so a cvar the plugin owns cannot be
sent at the same moment as the cfg that would overwrite it. `mp_match_can_clinch` and
`mp_match_restart_delay` are therefore asserted on the first round of the live phase, not at go-live.

## The end-of-match rule

When the engine ends a match itself, its end-of-match screen goes up and `CCSGameRules.GamePhase` is 5
for `mp_match_restart_delay` seconds (`post_match_seconds`, 15 here).

**Nothing may be sent while that screen is up.** A warmup exec'd one tick into it aborts the
end-of-match state and leaves the room on a "match cancelled" 0-0 panel. `mp_restartgame` does not clear
that, and writing `m_gamePhase` back does not either - it only makes the plugin's own check lie.

Left alone the engine clears it by itself: the intermission times out, the engine restarts, and everyone
is in the warmup that was already set up. So the hand-back waits for the phase to leave 5 and only then
puts the server back, with a map reload as a last resort if the phase never leaves.

**`mp_restartgame` resurrects nobody.** It tops the living back up to full health and leaves the dead
where they are. So a warmup with `mp_respawn_on_death_ct` / `_t` at 0 - which is right for a match and
wrong for a warmup - splits the room into the people who survived the last round, walking about, and the
people who did not, locked by `mp_forcecamera` to a team-mate's camera until the next map. That reads
exactly like being stuck in spectator, and a reconnect clears it, because it is not spectator at all: it
is a corpse. `warmup.cfg` sets both to 1, which is the only thing in the server that will respawn them.

An admin ending a match that is still live (`!pug stop`) is the other case, and there the order matters:
`mp_restartgame` first, the warmup cfg after. The other way round opens the warmup inside the round-end
freeze, and a warmup whose clock is paused never counts that freeze down - the room can take a step and
is then snapped back to where it stood.

## What the match engine does

**Ready.** The plugin's own list of who has readied is the only record of it, so the panel and the
threshold cannot disagree. The gate in front of it: a knife round is decided from who is alive when the
round ends, and a round can end the instant it restarts, so a knife round that starts with somebody in
spectator or dead is decided on the spot with no knives thrown. Until every drafted player is connected,
on their drafted side and spawned, a ready does not count.

**The knife round.** Alive players, then total health between them, then a coin flip - the engine's own
round winner is no use because a knife round that runs out of time has none. The decision is made in a
*pre*-hook on `round_end` and writes the winning side back into the event, so the end-of-round panel
names the side that won the knives. `cs_win_panel_round` would be the obvious place and does not fire in
CS2.

**Pauses.** `.pause`, `.tech`, `.unpause`. `mp_pause_match` does not hold anything until the round ends,
so the seconds a pause has lasted are only counted during freeze time - which is also the window where
the caller can take an unlanded pause back for free. Both sides have to `.unpause`, except for a
technical pause past `tech_pause_seconds`, which either side can end. Halftime (`GamePhase` 4) and the
post-game panel (5) refuse both commands: unpausing out of them does not come back.

**Demos.** `tv_record` with an absolute quoted path, armed at go-live and started at the first round
start *after* the restart, because a recording started before `mp_restartgame` goes with it. At the end
of a match GOTV is still sending the last round, so when `tv_enable` is on `mp_match_restart_delay` is
stretched to cover `tv_delay + 15` and the recording is stopped inside that window. No `tv_enable` means
no demo, one line in the log, and no stretched intermission.

**Stats.** A SQLite file (`gpp.db`) with two tables: a match row written when the match goes live and
completed when it ends, and a player row each for kills, deaths, assists, damage, headshots and MVPs.
The schema and its three statements are in `StatsStore.cs`, with no CounterStrikeSharp in them, and
`Gpp.Tests` runs them against a real database.

## What it does not do

- **Round backups.** A match that is abandoned, crashed out or ended by accident is replayed from the
  start or not at all.
- **Map veto.** The map vote does the map.
- **Series.** One map, one match.
- **Coaches, practice mode, backup uploads, an event stream.**

## Engine facts worth not rediscovering

- CounterStrikeSharp's chat triggers are `!` and `/` (`configs/core.json`), **not** `.`. So nothing turns
  a chat `.ready` into `css_ready`, and the plugin's own `say` listener is the only thing that answers
  the dot. [SOURCE: CounterStrikeSharp 1.0.376 `src/core/managers/chat_manager.cpp`, read 2026-10-19]
- A command name may be registered by more than one plugin: CounterStrikeSharp keeps a list of handlers
  per name and runs all of them. [SOURCE: CounterStrikeSharp 1.0.376 `Core/Commands/CommandManager.cs`
  RegisterCommand, read 2026-10-19]
- `CCSGameRules.GamePhase`: 4 is halftime, 5 is the post-game panel.
- **`mp_halftime_duration` is a target, not a duration.** Its own help text says "shortened if team
  intros are active", and the floor it is shortened to is `mp_min_halftime_duration` (8.5). Team intros
  count as active whenever `mp_team_intro_type` is `auto` - the default - and `mp_halftime` is set, so a
  configured halftime is itself what switches them on. `mp_team_intro_time 0` sets their length and does
  not stop them counting. An MVP anthem is longer than nine seconds, which is the whole of why one plays
  on into the second half: nothing stops it, the half is simply shorter than the song. `live.cfg` sets
  `mp_team_intro_type none`, and the plugin measures and logs how long the half actually lasted so the
  figure is a number rather than a theory. [SOURCE: cvar help text read off the engine, CS2 1.41.9.2,
  2026-10-30]
- **The pug's phases do not cover a live match.** `_phase` is `Idle` from the handoff onwards, so once a
  match is live nothing in the plugin is placing people - and the engine assigns a team to anyone who
  sits in the team-select menu past `mp_force_pick_time`. That is how a player who reconnected mid-match
  ended up on the side opposite the one they had played the first half on, without touching a key.
  `spectate_mid_match_joins` puts them in spectator instead and leaves the choice to them; nothing is
  blocked and nobody is kicked.
- Scores live on the `cs_team_manager` entities, per side (`CT` / `TERRORIST`), so reading a *team's*
  score means knowing which side it is on. The plugin tracks that in one field, flipped by `.switch` and
  by every half swap. The swap rounds - the half and every overtime half - are the one piece of
  arithmetic that is easy to get wrong, and are under test.
- There are two per-round buy caps and the zeus has its own. `mp_weapons_allow_typecount` (engine
  default 5) is "how many purchases of each weapon type allowed per player per round"; it does not cover
  the zeus. `mp_weapons_allow_zeus` is a count too, not a 0/1 toggle - "how many Zeus purchases a player
  can make per round (0 to disallow, -1 to have no limit)". So unlimited warmup zeuses needs `-1` on the
  zeus cvar specifically. [SOURCE: `help` on both cvars, CS2 1.41.9.2, read 2026-10-24]
  The plugin follows the warmup edge: both `-1` in warmup, `1`/`5` once live. No cfg sets typecount, so
  the plugin is its only owner; a map change resets both, so the edge is re-armed on map start.
- A chat command runs inside the engine's netchan message processing, which has a CPU budget per call and
  drops the client whose message overran it. Every chat handler hands its work to the next frame.
