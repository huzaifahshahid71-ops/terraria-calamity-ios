#!/usr/bin/env python3
"""Generate control-icon C data from the builder's own Terraria XNB images. Output stays in build/."""
import argparse, io, struct, subprocess, tempfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
NAMES=['Item_188','Item_189','Item_292','Item_84','Item_8','Item_190','Item_2430','Item_3507','Inventory_Back','Inventory_Back14']
def main():
    from PIL import Image
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--content',required=True,type=Path)
    p.add_argument('--output',type=Path,default=ROOT/'build/native-src/SDL2/src/video/uikit/SDL_terraria_icons.h')
    args=p.parse_args()
    if ROOT/'build' not in args.output.resolve().parents: p.error('Output must stay in the ignored build directory')
    exporter=ROOT/'tools/XnbExport/bin/Release/net8.0/XnbExport.dll'
    if not exporter.exists(): p.error('Build tools/XnbExport first')
    lines=['/* Generated locally from user-owned content. Do not commit. */','#include <stddef.h>','typedef struct { const char *name; const unsigned char *png; size_t len; } TPIconData;']
    entries=[]
    with tempfile.TemporaryDirectory() as temporary:
        for name in NAMES:
            raw=Path(temporary)/(name+'.raw')
            subprocess.run(['dotnet',str(exporter),'export',str(args.content/'Images'/f'{name}.xnb'),str(raw)],check=True)
            data=raw.read_bytes();w,h=struct.unpack('<ii',data[:8])
            image=Image.frombytes('RGBA',(w,h),data[8:]);buf=io.BytesIO();image.save(buf,format='PNG')
            png=buf.getvalue();symbol='tp_icon_'+name
            lines.append('static const unsigned char '+symbol+'[] = {'+','.join(map(str,png))+'};')
            entries.append('{"'+name+'",'+symbol+','+str(len(png))+'}')
    lines.append('static const TPIconData TPIcons[] = {'+','.join(entries)+'};')
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text('\n'.join(lines)+'\n')
    print('Generated',args.output)
if __name__=='__main__':main()
