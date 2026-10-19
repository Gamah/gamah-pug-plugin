# Deploying the plugin

Build:

    cd plugin && dotnet build -c Release

That writes `plugin/Gpp/bin/Release/net10.0/`, which is **more than one file**: the stats database
pulls in `Microsoft.Data.Sqlite`, and that needs its managed assemblies, `Gpp.deps.json` (the thing
that tells the loader where the native library is) and `runtimes/linux-x64/native/libe_sqlite3.so`.

Install all of it to:

    <csgo>/addons/counterstrikesharp/plugins/Gpp/
      Gpp.dll  Gpp.deps.json  Microsoft.Data.Sqlite.dll  SQLitePCLRaw.*.dll
      runtimes/linux-x64/native/libe_sqlite3.so

If the native library or `Gpp.deps.json` is missing, stats log one error and are skipped — the pug runs
on without them.

**CounterStrikeSharp hot-reloads a plugin when its dll is written, so copying the file *is* the
reload.** Which means `Gpp.dll` goes last, after everything it needs is already there. No server
restart, nobody disconnected. Watch the server log for `Finished loading plugin` to confirm it took.

The three phase configs go to the game's own cfg directory:

    <csgo>/cfg/gpp/{warmup,knife,live}.cfg          # from examples/cfg/gpp

`warmup.cfg` is the one that holds the warmup open between pugs. Without it the plugin falls back to a
handful of built-in cvars and says so in the log.

Config lives at:

    <csgo>/addons/counterstrikesharp/configs/plugins/Gpp/Gpp.json

and is **read only at plugin load**, so a config change does need a reload — touch the dll again, or
`css_plugins reload <index>`. **By index, not by name:** `css_plugins reload Gpp` is refused.

`examples/install.sh` is the script the original server uses. It is written for one machine's paths
(`/home/steam/cs2`, a `steam` user); read it before running it anywhere else.

## Admins

Admin commands default to `@css/config`, read from **CounterStrikeSharp's own**
`addons/counterstrikesharp/configs/admins.json`. `css_admins_reload` applies it live; no restart.

## Prerequisites

- Metamod:Source
- CounterStrikeSharp **with the runtime** (the `with-runtime` build)
- GOTV enabled (`tv_enable 1`) if you want demos
- that is all. MatchZy is no longer required — see [match.md](match.md)

## Dropping MatchZy

GPP runs the match itself, so MatchZy is dead weight, but `match.matchzy_installed` defaults to true
so that a server still carrying it is kept quiet: asleep, and with its two kick cvars off. Once a pug
has been played end to end on GPP alone:

    css_plugins list                      # note MatchZy's index
    css_plugins unload <index>
    # then, with the server empty:
    rm -rf <csgo>/addons/counterstrikesharp/plugins/MatchZy

and set `match.matchzy_installed` to false, or the server console fills with unknown-command lines for
every `matchzy_*` cvar GPP asserts. `cfg/MatchZy/` can stay where it is; nothing reads it any more, and
it is the only copy of the settings `cfg/gpp/` was derived from.

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

Nothing in `gameinfo.gi`, the admin files or the map pool changes. The draft no longer writes a match
config at all, so any `pugdraft-*.json` or `gpp-*.json` left in `csgo/MatchZy/` is dead and can go.

If you run the Discord bot, its shared-secret header changed from `X-PugDraft-Token` to `X-GPP-Token`.
That is internal to the plugin and the bot, so just deploy both — but deploy **both**, because an old
bot and a new plugin will not authenticate to each other.
