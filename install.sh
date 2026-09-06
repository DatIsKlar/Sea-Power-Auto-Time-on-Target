#!/usr/bin/env bash
# Build AutoTOT and install it as a local Sea Power mod (loaded via AnchorChain).
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

echo ">> building..."
dotnet build -c Release -v minimal

echo ">> staging dist..."
cp bin/Release/AutoTOT.dll dist/AutoTOT/AutoTOT.dll

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
install_atomic dist/AutoTOT/AutoTOT.dll "$DEST/AutoTOT.dll"

echo ">> done. Enable 'Auto Time-on-Target' in the Mods menu, then restart the game."
