#!/usr/bin/env bash
# Unattended in-game test driver for AutoTOT.
#
#   tools/livetest/run.sh plans/slava-solo.ini [more plan files...]
#
# For every [section] in the given plan files: write it as BepInEx/AutoTOT-livetest/plan.ini,
# launch Sea Power through Steam, wait for the in-game LiveTestRunner to write results/DONE,
# make sure the game process tree is gone, and collect the log, the result and the corpus files
# into tools/livetest/runs/<timestamp>-<section>/. Prints a per-round summary at the end.
#
# Steam must already be running and logged in. The driver never touches a game it did not launch:
# if Sea Power is already up it refuses to start.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
APPID=1286220
LAUNCH_GRACE=600      # s to wait for the reaper to appear after steam -applaunch
QUIT_GRACE=60         # s after DONE before we start killing
STARTUP_ALLOWANCE=900 # s added to the plan's own timeout for Proton start + mission load
MONITOR="${MONITOR:-}" # optional 1-based display index passed to the Unity player as -monitor N
WINDOW="${WINDOW:-}"   # optional WxH: run windowed at that size instead of fullscreen

GAME_DIR="${GAME_DIR:-$(sed -n 's/^[[:space:]]*<GameDir>\(.*\)<\/GameDir>.*/\1/p' "$ROOT/AutoTOT.local.props" 2>/dev/null | head -n1)}"
[ -d "$GAME_DIR" ] || { echo "GAME_DIR not found ($GAME_DIR); set GAME_DIR or AutoTOT.local.props" >&2; exit 2; }
CFG="$GAME_DIR/BepInEx/config/com.seapowermods.autotot.cfg"
# Settings the mod reads ONCE at startup cannot be set from inside the run, so the driver edits
# the config file for them and always puts the user's own file back. Example:
#   CFG_OVERRIDE="EstimatorThreads=0" tools/livetest/run.sh plans/big-strike-fps.ini
CFG_OVERRIDE="${CFG_OVERRIDE:-}"
LT="$GAME_DIR/BepInEx/AutoTOT-livetest"
LOG="$GAME_DIR/BepInEx/LogOutput.log"
SOLVE="$GAME_DIR/BepInEx/AutoTOT-solve"
# The game keeps its own logs beside BepInEx's: a session log carrying every Unity exception, and
# an event log categorised by Launch, EngageTask, Interception, Chaff, Jamming, Destruction,
# Blastzone and FriendlyFire. That event log is the only thing that says WHY a round failed to
# arrive, which the mod itself can only report as shot down, decoyed or guidance lost.
GAMELOGS="$GAME_DIR/Sea Power_Data/Logs"
RUNS="$HERE/runs"
# The offline lab lives in the flight model repo (SeaPowerFlightModel), normally checked out beside AutoTOT.
LAB="${FLIGHT_MODEL_DIR:-$ROOT/../SeaPowerFlightModel}/tools/lab"
# Every round a real run flies is evidence for the offline model, so each run's captures are
# imported into the checked-in corpus and the index rebuilt. Import is keyed on the round id and
# skips duplicates, so re-running a plan is safe. IMPORT=0 turns it off.
IMPORT="${IMPORT:-1}"
# Process reuse was tried on 2026-09-12 and REMOVED. The game cannot load a mission over another
# one in the same process: SceneManager_missionLoaded throws "Gameobject is not a root in a scene"
# and the Effects SpawnPool is registered twice, both as modal dialogs the player must dismiss.
# 75 of those exceptions in the reuse session against zero in every other session that day. The
# 38 to 39 s a launch costs is not worth corrupting the scene to save.
mkdir -p "$LT" "$RUNS"

[ $# -ge 1 ] || { echo "usage: $0 plan.ini [plan.ini...]" >&2; exit 2; }

# The runner is compiled only into the live test build. With a Workshop build installed the game
# would load, find nothing reading the plan, and sit in the menu until the timeout.
MODDLL="$GAME_DIR/Sea Power_Data/StreamingAssets/AutoTOT/AutoTOT.dll"
# grep -c, not grep -q: -q exits on the first match, strings then dies of SIGPIPE, and pipefail reads
# that as "not found".
if [ "$(strings -el "$MODDLL" 2>/dev/null | grep -c 'AutoTOT-livetest')" -eq 0 ]; then
    echo "the installed AutoTOT.dll has no live test runner (a Workshop build?)." >&2
    echo "install the test build first: LIVETEST=1 ./install.sh" >&2
    exit 2
fi

# dotnet is not always on PATH; install.sh uses the same private-SDK fallback. The lab's apphost
# also needs DOTNET_ROOT, so it is invoked through the SDK rather than run directly.
find_dotnet() {
    command -v dotnet >/dev/null 2>&1 && { echo dotnet; return; }
    for d in "$HOME/.dotnet-sdk" "$HOME/.dotnet"; do [ -x "$d/dotnet" ] && { echo "$d/dotnet"; return; }; done
}
lab_import() {
    local capture="$1" dn dll
    dn="$(find_dotnet)"
    [ -z "$dn" ] && { echo "   corpus: dotnet not found, skipping import"; return; }
    dll="$LAB/bin/Release/net8.0/autotot-lab.dll"
    [ -f "$dll" ] || "$dn" build "$LAB/Lab.csproj" -c Release -v quiet >/dev/null 2>&1
    [ -f "$dll" ] || { echo "   corpus: lab did not build, skipping import"; return; }
    echo "   corpus: importing $(ls "$capture"/*.solveinput 2>/dev/null | wc -l) capture(s)"
    "$dn" "$dll" import "$capture" | tail -n2 | sed 's/^/   /'
    "$dn" "$dll" index >/dev/null && echo "   corpus: index rebuilt"
}

game_pids() { pgrep -f "AppId=$APPID" 2>/dev/null; }
reaper_pid() { pgrep -f "reaper SteamLaunch AppId=$APPID" 2>/dev/null | head -n1; }
exe_pids() { pgrep -f 'Sea Power.exe' 2>/dev/null; }
descendants() { local p; for p in $(pgrep -P "$1" 2>/dev/null); do echo "$p"; descendants "$p"; done; }

if [ -n "$(game_pids)" ]; then
    echo "Sea Power is already running (pids: $(game_pids | tr '\n' ' ')). Refusing to start." >&2
    exit 3
fi
if [ -f "$LT/plan.running" ]; then
    echo "note: stale plan.running from an earlier run, removing"; rm -f "$LT/plan.running"
fi

# Split every plan file into one temp file per section.
SECTIONS=()
TMPD="$(mktemp -d)"
trap 'rm -rf "$TMPD"' EXIT
for f in "$@"; do
    [ -f "$f" ] || { echo "plan file not found: $f" >&2; exit 2; }
    awk -v out="$TMPD" '
        /^[[:space:]]*\[.*\][[:space:]]*$/ { name=$0; gsub(/^[[:space:]]*\[|\][[:space:]]*$/, "", name);
                                            gsub(/[^A-Za-z0-9._-]/, "_", name); n++;
                                            file=sprintf("%s/%03d-%s.ini", out, n, name); print > file; next }
        file { print > file }' "$f"
done
while IFS= read -r s; do SECTIONS+=("$s"); done < <(ls "$TMPD"/*.ini 2>/dev/null | sort)
[ ${#SECTIONS[@]} -ge 1 ] || { echo "no [section] found in the plan file(s)" >&2; exit 2; }

cfg_backup=""
restore_cfg() { [ -n "$cfg_backup" ] && [ -f "$cfg_backup" ] && cp "$cfg_backup" "$CFG" && rm -f "$cfg_backup" && cfg_backup=""; }
if [ -n "$CFG_OVERRIDE" ]; then
    [ -f "$CFG" ] || { echo "CFG_OVERRIDE given but no config at $CFG" >&2; exit 2; }
    cfg_backup="$(mktemp)"; cp "$CFG" "$cfg_backup"
    trap 'restore_cfg; rm -rf "$TMPD"' EXIT
    # A trap that only cleaned up let Ctrl-C return to the loop, which then launched the next section
    # without a plan; exiting runs the EXIT trap above once.
    trap 'exit 130' INT
    trap 'exit 143' TERM
    for kv in $CFG_OVERRIDE; do
        k="${kv%%=*}"; v="${kv#*=}"
        grep -qiE "^[[:space:]]*$k[[:space:]]*=" "$CFG" || { echo "unknown config key: $k" >&2; restore_cfg; exit 2; }
        sed -i -E "s|^([[:space:]]*$k[[:space:]]*=[[:space:]]*).*|\1$v|I" "$CFG"
        echo "config: $k = $v (restored afterwards)"
    done
fi

summary_rows=()
for sec in "${SECTIONS[@]}"; do
    name="$(basename "$sec" .ini | sed 's/^[0-9]*-//')"
    # Keys case-insensitively, as the in-game parser reads them.
    timeout_s="$(sed -n 's/^[[:space:]]*TimeoutSec[[:space:]]*=[[:space:]]*\([0-9.]*\).*/\1/Ip' "$sec" | head -n1)"
    contact_s="$(sed -n 's/^[[:space:]]*WaitForContactSec[[:space:]]*=[[:space:]]*\([0-9.]*\).*/\1/Ip' "$sec" | head -n1)"
    comp="$(sed -n 's/^[[:space:]]*Compression[[:space:]]*=[[:space:]]*\([0-9.]*\).*/\1/Ip' "$sec" | head -n1)"
    : "${timeout_s:=1500}" "${contact_s:=120}" "${comp:=1}"
    # TimeoutSec is sim seconds; at compression c it passes c times faster in wall time. In awk, since shell
    # arithmetic is integer-only: Compression=0.5 divided by zero there, and ".5" was a syntax error.
    limit="$(awk -v a="$STARTUP_ALLOWANCE" -v c="$contact_s" -v t="$timeout_s" -v k="$comp" \
        'BEGIN { if (k + 0 <= 0) k = 1; printf "%d", a + c + t / k }')"

    stamp="$(date +%Y%m%d-%H%M%S)"
    out="$RUNS/$stamp-$name"; mkdir -p "$out"
    echo "== [$name] limit ${limit}s, output $out"

    rm -rf "$LT/results"; mkdir -p "$LT/results"
    cp "$sec" "$LT/plan.ini"
    before_solve="$(mktemp)"; ls -1 "$SOLVE" 2>/dev/null | sort > "$before_solve"
    launch_marker="$(mktemp)"   # anything in the game's own log folder newer than this is ours

    t0=$(date +%s)
    extra=(); [ -n "$MONITOR" ] && extra=(-monitor "$MONITOR")
    if [ -n "$WINDOW" ]; then
        extra+=(-screen-fullscreen 0 -screen-width "${WINDOW%x*}" -screen-height "${WINDOW#*x}")
    fi
    steam -applaunch "$APPID" "${extra[@]}" >/dev/null 2>&1 &
    reaper=""
    while [ $(( $(date +%s) - t0 )) -lt $LAUNCH_GRACE ]; do
        reaper="$(reaper_pid)"; [ -n "$reaper" ] && break; sleep 2
    done
    if [ -z "$reaper" ]; then
        echo "   game process never appeared within ${LAUNCH_GRACE}s" | tee "$out/driver.txt"
        rm -f "$LT/plan.ini"; summary_rows+=("$name|NO LAUNCH"); continue
    fi
    echo "   reaper pid $reaper"

    status=""
    while :; do
        if [ -f "$LT/results/DONE" ]; then status="$(tr -d '\r' < "$LT/results/DONE" | head -n1)"; break; fi
        if ! kill -0 "$reaper" 2>/dev/null; then status="GAME EXITED WITHOUT DONE"; break; fi
        if [ $(( $(date +%s) - t0 )) -ge $limit ]; then status="DRIVER TIMEOUT after ${limit}s"; break; fi
        sleep 5
    done
    echo "   status: $status"

    # Shutdown. The runner quits on its own; give Proton time to unwind, then escalate.
    tq=$(date +%s)
    while kill -0 "$reaper" 2>/dev/null && [ $(( $(date +%s) - tq )) -lt $QUIT_GRACE ]; do sleep 2; done
    if kill -0 "$reaper" 2>/dev/null; then
        echo "   game still up ${QUIT_GRACE}s after finish; SIGTERM to Sea Power.exe"
        for p in $(exe_pids); do kill -TERM "$p" 2>/dev/null; done
        sleep 20
        if kill -0 "$reaper" 2>/dev/null; then
            echo "   SIGKILL to the reaper's tree"
            tree="$(descendants "$reaper") $reaper"
            for p in $tree; do kill -KILL "$p" 2>/dev/null; done
            sleep 5
        fi
    fi
    tw=$(date +%s)
    while [ -n "$(game_pids)" ] && [ $(( $(date +%s) - tw )) -lt 60 ]; do sleep 2; done
    [ -n "$(game_pids)" ] && echo "   WARNING: game pids still present: $(game_pids | tr '\n' ' ')"

    # Collect.
    [ -f "$LOG" ] && tr -d '\r' < "$LOG" > "$out/LogOutput.log"
    for r in "$LT"/results/*; do [ -f "$r" ] && tr -d '\r' < "$r" > "$out/$(basename "$r")"; done
    if [ -d "$SOLVE" ]; then
        mkdir -p "$out/solve"
        comm -13 "$before_solve" <(ls -1 "$SOLVE" | sort) | while IFS= read -r f; do cp "$SOLVE/$f" "$out/solve/"; done
    fi
    if [ -d "$GAMELOGS" ]; then
        mkdir -p "$out/gamelogs"
        find "$GAMELOGS" -maxdepth 1 -type f -newer "$launch_marker" -exec cp {} "$out/gamelogs/" ';' 2>/dev/null
        gl=$(ls "$out/gamelogs" 2>/dev/null | wc -l)
        ue=$(cat "$out"/gamelogs/*_session_*.html 2>/dev/null | grep -ac 'ERROR: ')
        echo "   game logs: $gl file(s), $ue Unity error line(s)"
    fi
    rm -f "$before_solve" "$launch_marker" "$LT/plan.running" "$LT/plan.ini"
    {
        echo "section=$name"; echo "status=$status"; echo "reaper=$reaper"
        echo "wall_s=$(( $(date +%s) - t0 ))"
    } > "$out/driver.txt"

    if [ "$IMPORT" = "1" ] && [ -d "$out/solve" ]; then lab_import "$out/solve"; fi

    for res in "$out"/*.result; do
        [ -f "$res" ] || continue
        rstatus="$(sed -n 's/^status=//p' "$res")"
        name="$(sed -n 's/^run=//p' "$res" | head -n1)"
        while IFS='|' read -r ammo tgt outc rng flight pred resid gap retg mn mx smin smax key; do
            # The game halves its physics step below 30 fps. A round integrated at 1/30, or one
            # whose step moved mid-flight, is not comparable with the rest and must be flagged.
            # Empty fields come from result files written before the step was recorded.
            step=$(awk -v a="$smin" -v b="$smax" 'BEGIN{
                if (a == "" || b == "") { print "?"; exit }
                if (a != b) { printf "STEP MOVED %s-%s", a, b; exit }
                if (a+0 > 0.02) { printf "HALF RATE %s", a; exit }
                print "ok" }')
            summary_rows+=("$name|$rstatus|$ammo|$tgt|$outc|${rng}m|${flight}s|${resid:--}|${gap:--}|$step")
        done < <(sed -n 's/^round=//p' "$res")
        [ -z "$(sed -n 's/^round=//p' "$res")" ] && summary_rows+=("$name|$rstatus|(no rounds)")
    done
    ls "$out"/*.result >/dev/null 2>&1 || summary_rows+=("$name|$status|(no result file)")
done

restore_cfg

echo
echo "section|status|ammo|target|outcome|final|flight|residual|gap|step"
printf '%s\n' "${summary_rows[@]}"
