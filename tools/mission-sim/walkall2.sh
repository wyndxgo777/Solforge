#!/usr/bin/env bash
# Original Red War walker (walk2.lua), kept as the reference the Solforge simulator grew from
# (src/SunriseStudio.Missions/Simulation/mission_host.lua). Runs every Red War mission in four modes
# under WSL's lua5.4. Override M (project scripts), K (game sdk/lua), OUT and NAMES as needed.
M=${M:-"/mnt/c/Users/Seth/Desktop/sol/10-Project Sol/Project Sol/scripts"}
K=${K:-/mnt/c/Users/Seth/Desktop/sol/01-clients/sol-client/bin/x64/Sunrise/sdk/lua}
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=${OUT:-"$HERE/out"}
mkdir -p "$OUT"
for n in ${NAMES:-towerfall journey rise deadzone vacancy lights reclamation prospect calculon leech heist thunder revenge voyage ember reunion}; do
  for mode in play skip guard reload; do
    lua5.4 "$HERE/walk2.lua" "$M" "$K" mission_$n $mode > "$OUT/w2_${n}_$mode.txt" 2>&1
    echo "$n $mode: $(tail -2 "$OUT/w2_${n}_$mode.txt" | tr '\n' ' ')"
  done
done
