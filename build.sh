#!/usr/bin/env bash
# build.sh — Linux counterpart of build.bat. Run from anywhere:
#   bash /var/mnt/windows/Users/Maro/Documents/Project/PfPresets/build.sh
#
# Requirements (already set up 2026-07-25):
#   - dotnet-sdk-10.0 inside the `mywine` distrobox
#   - APPDATA shim: ~/.xlcore/msbuild-appdata/XIVLauncher/addon/Hooks/dev
#     -> symlink to ~/.xlcore/dalamud/Hooks/dev (satisfies the csproj's
#     $(appdata) HintPaths without touching the project file)
#
# In-game, the dev plugin location points straight at bin/Release, so there is
# no deploy step — build, then "Scan Dev Plugins" in Dalamud to reload:
#   Z:\var\mnt\windows\Users\Maro\Documents\Project\PfPresets\bin\Release\PfPresets.dll
#
# Community ratings (Phase 2) are compiled in by default here, because this is the
# dev loop for the third-party build. Pass --no-ratings for the conservative build
# that omits them entirely and stays submittable to the official Dalamud repo:
#   bash build.sh --no-ratings
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

ENABLE_RATINGS=true
for arg in "$@"; do
  case "$arg" in
    --no-ratings) ENABLE_RATINGS=false ;;
    --ratings)    ENABLE_RATINGS=true ;;
    *) echo "Unknown option: $arg (expected --ratings or --no-ratings)" >&2; exit 2 ;;
  esac
done

# The no-ratings build goes somewhere else, and this is not a tidiness preference.
#
# Both builds used to write bin/Release/PfPresets.dll, which is the exact path the in-game dev
# plugin loads. So verifying the official-repo build - a thing you do right after changing
# something, without thinking about it - silently replaced the running plugin with one compiled
# without PFP_RATINGS: no tabs, no My Profile, no party panel, no settings tab. It looked exactly
# like the UI had broken, and it cost several rounds of hunting a rendering bug that did not exist.
# The two builds can no longer overwrite each other.
if [ "$ENABLE_RATINGS" = "true" ]; then
  OUT_DIR="$SCRIPT_DIR/bin/Release"
  OUT_ARG=""
else
  OUT_DIR="$SCRIPT_DIR/bin/ReleaseNoRatings"
  OUT_ARG=" -o '$OUT_DIR'"
fi

# APPDATA IS ONLY SHIMMED WHERE THE SHIM ACTUALLY LEADS TO DALAMUD.
#
# The csproj finds Dalamud through $(appdata)\XIVLauncher\addon\Hooks\dev. On Linux there is no
# APPDATA, so this points it at the xlcore symlink described in the header.
#
# Under git-bash on Windows there IS an APPDATA and it is already right. Overriding it made every
# Dalamud type in the project unresolvable - 546 "type or namespace not found" errors, which reads
# like the source is broken rather than the environment. package.sh calls this script, so it took
# release packaging down with it.
#
# TESTED ON Dalamud.dll, NOT ON THE DIRECTORY. A build that runs with the wrong APPDATA still
# creates $APPDATA/NuGet on its way to failing, so after one bad run the directory exists and a
# directory test says "shim is present" forever after - which is how the first version of this
# guard passed while changing nothing.
if [ -f "$HOME/.xlcore/msbuild-appdata/XIVLauncher/addon/Hooks/dev/Dalamud.dll" ]; then
  APPDATA_PREFIX="APPDATA=\$HOME/.xlcore/msbuild-appdata "
else
  APPDATA_PREFIX=""
fi

BUILD_CMD="cd '$SCRIPT_DIR' && ${APPDATA_PREFIX}dotnet build PfPresets.csproj -c Release -p:EnableWindowsTargeting=true -p:EnableRatings=$ENABLE_RATINGS --no-incremental$OUT_ARG"

# --no-incremental for the same reason as build.bat: Dalamud only reloads a dev
# plugin when the DLL's timestamp changes, so always force a fresh write.
if command -v dotnet >/dev/null 2>&1; then
  sh -c "$BUILD_CMD"
else
  distrobox enter mywine -- sh -c "$BUILD_CMD"
fi

echo "[OK] Build complete: $OUT_DIR/PfPresets.dll"
if [ "$ENABLE_RATINGS" = "true" ]; then
  echo "     Community ratings: COMPILED IN (third-party repo build)."
  echo "     This is the DLL the in-game dev plugin loads."
else
  echo "     Community ratings: omitted (official repo build)."
  echo "     NOT the dev plugin: that stays at bin/Release, built by 'build.sh' with no flags."
fi
echo "     Scan Dev Plugins in-game to reload."
