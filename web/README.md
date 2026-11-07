# The stats site

Match history, per-player totals and the demo files, served from the plugin's own `gpp.db`. It is a
reader: it opens the database read-only, creates nothing, migrates nothing and has no forms, no admin
and no login. Everything that runs a pug is the plugin's job and stays there.

    web/Gpp.Web/StatsQueries.cs   every read, as SQL - compiled into Gpp.Tests as well, so the pairing
                                  with StatsStore is tested against a database StatsStore wrote
    web/Gpp.Web/Pages.cs          the HTML, server-rendered; player names go through HtmlEncode
    web/Gpp.Web/Program.cs        the routes, the settings and the demo directory

Pages: `/` match history (25 a page), `/match/{id}` one scoreboard, `/players` everyone by matches
played, `/player/{steamid64}` one player's totals and their matches, `/demos` the demo files,
`/healthz` which reads one row and answers 503 if it cannot.

## Run it

    export PATH=$HOME/.local/share/toolchains/dotnet10:$PATH
    dotnet build -c Release web/Gpp.Web/Gpp.Web.csproj
    GPP_DB=/path/to/gpp.db GPP_DEMOS=/path/to/demos \
      ASPNETCORE_URLS=http://127.0.0.1:5057 \
      dotnet web/Gpp.Web/bin/Release/net10.0/Gpp.Web.dll

`GPP_DB` is required and is the plugin's database - on a server that is
`addons/counterstrikesharp/plugins/Gpp/gpp.db`. `GPP_DEMOS` is optional; without it `/demos` says so.

## Two things that will bite you

- **Point `GPP_DB` at the plugin's `stats_snapshot`, not at the live database.** Opening a WAL database
  means creating its `-shm` sidecar in the database's own directory, and that is true of a read-only
  connection too - SQLite keeps its reader bookkeeping in there. The service user has no write access to
  the plugin directory, so pointed at the live file every page 500s on `SQLite Error 14: unable to open
  database file` while the data behind it is perfectly fine. Granting that write bit is the wrong answer:
  a reader of match history has no business holding one next to a live `Gpp.dll`. GPP's snapshot is a
  `VACUUM INTO` copy taken the moment each write commits, in rollback-journal mode, so reading it asks
  for nothing. It is as fresh as the last completed match, which is all these pages show.
- **The database is opened `Mode=ReadOnly`, and that is load-bearing.** A bug here cannot damage a
  night's stats. `immutable=1` would also open without write access and must not be used: it reads the
  main database file alone, so every row still in a `-wal` file is silently missing - measured against
  the live database, the last committed match disappears.
- **Nothing in the database records a demo's filename**, so a match's demos are found by matching the
  map and both team names against what is on disk, which is what `demo_name_format` puts there by
  default. Change that format and the links go quiet rather than wrong. A `demo` column on
  `gpp_matches`, written by the plugin, is what would make this exact.

## Deploying it

A systemd unit on the game VM, bound to localhost, reverse-proxied by whatever already holds the
certificate. It reads GPP's snapshot, so it needs no access to the plugin directory at all - only to the
snapshot directory, which it owns, and to the demo directory. Set `stats_snapshot` in `Gpp.json` to the
same path and reload the plugin.

    [Unit]
    Description=GPP stats site
    After=network-online.target

    [Service]
    User=gppweb
    SupplementaryGroups=steam
    Environment=GPP_DB=/var/lib/gpp-web/gpp.db
    Environment=GPP_DEMOS=/home/steam/cs2/game/csgo/gpp-demos
    Environment=ASPNETCORE_URLS=http://127.0.0.1:5057
    ExecStart=/usr/bin/dotnet /opt/gpp-web/Gpp.Web.dll
    Restart=always
    ProtectSystem=strict
    StateDirectory=gpp-web
    ReadOnlyPaths=/home/steam/cs2
    PrivateTmp=true

`SupplementaryGroups=steam` is still what gets the demo files read; the snapshot needs nothing. GPP runs
as `steam` and writes the snapshot, so the directory has to be writable by `steam` and readable by
`gppweb` - `StateDirectory=gpp-web` creates it `gppweb:gppweb`, so it wants `chgrp steam` and `chmod
g+w`, or simply `install -d -o steam -g gppweb -m 750 /var/lib/gpp-web`.

    [Install]
    WantedBy=multi-user.target

The site holds no secret: no rcon password, no token, no write path. What it does expose is every
player's SteamID64 and name, so put it behind the proxy you already trust rather than on the open
internet by itself.
