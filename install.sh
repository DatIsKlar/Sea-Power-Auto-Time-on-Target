#!/usr/bin/env bash
# Build AutoTOT and install it as a local Sea Power mod (loaded via AnchorChain).
# LIVETEST=1 ./install.sh installs the live test build instead (see below).
set -euo pipefail

# Fall back to a private .NET SDK install only when dotnet isn't on PATH already.
if ! command -v dotnet >/dev/null 2>&1; then
    if [ -z "${DOTNET_ROOT:-}" ]; then
        for d in "$HOME/.dotnet-sdk" "$HOME/.dotnet"; do
            if [ -d "$d" ]; then DOTNET_ROOT="$d"; break; fi
        done
    fi
    if [ -z "${DOTNET_ROOT:-}" ]; then
        echo "dotnet not found on PATH. Install the .NET SDK, or set DOTNET_ROOT/PATH to your SDK." >&2
        exit 1
    fi
    export DOTNET_ROOT PATH="$DOTNET_ROOT:$PATH"
fi

cd "$(dirname "$0")"

# Install destination. Falls back to <GameDir> from AutoTOT.local.props (the same
# file the build uses), so a configured dev machine can just run ./install.sh.
if [ -z "${GAME_DIR:-}" ] && [ -f AutoTOT.local.props ]; then
    GAME_DIR="$(sed -n 's/^[[:space:]]*<GameDir>\(.*\)<\/GameDir>.*/\1/p' AutoTOT.local.props | head -n1)"
fi
if [ -z "${GAME_DIR:-}" ]; then
    echo "GAME_DIR is not set. Point it at your Sea Power install, e.g.:" >&2
    echo "  GAME_DIR=\"/path/to/Sea Power\" ./install.sh" >&2
    exit 1
fi
# Checked before building: a typo, or an MSBuild property such as $(HOME) that only the build expands,
# would otherwise create a stray folder below and report the install as done.
if [ ! -d "$GAME_DIR/Sea Power_Data/StreamingAssets" ]; then
    echo "GAME_DIR does not look like a Sea Power install (no Sea Power_Data/StreamingAssets): $GAME_DIR" >&2
    exit 1
fi

# LIVETEST=1 builds the live test build (runner compiled in, bin/LiveTest/) and installs it without
# touching dist/, which only ever holds the Workshop build.
if [ "${LIVETEST:-0}" = "1" ]; then
    echo ">> building the live test build (runner included)..."
    dotnet build -c Release -v minimal -p:LiveTest=true
    SRC=bin/LiveTest/AutoTOT.dll
else
    echo ">> building..."
    dotnet build -c Release -v minimal
    echo ">> staging dist..."
    cp bin/Release/AutoTOT.dll dist/AutoTOT/AutoTOT.dll
    SRC=dist/AutoTOT/AutoTOT.dll
fi

# The build compiles the model at FlightModelDir: AutoTOT.local.props when it sets one (normally a working copy at
# ../SeaPowerFlightModel), else the FlightModel submodule, while GitHub builds the commit the submodule pins. Say so
# when they differ, so a release does not ship a model the repository cannot rebuild. Read the same way the build
# resolves it, rather than guessed, so the note compares the checkout that was actually compiled.
MODEL_DIR="$(sed -n 's/^[[:space:]]*<FlightModelDir>\(.*\)<\/FlightModelDir>.*/\1/p' AutoTOT.local.props 2>/dev/null | head -n1 || true)"
MODEL_DIR="${MODEL_DIR:-FlightModel}"
# The git reads are allowed to fail (a ZIP download, git not installed): under set -e a failed pipeline would
# end the script here, after the build and before the install, without a word.
PIN="$(git ls-tree HEAD FlightModel 2>/dev/null | awk '{print $3}' || true)"
if [ -n "$PIN" ] && [ -e "$MODEL_DIR/.git" ]; then
    HEAD_SHA="$(git -C "$MODEL_DIR" rev-parse HEAD 2>/dev/null || true)"
    DIRTY="$(git -C "$MODEL_DIR" status --porcelain --untracked-files=no -- Simulation Support Diagnostics 2>/dev/null || true)"
    if [ -n "$HEAD_SHA" ] && { [ "$HEAD_SHA" != "$PIN" ] || [ -n "$DIRTY" ]; }; then
        echo ">> NOTE: built with the flight model at $MODEL_DIR (${HEAD_SHA:0:7}${DIRTY:+, uncommitted changes}), but the"
        echo "   FlightModel submodule pins ${PIN:0:7}. Before a release: commit the model, then"
        echo "   git -C FlightModel fetch && git -C FlightModel checkout ${HEAD_SHA:0:7} && git add FlightModel"
    fi
fi

DEST="$GAME_DIR/Sea Power_Data/StreamingAssets/AutoTOT"
echo ">> installing to: $DEST"
mkdir -p "$DEST"

# Install by atomic rename, never by writing the destination in place.
#
# A running game has the mod DLL memory-mapped. `cp` opens the destination with
# O_TRUNC and rewrites it, so the live process's mapping starts returning garbage
# metadata: Mono then reports nonsense type names and "Method has zero rva" from
# whatever it JITs next, which looks exactly like a mod bug and is not one.
# rename(2) swaps the directory entry instead and leaves the old inode alone, so a
# running game keeps working on the old copy and picks the new one up on restart.
install_atomic() {
    src="$1"; dst="$2"
    tmp="$(dirname "$dst")/.$(basename "$dst").new.$$"
    cp "$src" "$tmp"
    mv -f "$tmp" "$dst"
}

install_atomic dist/AutoTOT/_info.ini "$DEST/_info.ini"
install_atomic "$SRC" "$DEST/AutoTOT.dll"

echo ">> done. Enable 'Auto Time-on-Target' in the Mods menu, then restart the game."
