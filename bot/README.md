# gpp-bot

Optional Discord companion for [GPP](../plugin) CS2 plugin. Moves each drafted team into its
own voice channel at the handoff and puts everyone back when the match ends.

The plugin runs perfectly well without this. See [../docs/discord.md](../docs/discord.md) for the
setup, the permissions, the linking flow and the HTTP contract — this file is only how to run it.

## Run

    python3 -m venv .venv
    .venv/bin/pip install -e .
    cp config.example.toml config.toml   # fill it in
    .venv/bin/gpp-bot -c config.toml

`-v` for debug logging. `curl localhost:8787/health` to check it is alive.

## Test

    .venv/bin/pip install aiohttp
    .venv/bin/python -m unittest discover -s tests

The tests cover the link table and the HTTP API with a stubbed mover, so they need no token and no
guild. The Discord half — actually moving people — has no coverage and wants a throwaway guild.

## Layout

    gpp_bot/config.py   config.toml, validated at startup
    gpp_bot/store.py    SteamID -> Discord ID table (sqlite) and link codes
    gpp_bot/mover.py    the voice-channel moves, and only those
    gpp_bot/api.py      the three HTTP endpoints the plugin calls
    gpp_bot/bot.py      discord.py client and the slash commands
