#!/bin/bash
# v34: bake Calamity's IL edits into tModLoader.dll so the phone AOT-compiles them (see tools/ManagedHooks/Baked.cs).
# Run after `HookWeaver weave-ios` whenever tModLoader, ManagedHooks or CalamityMod changes. Mac only; reads originals only.
set -euo pipefail
R="$(cd "$(dirname "$0")/.." && pwd)"; H="$R/build/headless"; OUT="$H/bake-out-$(date +%H%M%S)"
export DOTNET_ROOT="$R/toolchain/dotnet" PATH="$R/toolchain/dotnet:$PATH" DOTNET_CLI_HOME="$R/toolchain/home" NUGET_PACKAGES="$R/toolchain/nuget"
dotnet build "$R/tools/ManagedHooks" -c Release >/dev/null
cp "$R"/build/woven-ios/{tModLoader,TerrariaHooks,ReLogic,FNA}.dll "$R/tools/ManagedHooks/bin/Release/net8.0/ManagedHooks.dll" "$H/tml-bake/"
cp "$R/inputs/mods/CalamityMod/CalamityMod.dll" "$R/inputs/mods/CalamityModMusic/CalamityModMusic.dll" "$H/tml-bake/"
mkdir -p "$OUT" "$H/save-bake" "$H/tsave-bake"; [ -d "$H/save-bake/Mods" ] || cp -R "$H/save-woven/Mods" "$H/save-bake/"
( cd "$H/tml-bake" && timeout 600 /usr/bin/env TP_BAKE_OUT="$OUT/tModLoader.dll" TP_BAKE_SRC="$H/tml-bake/tModLoader.dll" TML_IOS_VANILLA_DIR="$R/build/terraria-content" \
    dotnet tModLoader.dll -server -tmlsavedirectory "$H/save-bake" -savedirectory "$H/tsave-bake" > "$OUT/server.out" 2>&1 < /dev/null & )
for i in $(seq 1 120); do [ -f "$OUT/tModLoader.dll.done" ] && break; sleep 5; done
pkill -9 -f "$H/save-bake" || true
[ -f "$OUT/tModLoader.dll.done" ] || { echo "bake did not finish"; exit 1; }
grep -c -v FAILED "$OUT/tModLoader.dll.report.txt" | sed 's/^/baked captures: /'; grep FAILED "$OUT/tModLoader.dll.report.txt" || true
echo "next: verify with tools/BakeVerify, then cp $OUT/tModLoader.dll build/tml-bundle/ (+ woven TerrariaHooks/ReLogic/FNA and ManagedHooks); MonoMod.Utils stays original"
