# Deploying the plugin

Build:

    cd plugin && dotnet build -c Release

That writes `plugin/GPP/bin/Release/net10.0/Gpp.dll`.

Install it to:

    <csgo>/addons/counterstrikesharp/plugins/Gpp/Gpp.dll

**CounterStrikeSharp hot-reloads a plugin when its dll is written, so copying the file *is* the
reload.** No server restart, nobody disconnected. Watch the server log for `Finished loading plugin
GPP` to confirm it took.

Config lives at:

    <csgo>/addons/counterstrikesharp/configs/plugins/Gpp/Gpp.json

and is **read only at plugin load**, so a config change does need a reload — touch the dll again, or
`css_plugins reload GPP`.

`examples/install.sh` is the script the original server uses. It is written for one machine's paths
(`/home/steam/cs2`, a `steam` user); read it before running it anywhere else.

## Admins

Admin commands default to `@css/config`, read from **CounterStrikeSharp's own**
`addons/counterstrikesharp/configs/admins.json` — not MatchZy's separate `cfg/MatchZy/admins.json`.
A server running both wants the same people in both files. `css_admins_reload` applies the CSS one
live; MatchZy's own `reload_admins` does the same for its list. Neither needs a restart.

## Prerequisites

- Metamod:Source
- CounterStrikeSharp **with the runtime** (the `with-runtime` build)
- MatchZy

MatchZy must be in sleep mode (`matchzy_autostart_mode 0`) — the plugin asserts this at load and on
every map change, but setting it in `cfg/MatchZy/config.cfg` too saves a surprise. See
`plugin/README.md` for why, and for the `sleep.cfg` edit that goes with it.

## Migrating from the PugDraft name

The plugin used to be called PugDraft and shipped as `PugDraft.dll`. The rename changes the
CounterStrikeSharp plugin folder and, with it, **where the config is read from**. CounterStrikeSharp
derives the config path from the plugin name, so a straight redeploy leaves GPP loading a freshly
generated default config — empty admin flag, default map pool, no Discord block. The vote would offer
seven maps nobody chose.

On the game server, with the server stopped or at least empty:

    CSS=<csgo>/addons/counterstrikesharp

    # 1. config first, so the new plugin finds it the moment it loads
    mkdir -p $CSS/configs/plugins/Gpp
    cp $CSS/configs/plugins/PugDraft/PugDraft.json $CSS/configs/plugins/Gpp/Gpp.json

    # 2. the new plugin
    mkdir -p $CSS/plugins/Gpp
    cp Gpp.dll $CSS/plugins/Gpp/Gpp.dll

    # 3. the old one, or both load and fight over every command
    rm -rf $CSS/plugins/PugDraft

Leave the old config directory in place until the new one has been proved on a live pug; it costs
nothing and is the only copy of your map pool.

Nothing in MatchZy, `gameinfo.gi`, the admin files or the map pool changes. Match configs written by
the draft are now named `gpp-<timestamp>.json` instead of `pugdraft-<timestamp>.json`; old ones stay
where they are and are not read again.

If you run the Discord bot, its shared-secret header changed from `X-PugDraft-Token` to `X-GPP-Token`.
That is internal to the plugin and the bot, so just deploy both — but deploy **both**, because an old
bot and a new plugin will not authenticate to each other.
