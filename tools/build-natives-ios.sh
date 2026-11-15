#!/bin/bash
# Builds FNA 23.10's native dependencies for iOS arm64 as dynamic frameworks (no CMake needed).
# Inputs: build/native-src/{SDL2,FNA3D,FAudio,Theorafile} (pinned tags). Output: build/natives-ios/*.framework
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; SRC="$ROOT/build/native-src"; OUT="$ROOT/build/natives-ios"; OBJ="$ROOT/build/native-build/obj"
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
SDK=$(xcrun --sdk iphoneos --show-sdk-path); CC="$(xcrun --sdk iphoneos -f clang)"
MIN=15.0; ARCH=arm64
CFLAGS=(-arch $ARCH -isysroot "$SDK" -miphoneos-version-min=$MIN -O2 -fPIC -fvisibility=default -g0 -Wno-everything)
SDLFW="$ROOT/build/native-build/SDL2/Release-iphoneos/SDL2.framework"
mkdir -p "$OUT" "$OBJ"; rm -rf "$OUT/SDL2.framework"; cp -R "$SDLFW" "$OUT/"
SDLINC="$SRC/SDL2/include"

framework() { # name objdir extra-link...
  local name=$1 dir=$2; shift 2
  local fw="$OUT/$name.framework"; rm -rf "$fw"; mkdir -p "$fw"
  "$CC" "${CFLAGS[@]}" -dynamiclib -install_name "@rpath/$name.framework/$name" -o "$fw/$name" "$dir"/*.o \
     -F "$OUT" -framework SDL2 -Wl,-rpath,@executable_path/Frameworks -Wl,-rpath,@loader_path/.. "$@"
  cat > "$fw/Info.plist" <<PL
<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>CFBundleExecutable</key><string>$name</string><key>CFBundleIdentifier</key><string>org.fna.$name</string>
<key>CFBundleName</key><string>$name</string><key>CFBundlePackageType</key><string>FMWK</string><key>CFBundleShortVersionString</key><string>1.0</string>
<key>CFBundleVersion</key><string>1</string><key>MinimumOSVersion</key><string>$MIN</string><key>CFBundleSupportedPlatforms</key><array><string>iPhoneOS</string></array></dict></plist>
PL
  echo "built $fw ($(stat -f %z "$fw/$name") bytes)"
}
compile() { # objdir defines... -- sources...
  local dir=$1; shift; local defs=(); while [ "$1" != "--" ]; do defs+=("$1"); shift; done; shift
  rm -rf "$dir"; mkdir -p "$dir"
  for s in "$@"; do "$CC" "${CFLAGS[@]}" "${defs[@]}" -c "$s" -o "$dir/$(basename "${s%.*}")_$(echo "$s" | md5 -q | cut -c1-6).o"; done
}

# FNA3D 23.10 (defines from its CMakeLists.txt; non-Windows => no D3D11, no HLSL profile)
F="$SRC/FNA3D"
compile "$OBJ/FNA3D" -DFNA3D_DRIVER_VULKAN -DFNA3D_DRIVER_OPENGL -DMOJOSHADER_NO_VERSION_INCLUDE -DMOJOSHADER_USE_SDL_STDLIB -DMOJOSHADER_EFFECT_SUPPORT \
  -DMOJOSHADER_DEPTH_CLIPPING -DMOJOSHADER_FLIP_RENDERTARGET -DMOJOSHADER_XNA4_VERTEX_TEXTURES -DSUPPORT_PROFILE_ARB1=0 -DSUPPORT_PROFILE_ARB1_NV=0 \
  -DSUPPORT_PROFILE_BYTECODE=0 -DSUPPORT_PROFILE_D3D=0 -DSUPPORT_PROFILE_METAL=0 -DSUPPORT_PROFILE_HLSL=0 \
  -I"$F/src" -I"$F/include" -I"$F/MojoShader" -I"$F/Vulkan-Headers/include" -I"$SDLINC" -- \
  "$F"/src/FNA3D.c "$F"/src/FNA3D_Driver_D3D11.c "$F"/src/FNA3D_Driver_OpenGL.c "$F"/src/FNA3D_Driver_Vulkan.c "$F"/src/FNA3D_Image.c \
  "$F"/src/FNA3D_CommandBuffer.c "$F"/src/FNA3D_Memory.c "$F"/src/FNA3D_PipelineCache.c "$F"/src/FNA3D_Tracing.c \
  "$F"/MojoShader/mojoshader.c "$F"/MojoShader/mojoshader_effects.c "$F"/MojoShader/mojoshader_common.c "$F"/MojoShader/mojoshader_d3d11.c \
  "$F"/MojoShader/mojoshader_opengl.c "$F"/MojoShader/mojoshader_vulkan.c "$F"/MojoShader/profiles/mojoshader_profile_common.c \
  "$F"/MojoShader/profiles/mojoshader_profile_glsl.c "$F"/MojoShader/profiles/mojoshader_profile_hlsl.c "$F"/MojoShader/profiles/mojoshader_profile_spirv.c
framework FNA3D "$OBJ/FNA3D" -framework Foundation

# FAudio 22.09 (SDL2 platform)
A="$SRC/FAudio"
compile "$OBJ/FAudio" -I"$A/include" -I"$A/src" -I"$SDLINC" -- \
  "$A"/src/F3DAudio.c "$A"/src/FACT3D.c "$A"/src/FACT.c "$A"/src/FACT_internal.c "$A"/src/FAPOBase.c "$A"/src/FAPOFX.c "$A"/src/FAPOFX_echo.c \
  "$A"/src/FAPOFX_eq.c "$A"/src/FAPOFX_masteringlimiter.c "$A"/src/FAPOFX_reverb.c "$A"/src/FAudio.c "$A"/src/FAudioFX_reverb.c \
  "$A"/src/FAudioFX_volumemeter.c "$A"/src/FAudio_internal.c "$A"/src/FAudio_internal_simd.c "$A"/src/FAudio_operationset.c \
  "$A"/src/FAudio_platform_sdl2.c "$A"/src/XNA_Song.c
framework FAudio "$OBJ/FAudio" -Wl,-no_fixup_chains

# Theorafile (no SDL dependency; ARM NEON asm defines omitted: portable C paths)
T="$SRC/Theorafile"
TSRC=$(grep -oE '(theorafile|lib/[a-z]+/[A-Za-z0-9_]+)\.c' "$T/Makefile" | sort -u | sed "s|^|$T/|")
compile "$OBJ/theorafile" -I"$T" -I"$T/lib" -- $TSRC
framework libtheorafile "$OBJ/theorafile"
