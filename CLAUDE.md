# gamah-pug-plugin

The 10-man pug flow for a CS2 server, as one CounterStrikeSharp plugin, plus an optional Discord bot.

    plugin/Gpp/        the plugin (C#, net10.0, CounterStrikeSharp 1.0.376)
    plugin/Gpp.Tests/  the engine-free rules and the stats SQL, under xunit
    bot/               Discord companion (Python) - optional, and the plugin makes no network calls
                       while discord.base_url is empty
    examples/cfg/gpp/  the three phase cfgs the plugin execs: warmup, knife, live
    examples/          a filled-in Gpp.json, install.sh, rcon.py, bot-test.sh
    docs/              what spans files

Build and test:

    export PATH=$HOME/.local/share/toolchains/dotnet10:$PATH
    dotnet build -c Release plugin/Gpp/Gpp.csproj
    dotnet test plugin/Gpp.Tests/Gpp.Tests.csproj

There is no CS2 server on this machine, so anything that touches the engine is verified by review and
by deploying it. Anything that does not should be moved behind a plain signature and tested: that is
what `WorldGeometry`, `HudMetrics`, `MatchRules` and `StatsStore` are, and every bug that recurred in
the overlay geometry recurred because the arithmetic was inside a plugin method nothing could call.

## What goes in a doc

Every sentence has to pass all three:

1. **It is true of the code as it stands**, and checkable against the code beside it.
2. **The code cannot say it** - the constraint a value is built around, a behaviour outside this repo
   (the engine, CounterStrikeSharp, the wire format), or an interface other code depends on.
3. **It does not go stale on its own.**

So: no history ("was 20", "used to be"), no incident reports - state the constraint in the present
tense instead, *nothing may be sent while `GamePhase` is 5* - no dates except on a `[SOURCE]` tag, no
pointers to branches or commits, no open questions (those are GitHub issues), and nothing that promises
a future. Mechanism goes in a comment beside the code; a file in `docs/` holds what spans files.

When cutting, delete rather than reword, and edit the section that owns the fact rather than appending
what was just learned to the end of a file.

`[SOURCE: ...]` marks a fact read out of somebody else's implementation rather than a documented
contract. It carries the version read and the date, because it can change without notice.

## Where things are written down

| | |
| --- | --- |
| [`README.md`](README.md) | what this is, and which half is optional |
| [`plugin/README.md`](plugin/README.md) | every command, every config key, the font widths |
| [`docs/match.md`](docs/match.md) | the phase machine, what each cfg owns, the end-of-match rule |
| [`docs/overlay.md`](docs/overlay.md) | the `point_worldtext` boards: the mount, the geometry, the measurements |
| [`docs/deploy.md`](docs/deploy.md) | installing it on a server, and why `Gpp.dll` is written last |
| [`docs/discord.md`](docs/discord.md) | the plugin ↔ bot HTTP contract and the linking flow |
| [`bot/README.md`](bot/README.md) | how to run the bot |

The server this runs on is documented in its own repo, not here: the box, the units, the updater and
what bites you there.

## Two things that will bite you

- **Writing `Gpp.dll` is the reload**, so the dll goes last and a deploy mid-match loses the match -
  the phase, the rosters and the ready list are in the instance being replaced. `install.sh` refuses
  unless the plugin reports itself idle.
- **The config is read only at plugin load.** `css_plugins reload <index>` - by index; the name is
  refused.
