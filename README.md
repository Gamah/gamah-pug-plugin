# Gamah's Pug Plugin (GPP)

Ten-man pug flow for a CS2 server running MatchZy: map vote, random captains, a captain knife for first
pick, a draft, then the teams handed to MatchZy to run the match.

Two parts, and **the second one is optional**:

| | What it is | Needs |
| --- | --- | --- |
| [`plugin/`](plugin/) | The CounterStrikeSharp plugin. The whole pug flow lives here. | CS2 server, Metamod, CounterStrikeSharp, MatchZy |
| [`bot/`](bot/) | A Discord bot that moves the drafted teams into voice channels. | Python 3.11+, a Discord bot token |

The plugin does not need the bot. With `discord.base_url` left empty in `Gpp.json` it makes no
network calls at all, `!link-discord` tells players the server has no bot, and everything else behaves
exactly as it did before the bot existed. Run the plugin on its own and nothing is missing.

The bot does need the plugin: it has no way to know who drafted whom otherwise.

## Layout

```
plugin/      CounterStrikeSharp plugin (C#, .NET 10)
bot/         Discord companion (Python) - optional
docs/        deployment, and the plugin <-> bot contract
examples/    a filled-in Gpp.json and an install script to adapt
```

## Quick start

Plugin only:

```
cd plugin && dotnet build -c Release
# copy Gpp.dll to csgo/addons/counterstrikesharp/plugins/Gpp/
```

See [`plugin/README.md`](plugin/README.md) for commands and configuration, and
[`docs/deploy.md`](docs/deploy.md) for installing it on a server.

With the Discord bot as well: [`docs/discord.md`](docs/discord.md).

## Status

Written for one server and generalised afterwards, so expect the rough edges of that: the config has
options nobody but the original server has exercised, and the Discord half has been tested against its
own test suite rather than against a live guild.
