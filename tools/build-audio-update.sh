#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
SDK=$(xcrun --sdk iphoneos --show-sdk-path)
CC=$(xcrun --sdk iphoneos -f clang)
A="$PWD/build/native-src/FAudio"
OBJ="$PWD/build/native-build/obj/FAudio"
SOURCE="$A/src/FAudio_platform_sdl2.c"
OBJECT="$OBJ/FAudio_platform_sdl2_$(echo "$SOURCE" | md5 -q | cut -c1-6).o"
"$CC" -arch arm64 -isysroot "$SDK" -miphoneos-version-min=15.0 -O2 -fPIC -fvisibility=default -g0 -Wno-everything -I"$A/include" -I"$A/src" -I"$PWD/build/native-src/SDL2/include" -c "$SOURCE" -o "$OBJECT"
"$CC" -arch arm64 -isysroot "$SDK" -miphoneos-version-min=15.0 -dynamiclib -install_name @rpath/FAudio.framework/FAudio -o build/natives-ios/FAudio.framework/FAudio "$OBJ"/*.o -F build/natives-ios -framework SDL2 -Wl,-rpath,@executable_path/Frameworks -Wl,-rpath,@loader_path/.. -Wl,-no_fixup_chains
