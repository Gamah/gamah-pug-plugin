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

If the native library or `Gpp.deps.json` is missing, stats log one error and are skipped - the pug runs
on without them.

**CounterStrikeSharp hot-reloads a plugin when its dll is written, so copying the file *is* the
reload.** Which means `Gpp.dll` goes last, after everything it needs is already there. No server
restart, nobody disconnected. Watch the server log for `Finished loading plugin` to confirm it took.

**Do not install during a live match.** The reload replaces the plugin instance, and the match phase,
the rosters and the ready list live in the instance being replaced: the engine keeps playing, and the
plugin no longer knows a match is on. `examples/install.sh` asks the running plugin over rcon
(`css_pug status`) and refuses unless it is idle; `--force` installs anyway.

The three phase configs go to the game's own cfg directory:

    <csgo>/cfg/gpp/{warmup,knife,live}.cfg          # from examples/cfg/gpp

`warmup.cfg` is the one that holds the warmup open between pugs. Without it the plugin falls back to a
handful of built-in cvars and says so in the log.

Config lives at:

    <csgo>/addons/counterstrikesharp/configs/plugins/Gpp/Gpp.json

and is **read only at plugin load**, so a config change does need a reload - touch the dll again, or
`css_plugins reload <index>`. **By index, not by name:** `css_plugins reload Gpp` is refused. A plugin
folder that is new to the server is not auto-discovered either: it needs `css_plugins load Gpp` once.

`examples/install.sh` is the script the original server uses, and it is written for that machine's
paths (`/home/steam/cs2` and `/home/steam/cs2b`, a `steam` user, rcon on `127.0.1.1`). It takes the
server to install to as its argument - `install.sh a`, `install.sh b` - so read it before running it
anywhere else.

## Admins

Admin commands default to `@css/config`, read from **CounterStrikeSharp's own**
`addons/counterstrikesharp/configs/admins.json`. `css_admins_reload` applies it live; no restart.

## Prerequisites

- Metamod:Source
- CounterStrikeSharp **with the runtime** (the `with-runtime` build)
- GOTV enabled (`tv_enable 1`) if you want demos
- that is all. No other pug plugin is involved; see [match.md](match.md).
