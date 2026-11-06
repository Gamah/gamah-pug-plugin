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

- **The database is opened `Mode=ReadOnly`, and that is load-bearing.** A reader shares the file with a
  live match because the plugin turns WAL on, and read-only means a bug here cannot damage a night's
  stats. `immutable=1` would also open it without write access and must not be used: it reads the main
  database file alone, so every row still in the `-wal` file is silently missing - measured, the last
  committed match disappears.
- **Nothing in the database records a demo's filename**, so a match's demos are found by matching the
  map and both team names against what is on disk, which is what `demo_name_format` puts there by
  default. Change that format and the links go quiet rather than wrong. A `demo` column on
  `gpp_matches`, written by the plugin, is what would make this exact.

## Deploying it

A systemd unit on the game VM, bound to localhost, reverse-proxied by whatever already holds the
certificate. It needs read access to the plugin directory - the service user must be able to open
`gpp.db` and its `-wal`/`-shm` files, which is a group membership, not a copy of the database.

    [Unit]
    Description=GPP stats site
    After=network-online.target

    [Service]
    User=gppweb
    SupplementaryGroups=steam
    Environment=GPP_DB=/home/steam/cs2/game/csgo/addons/counterstrikesharp/plugins/Gpp/gpp.db
    Environment=GPP_DEMOS=/home/steam/cs2/game/csgo/gpp-demos
    Environment=ASPNETCORE_URLS=http://127.0.0.1:5057
    ExecStart=/usr/bin/dotnet /opt/gpp-web/Gpp.Web.dll
    Restart=always
    ProtectSystem=strict
    ReadOnlyPaths=/home/steam/cs2
    PrivateTmp=true

    [Install]
    WantedBy=multi-user.target

The site holds no secret: no rcon password, no token, no write path. What it does expose is every
player's SteamID64 and name, so put it behind the proxy you already trust rather than on the open
internet by itself.
