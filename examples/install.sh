#!/bin/bash
# Installs GPP on one server of this VM. Run as root:
#   sudo bash ~/gpp/install.sh [a|b] [--force]
# Default target is a.  --force installs during a live match.
set -euo pipefail

TARGET=a
FORCE=0
for arg in "$@"; do
  case $arg in
    a|A) TARGET=a ;;
    b|B) TARGET=b ;;
    --force) FORCE=1 ;;
    *) echo "usage: install.sh [a|b] [--force]"; exit 1 ;;
  esac
done

SRC=${SRC:-/home/cs2/gpp}
case $TARGET in
  a) ROOT=/home/steam/cs2;  PORT=27015 ;;
  b) ROOT=/home/steam/cs2b; PORT=27016 ;;
esac
CS2=$ROOT/game/csgo
CSS=$CS2/addons/counterstrikesharp
PLUGIN=$CSS/plugins/Gpp

echo "== target: server $TARGET  ($ROOT, rcon :$PORT)"
[ -f "$SRC/Gpp.dll" ] || { echo "no Gpp.dll in $SRC"; exit 1; }
[ -d "$CSS" ] || { echo "no CounterStrikeSharp at $CSS"; exit 1; }

# Writing Gpp.dll is the reload, and a reload mid-match loses the match: the phase, the rosters and the
# ready list are all in the plugin instance being replaced. So ask the running plugin what it is doing.
echo "== match guard"
if [ "$FORCE" = 1 ]; then
  echo "  --force: not asking"
elif [ ! -f /home/cs2/.rcon_pw ]; then
  echo "  no /home/cs2/.rcon_pw - cannot ask whether a match is running; use --force"; exit 1
else
  status=$(python3 "$SRC/rcon.py" 127.0.1.1 "$PORT" "$(cat /home/cs2/.rcon_pw)" 'css_pug status' 2>&1 | tail -n +2)
  echo "  $status"
  case $status in
    *Idle.*|*"No match running."*) ;;
    *) echo "  refusing: the server is busy. !pug stop it, or install with --force"; exit 1 ;;
  esac
fi

# The plugin is not one file: the stats database pulls in Microsoft.Data.Sqlite, which needs its managed
# assemblies, Gpp.deps.json (that is what tells the loader where the native library is) and
# runtimes/linux-x64. Copy the whole build output, not just the dll.
echo "== plugin"
install -d -o steam -g steam -m 755 "$PLUGIN"
install -d -o steam -g steam -m 755 "$PLUGIN/runtimes/linux-x64/native"
for file in "$SRC"/*.dll "$SRC"/Gpp.deps.json "$SRC"/Gpp.runtimeconfig.json; do
  [ -f "$file" ] || continue
  install -o steam -g steam -m 644 "$file" "$PLUGIN/$(basename "$file")"
done
if [ -f "$SRC/runtimes/linux-x64/native/libe_sqlite3.so" ]; then
  install -o steam -g steam -m 644 "$SRC/runtimes/linux-x64/native/libe_sqlite3.so" \
    "$PLUGIN/runtimes/linux-x64/native/libe_sqlite3.so"
else
  echo "  no libe_sqlite3.so in $SRC/runtimes/linux-x64/native - stats will log an error and be skipped"
fi
# Written last: CounterStrikeSharp reloads the plugin the moment Gpp.dll is written, so everything it
# needs has to be in place before that happens.
install -o steam -g steam -m 644 "$SRC/Gpp.dll" "$PLUGIN/Gpp.dll"

echo "== phase cfgs"
install -d -o steam -g steam -m 755 "$CS2/cfg/gpp"
for cfg in warmup knife live; do
  src="$SRC/cfg/gpp/$cfg.cfg"
  dest="$CS2/cfg/gpp/$cfg.cfg"
  if [ ! -f "$src" ]; then
    echo "  no $src - skipping"
  elif [ -f "$dest" ]; then
    echo "  $dest exists, leaving it alone"
  else
    install -o steam -g steam -m 644 "$src" "$dest"
    echo "  wrote $dest"
  fi
done

echo "== workshop maps installed"
for dir in /home/steam/cs2/steamapps/workshop/content/730/*; do
  [ -d "$dir" ] || continue
  vpk=$(find "$dir" -maxdepth 2 -name '*.vpk' -printf '%f\n' 2>/dev/null | head -1)
  echo "  $(basename "$dir")  ${vpk:-?}"
done

echo "== GPP config"
install -d -o steam -g steam -m 755 "$CSS/configs/plugins/Gpp"
CFG=$CSS/configs/plugins/Gpp/Gpp.json
if [ -f "$CFG" ]; then
  echo "  $CFG exists, leaving it alone"
else
  install -o steam -g steam -m 644 "$SRC/Gpp.json" "$CFG"
  echo "  wrote $CFG"
fi

# Admin commands check the controller's SteamID against CounterStrikeSharp's own admin list, so a server
# with nobody holding @css/config has no working !pug start.
echo "== CounterStrikeSharp admins"
python3 - "$CSS/configs/admins.json" <<'PY'
import json, sys

path = sys.argv[1]
try:
    with open(path) as handle:
        admins = json.load(handle)
except FileNotFoundError:
    sys.exit(f"  {path} does not exist - nobody can run !pug start")
except json.JSONDecodeError as error:
    sys.exit(f"  {path} is not valid JSON ({error})")

holders = [value.get("identity") for value in admins.values()
           if "@css/config" in value.get("flags", [])]
if holders:
    print(f"  {len(holders)} admin(s) with @css/config")
else:
    print(f"  no @css/config in {path} - nobody can run !pug start")
PY

echo
echo "Done. Copying the dll is the reload - CounterStrikeSharp picks it up on write."
echo "Then check:  rcon 127.0.1.1 $PORT <pw> 'css_plugins list'"
echo "A config change needs a reload by index:  css_plugins reload <n>  (the name is refused)."
