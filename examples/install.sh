#!/bin/bash
# Installs GPP on this VM. Run as root:  sudo bash ~/gpp/install.sh
set -euo pipefail

SRC=${SRC:-/home/cs2/gpp}
CS2=/home/steam/cs2/game/csgo
CSS=$CS2/addons/counterstrikesharp
PLUGIN=$CSS/plugins/Gpp

[ -f "$SRC/Gpp.dll" ] || { echo "no Gpp.dll in $SRC"; exit 1; }

echo "== plugin"
install -d -o steam -g steam -m 755 "$PLUGIN"
install -o steam -g steam -m 644 "$SRC/Gpp.dll" "$PLUGIN/Gpp.dll"

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
