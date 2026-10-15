#!/usr/bin/env bash
# Builds a self-contained PKHeX (Avalonia) for Linux and optionally installs a desktop launcher.
# Usage: ./build-linux.sh [--install] [--rid linux-x64|linux-arm64]
set -euo pipefail

cd "$(dirname "$0")"
RID="linux-x64"
INSTALL=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --install) INSTALL=1 ;;
    --rid) RID="$2"; shift ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
  shift
done

OUT="$(pwd)/publish/$RID"
dotnet publish -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT"
echo "Built: $OUT/PKHeX.Avalonia"

if [[ $INSTALL -eq 1 ]]; then
  DEST="$HOME/.local/share/pkhex"
  mkdir -p "$DEST" "$HOME/.local/bin" "$HOME/.local/share/applications"
  cp -r "$OUT"/. "$DEST"/
  cp ../icon.png "$DEST/icon.png"
  ln -sf "$DEST/PKHeX.Avalonia" "$HOME/.local/bin/pkhex"
  sed -e "s|@EXEC@|$DEST/PKHeX.Avalonia|" -e "s|@ICON@|$DEST/icon.png|" pkhex.desktop.in \
    > "$HOME/.local/share/applications/pkhex.desktop"
  echo "Installed. Launch \"PKHeX\" from your applications menu, or run: pkhex"
fi
