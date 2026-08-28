#!/usr/bin/env bash
# build.sh — builds AutoRezzer. Run from anywhere:
#   bash ~/Documents/GitHub/AutoRezzer/build.sh
#
# Requirements:
#   - dotnet SDK 10
#   - APPDATA shim: ~/.xlcore/msbuild-appdata/XIVLauncher/addon/Hooks/dev
#     -> symlink to ~/.xlcore/dalamud/Hooks/dev (satisfies the csproj's
#     $(appdata) HintPaths without touching the project file)
#
# In-game the dev plugin location points straight at bin/Release, so there is no
# deploy step — build, then "Scan Dev Plugins" in Dalamud to reload.
#
# DalamudPackager also writes bin/Release/AutoRezzer/latest.zip. That is the file
# that gets copied to PfPresets/repo/AutoRezzer.zip for the plugin repository.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# APPDATA IS ONLY SHIMMED WHERE THE SHIM ACTUALLY LEADS TO DALAMUD.
#
# The csproj finds Dalamud through $(appdata)\XIVLauncher\addon\Hooks\dev. On Linux there is no
# APPDATA, so this points it at the xlcore symlink described in the header. Under git-bash on
# Windows there IS an APPDATA and it is already right; overriding it there makes every Dalamud
# type in the project unresolvable, which reads like the source is broken rather than the
# environment.
#
# TESTED ON Dalamud.dll, NOT ON THE DIRECTORY. A build that runs with the wrong APPDATA still
# creates $APPDATA/NuGet on its way to failing, so after one bad run a directory test says
# "shim is present" forever after.
if [ -f "$HOME/.xlcore/msbuild-appdata/XIVLauncher/addon/Hooks/dev/Dalamud.dll" ]; then
  APPDATA_PREFIX="APPDATA=\$HOME/.xlcore/msbuild-appdata "
else
  APPDATA_PREFIX=""
fi

# --no-incremental because Dalamud only reloads a dev plugin when the DLL's
# timestamp changes, so always force a fresh write.
BUILD_CMD="cd '$SCRIPT_DIR' && ${APPDATA_PREFIX}dotnet build AutoRezzer.csproj -c Release -p:EnableWindowsTargeting=true --no-incremental"

if command -v dotnet >/dev/null 2>&1; then
  sh -c "$BUILD_CMD"
elif command -v distrobox >/dev/null 2>&1; then
  distrobox enter mywine -- sh -c "$BUILD_CMD"
else
  echo "No dotnet on PATH and no distrobox to fall back to." >&2
  exit 1
fi

echo "[OK] Build complete."
echo "     Dev plugin DLL: $SCRIPT_DIR/bin/Release/AutoRezzer.dll"
echo "     Repo package:   $SCRIPT_DIR/bin/Release/AutoRezzer/latest.zip"
echo "     Scan Dev Plugins in-game to reload."
