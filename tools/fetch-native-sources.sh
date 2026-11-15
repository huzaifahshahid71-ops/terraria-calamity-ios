#!/bin/bash
# Download only upstream source; the generated tree is ignored by Git.
set -euo pipefail
R="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$R/build/native-src"
clone() {
  local name=$1 url=$2 commit=$3
  if [ -e "$R/build/native-src/$name" ]; then
    echo "$name already exists; refusing to replace it" >&2; exit 1
  fi
  git clone "$url" "$R/build/native-src/$name"
  git -C "$R/build/native-src/$name" checkout --detach "$commit"
  git -C "$R/build/native-src/$name" submodule update --init --recursive
}
clone SDL2 https://github.com/libsdl-org/SDL.git 031912c4b6c5db80b443f04aa56fec3e4e645153
clone FNA3D https://github.com/FNA-XNA/FNA3D.git 95ee2be9933a1e3f6007b1483eb6533cec1eaf10
clone FAudio https://github.com/FNA-XNA/FAudio.git 2675093dcdaaa747487695a6bd28b13f211407b6
clone Theorafile https://github.com/FNA-XNA/Theorafile.git 70fef0f8f38727da61039a23c7003f481f583e1c
python3 "$R/tools/apply-native-patches.py"
