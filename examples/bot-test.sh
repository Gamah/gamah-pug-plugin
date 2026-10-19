#!/bin/bash
# Walk a whole pug on an empty server, one stage at a time. Bots stand in for players (count_bots in
# Gpp.json makes the plugin count them), so one person can get as far as a finished match with stats
# and a demo on disk.
#
# Bots cannot type, so three things only a console can do are stages here: the ready (bots never
# .ready), the side choice after the knife round (you may well be on the losing side), and shortening
# the match so it actually ends while you are watching.
#
#   bot-test.sh bots [n]        bots on, default 9
#   bot-test.sh captains        pin the two humans on the server as captains
#   bot-test.sh draft [n]       bots + draft on the current map - the quick path to a match
#   bot-test.sh start [n]       bots + the full flow, map vote first (you have to !1 to vote)
#   bot-test.sh ready           force the ready phase through
#   bot-test.sh stay|switch     answer the knife round from the console
#   bot-test.sh short [n]       mp_maxrounds n (default 4), overtime off - a match that ends soon
#   bot-test.sh normal          mp_maxrounds 24, overtime on again
#   bot-test.sh pause|unpause   from the console, which counts as an admin pause
#   bot-test.sh status          what the plugin and the engine both think is happening
#   bot-test.sh stop            !pug stop, bots off, rounds back to normal
#   bot-test.sh walk [n]        draft -> ready -> stay, with the waits in between
#
# RCON password comes from ~/.rcon_pw. The stats database and the demos live under /home/steam, which
# this user cannot read: `status` prints the two commands to check them with.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
PW=$(cat ~/.rcon_pw)
rcon() { python3 -I "$HERE/rcon.py" 127.0.1.1 27015 "$PW" "$@"; }

PLUGIN=/home/steam/cs2/game/csgo/addons/counterstrikesharp/plugins/Gpp
DEMOS=/home/steam/cs2/game/csgo/gpp-demos

bots_on() {
  local n=$1
  # The plugin owns the bots, not this script: anything that execs a phase cfg or restarts the warmup
  # can clear the quota, and !pug bots keeps putting them back for as long as the test runs.
  rcon "bot_difficulty 2" "mp_autoteambalance 0" "mp_limitteams 0" "css_pug bots $n" >/dev/null
  echo "$n bots asked for - they take a few seconds to turn up"
}

case ${1:-status} in
  bots)
    bots_on "${2:-9}"
    ;;

  captains)
    # The draw is over bots too when count_bots is on, and a bot captain neither knifes nor picks.
    rcon "css_pug captains humans" >/dev/null
    echo "the two humans here are the captains - they knife each other for first pick"
    ;;

  draft)
    bots_on "${2:-9}"
    sleep 6
    # force: skips min_players, skips the !captain sign-up window, and ends anything already running.
    rcon "css_pug draft force" >/dev/null
    echo "drafting on the current map. Bot captains pick on a 3s clock, so give it ~30s,"
    echo "then: bot-test.sh ready"
    ;;

  start)
    bots_on "${2:-9}"
    sleep 6
    rcon "css_pug start force" >/dev/null
    echo "map vote up - !1 (or !vote <n>) in chat to vote; bots do not vote."
    echo "The map changes, the draft runs, then: bot-test.sh ready"
    ;;

  ready)
    # Bots are not counted towards min_ready and cannot ready anyway, so with one human on the server
    # a single .ready is enough - this is the version that needs nobody at all.
    rcon "css_forceready" >/dev/null
    echo "readied. Knife round next (knife_round true), then: bot-test.sh stay"
    ;;

  stay|switch)
    rcon "css_$1" >/dev/null
    echo "$1 sent. Should be LIVE - check the demo and the score with: bot-test.sh status"
    ;;

  short)
    n=${2:-4}
    # Set after going live, or live.cfg puts mp_maxrounds back to 24 over the top of it.
    rcon "mp_maxrounds $n" "mp_overtime_enable 0" "mp_match_can_clinch 1" >/dev/null
    echo "mp_maxrounds $n, overtime off - run this AFTER going live, not before."
    ;;

  normal)
    rcon "mp_maxrounds 24" "mp_overtime_enable 1" >/dev/null
    echo "rounds back to 24 with overtime"
    ;;

  pause|unpause)
    rcon "css_$1" >/dev/null
    echo "$1 sent from the console, which counts as an admin $1"
    ;;

  stop)
    rcon "css_pug stop" "css_pug bots 0" "mp_maxrounds 24" "mp_overtime_enable 1" >/dev/null
    echo "stopped, bots cleared, rounds back to normal"
    ;;

  walk)
    "$0" draft "${2:-9}"
    echo "- waiting 40s for the draft"
    sleep 40
    "$0" ready
    echo "- waiting 25s for the knife round"
    sleep 25
    "$0" stay
    sleep 5
    "$0" status
    ;;

  status)
    rcon "css_pug status" "mp_teamname_1" "mp_teamname_2" "mp_maxrounds" \
         "mp_weapons_allow_typecount" "mp_halftime" "tv_enable" "css_plugins list"
    echo
    echo "The two things rcon cannot see (run these with sudo):"
    echo "  ls -l $DEMOS"
    echo "  sqlite3 $PLUGIN/gpp.db 'select * from gpp_matches; select name,team,kills,deaths,damage from gpp_players'"
    ;;

  *)
    sed -n '2,30p' "$0"
    exit 1
    ;;
esac
