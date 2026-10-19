# Replacing MatchZy

GPP currently runs the pug up to the point where MatchZy takes over the match itself. This is what
MatchZy is still doing, so the work of dropping it can be scoped against something real. Line
references are MatchZy 0.9.1 (build 89c1de8), read 2026-10-19.

## What the handoff looks like today

GPP sets the team names, puts the drafted players on their sides, and starts MatchZy's own ready
flow with `css_sleep;css_match`. **No match config is loaded, on purpose** - `matchzy_loadmatch` is
the only thing that sets `isMatchSetup`, and that flag is what kicks anyone outside the roster on
connect (`EventHandlers.cs` EventPlayerConnectFullHandler), forces roster players back onto their
drafted side (`Utility.cs` UpdatePlayersMap) and parks everyone else in spectator (`Utility.cs`
SwitchPlayerTeam). Open joining is the whole reason the config is gone; anything that puts
`matchzy_loadmatch` back brings the kicks back with it.

The one exception is the `draft_then_veto` flow, which still writes a config because the veto is
MatchZy's and reads its pool from one.

## What GPP already owns

Map vote, captain knife, draft, the Center/Alert overlay, warmup money, the per-round buy cap
(`SyncBuyCap`), force-spectate, the ready gate, and the ready tracking the panel lists. Ready state
is *inferred* from the ready commands going past - MatchZy exposes no way to read
`playerReadyStatus` - so that mirror disappears the moment MatchZy does, replaced by the real thing.

## What MatchZy still provides

- **Ready system.** `.ready` `.r` `.unready` `.notready` `.ur` `.forceready`, chat-only, gated on
  `readyAvailable`; threshold from `matchzy_minimum_ready_required`. `ReadySystem.cs`,
  `ReadyTimeout.cs` (the latter also has join mode, `readyMode 1`, unused here).
- **Phase machine and cfg execs.** sleep -> warmup -> knife -> live, each exec'ing
  `cfg/MatchZy/{sleep,warmup,knife,live}.cfg`. `Utility.cs` StartWarmup / StartKnifeRound /
  StartLive, ResetMatch at `Utility.cs:443`, StartMatchMode at `Utility.cs:1399` (only starts from
  sleep or practice), StartSleepMode at `SleepMode.cs:14`.
- **Knife round.** `isKnifeRequired` -> StartKnifeRound (`Utility.cs:845-862`), then `.stay` /
  `.switch` / `.swap`. The winner is decided from who is alive when the round ends, which is why
  GPP's ready gate exists at all - a knife round that starts with someone dead or in spectator is
  decided on the spot.
- **Match end.** HandleMatchEnd -> EndSeries -> ResetMatch, the last one only after
  `mp_match_restart_delay` (12s stock), extended further if GOTV still needs to flush. This is why
  the handoff retries: `matchStarted` stays true for that whole window and `css_sleep` refuses.
- **Scores, team names, clan tags.** `mp_teamname_1/2`, HandleClanTags, SwapSidesInTeamData at half.
- **Pauses.** `.pause` `.unpause` `.tech`, `matchzy_max_tech_pauses`, `matchzy_tech_pause_time`.
- **Round backups and restore.** SetupRoundBackupFile, `.restore`, optional remote backup URL.
- **Demos.** `matchzy_demo_recording_enabled`, `matchzy_demo_name_format`, `matchzy_demo_path`,
  optional upload URL.
- **Stats.** SQLite `matchzy_stats_matches` / `_players` / `_maps`, InitMatch / SetMatchEndData.
- **Admin model.** `cfg/MatchZy/admins.json`, read at plugin load, *plus* a fallback to
  CounterStrikeSharp's own admin data - and `player == null` (server console or rcon) is always
  treated as admin (`Utility.cs:130-139`), which is what lets GPP drive `css_*` commands at all.

## Decisions the next session has to make

1. **Who holds the warmup.** Today `cfg/MatchZy/sleep.cfg` owns it (it was edited to end in
   `mp_warmup_start` / `mp_warmuptime 9999` / `mp_warmup_pausetimer 1`), and the plugin deliberately
   does not also do it. Without MatchZy nothing execs that file, so GPP has to hold warmup itself or
   exec its own cfg - and the duplication that was avoided before becomes the only option.
2. **Round backups are the big one.** Dropping them means a crashed or disconnected match cannot be
   resumed mid-match. Decide explicitly whether GPP reimplements `mp_backup_round_file` handling and
   a `.restore`, or whether pugs simply accept the loss.
3. **Stats and demos are separable** and could go first or be skipped: demos are engine cvars plus a
   filename, stats are a SQLite table GPP could own outright.
4. **GOTV flush before a map change.** HandleMatchEnd extends the restart delay to let the broadcast
   finish; a naive reimplementation truncates the last round of every demo.
5. **No hybrid is available.** "Keep MatchZy for backups and stats only" does not work - its
   per-match bookkeeping hangs off a loaded config, and loading one is what kicks people. Either it
   runs the match (today) or it goes entirely.

## Things measured on this server, worth not rediscovering

- `.ready` typed in chat only becomes `css_ready` when `.` is a CounterStrikeSharp chat trigger;
  otherwise MatchZy handles it off its own `EventPlayerChat` hook and no command listener sees it.
  GPP watches both routes. A GPP-owned ready system would make this moot - and would also close the
  gap where a chat `.ready` can walk past the ready gate.
- `mp_weapons_allow_zeus` is a 0/1 toggle, not a count. The per-round buy cap is
  `mp_weapons_allow_typecount` (engine default 5). Neither `sleep.cfg` nor `live.cfg` sets it, so
  GPP follows the warmup edge instead: `-1` in warmup, `5` once live.
- `cfg/MatchZy/warmup.cfg` starts with `bot_quota 0` and is exec'd on every map start and warmup
  restart, which is why test bots vanish seconds after being added.
