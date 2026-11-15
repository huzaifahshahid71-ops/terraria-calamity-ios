#!/usr/bin/env python3
"""Apply this kit's patches to clean, pinned native source checkouts."""
import shutil, subprocess
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def main():
    for library, patch in [('SDL2','SDL2-2.28.2-terraria-ios.patch'),('FNA3D','FNA3D-23.10-ios.patch'),('FAudio','FAudio-22.09-ios.patch')]:
        directory=ROOT/'build/native-src'/library; source=ROOT/'patches'/patch
        subprocess.run(['git','-C',str(directory),'apply','--check',str(source)],check=True)
        subprocess.run(['git','-C',str(directory),'apply',str(source)],check=True)
    for name in ['SDL_terraria_overlay.h','SDL_terraria_icons.h']:
        shutil.copy2(ROOT/'patches/SDL2-terraria-overlay'/name,ROOT/'build/native-src/SDL2/src/video/uikit'/name)
    shutil.copy2(ROOT/'patches/FAudio_platform_sdl2_ios.h',ROOT/'build/native-src/FAudio/src/FAudio_platform_sdl2_ios.h')
    print('Native patches applied. Icon header is an artwork-free placeholder.')
if __name__=='__main__':main()
