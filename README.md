# Gamah's Pug Plugin (GPP)

Ten-man pug flow for a CS2 server: map vote, random captains, a captain knife for first pick, a draft,
then the match itself - ready up, knife for sides, go live, pauses, a demo and a stats row. MatchZy used
to run everything from `.ready` onwards and is no longer needed; [`docs/match.md`](docs/match.md) is what
replaced it and what was given up with it.

Two parts, and **the second one is optional**:

| | What it is | Needs |
| --- | --- | --- |
| [`plugin/`](plugin/) | The CounterStrikeSharp plugin. The whole pug lives here. | CS2 server, Metamod, CounterStrikeSharp |
| [`bot/`](bot/) | A Discord bot that moves the drafted teams into voice channels. | Python 3.11+, a Discord bot token |

The plugin does not need the bot. With `discord.base_url` left empty in `Gpp.json` it makes no
network calls at all, `!link-discord` tells players the server has no bot, and everything else behaves
exactly as it did before the bot existed. Run the plugin on its own and nothing is missing.

The bot does need the plugin: it has no way to know who drafted whom otherwise.

## Layout

```
plugin/Gpp/        CounterStrikeSharp plugin (C#, .NET 10)
plugin/Gpp.Tests/  the engine-free rules and the stats SQL, under xunit
bot/               Discord companion (Python) - optional
docs/              deployment, the match engine, the overlay, and the plugin <-> bot contract
examples/          a filled-in Gpp.json, the three phase cfgs, and an install script to adapt
```

## Quick start

Plugin only:

```
cd plugin && dotnet build -c Release
dotnet test Gpp.Tests
# copy the whole build output to csgo/addons/counterstrikesharp/plugins/Gpp/
# and examples/cfg/gpp to csgo/cfg/gpp/            (see docs/deploy.md - it is not one file any more)
```

See [`plugin/README.md`](plugin/README.md) for commands and configuration, and
[`docs/deploy.md`](docs/deploy.md) for installing it on a server.

Everything the pug asks for is drawn in the world - three `point_worldtext` boards in front of each
player, one of them the question and two of them the teams. [`docs/overlay.md`](docs/overlay.md) is how
that works and what was measured to get there; `!pug preview` plays every panel in order without
needing a pug.

With the Discord bot as well: [`docs/discord.md`](docs/discord.md).

## Status

Written for one server and generalised afterwards, so expect the rough edges of that: the config has
options nobody but the original server has exercised, and the Discord half has been tested against its
own test suite rather than against a live guild.
