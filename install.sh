#!/usr/bin/env bash
# Build AutoTOT and install it as a local Sea Power mod (loaded via AnchorChain).
set -euo pipefail

GAME_DIR="${GAME_DIR:-/NEW-DRIVE/SteamLibrary/steamapps/common/Sea Power}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet-sdk}"
export PATH="$DOTNET_ROOT:$PATH"

cd "$(dirname "$0")"

echo ">> building..."
dotnet build -c Release -v minimal

echo ">> staging dist..."
cp bin/Release/AutoTOT.dll dist/AutoTOT/AutoTOT.dll

DEST="$GAME_DIR/Sea Power_Data/StreamingAssets/AutoTOT"
echo ">> installing to: $DEST"
mkdir -p "$DEST"
cp dist/AutoTOT/_info.ini "$DEST/_info.ini"
cp dist/AutoTOT/AutoTOT.dll "$DEST/AutoTOT.dll"

echo ">> done. Enable 'Auto Time-on-Target' in the Mods menu, then restart the game."
