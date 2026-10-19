#!/bin/bash
# Installs GPP on this VM. Run as root:  sudo bash ~/gpp/install.sh
set -euo pipefail

SRC=${SRC:-/home/cs2/gpp}
CS2=/home/steam/cs2/game/csgo
CSS=$CS2/addons/counterstrikesharp
PLUGIN=$CSS/plugins/Gpp

[ -f "$SRC/Gpp.dll" ] || { echo "no Gpp.dll in $SRC"; exit 1; }

# The plugin is no longer one file: the stats database pulls in Microsoft.Data.Sqlite, which needs its
# managed assemblies, Gpp.deps.json (that is what tells the loader where the native library is) and
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

echo "== CounterStrikeSharp admins"
python3 - "$CSS/configs/admins.json" "$CS2/cfg/MatchZy/admins.json" <<'PY'
import json, os, shutil, sys

css_path, mz_path = sys.argv[1], sys.argv[2]

def load(path):
    try:
        with open(path) as handle:
            return json.load(handle)
    except FileNotFoundError:
        return {}
    except json.JSONDecodeError as error:
        print(f"  {path} is not valid JSON ({error}) - not touching it")
        sys.exit(1)

css, matchzy = load(css_path), load(mz_path)
steam_ids = [key for key in matchzy if key.isdigit()]
if not steam_ids:
    print(f"  no SteamIDs in {mz_path} - add yourself to {css_path} by hand")
    sys.exit(0)

changed = False
for steam_id in steam_ids:
    entry = next((value for value in css.values()
                  if str(value.get("identity", "")) == steam_id), None)
    if entry is None:
        css[f"pug-{steam_id}"] = {
            "identity": steam_id,
            "flags": ["@css/config", "@css/generic"],
        }
        print(f"  added {steam_id}")
        changed = True
    elif "@css/config" not in entry.get("flags", []):
        entry.setdefault("flags", []).append("@css/config")
        print(f"  gave {steam_id} @css/config")
        changed = True
    else:
        print(f"  {steam_id} already has @css/config")

if changed:
    if os.path.exists(css_path):
        shutil.copy2(css_path, css_path + ".gpp.bak")
    with open(css_path, "w") as handle:
        json.dump(css, handle, indent=2)
    shutil.chown(css_path, "steam", "steam")
    print(f"  wrote {css_path}")
PY

echo
echo "Done. Copying the dll is the reload - CounterStrikeSharp picks it up on write."
echo "Then check:  rcon 127.0.1.1 27015 <pw> 'css_plugins list'"
echo "A config change needs a reload by index:  css_plugins reload <n>  (the name is refused)."
