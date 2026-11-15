#!/usr/bin/env python3
"""Stage the owned game content + exact tML overrides for one private IPA.

Only creates derived files under build/game-assets. Source trees are read-only.
Staging copies bytes and never modifies original files.
"""
import hashlib, json, os, shutil
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
DEST=ROOT/'build/game-assets'
BASE=ROOT/'build/terraria-content/Content'
OVERLAY=ROOT/'build/tml-content/Content'
assert BASE.is_dir() and OVERLAY.is_dir()
manifest={}
def stage(source,destination):
    destination.parent.mkdir(parents=True,exist_ok=True)
    if destination.exists():
        if os.path.samefile(source,destination):return
        destination.unlink()
    shutil.copy2(source,destination)
for layer,tree in [('vanilla',BASE),('tModLoader',OVERLAY)]:
    for p in sorted(tree.rglob('*')):
        if not p.is_file():continue
        rel=p.relative_to(tree);stage(p,DEST/'Content'/rel)
        manifest['Content/'+rel.as_posix()]={'layer':layer,'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()}
for mod in json.loads((ROOT/'metadata/selected-mod-verification.json').read_text()):
    name=mod['name']+'.tmod'
    source=ROOT/'inputs/mod-packages'/name
    assert source.is_file(),source
    assert hashlib.sha256(source.read_bytes()).hexdigest()==mod['sha256'],name+' differs from verified input'
    stage(source,DEST/'Mods'/name)
    manifest['Mods/'+name]={'layer':'owned mod input','bytes':source.stat().st_size,'sha256':mod['sha256']}
(DEST/'Mods').mkdir(parents=True, exist_ok=True)
(DEST/'Mods/enabled.json').write_text(json.dumps(['CalamityModMusic','CalamityMod'])+'\n')
assert (DEST/'Content/Images/ChestStack_0.xnb').read_bytes()==(OVERLAY/'Images/ChestStack_0.xnb').read_bytes()
(ROOT/'metadata').mkdir(exist_ok=True)
(ROOT/'metadata/assets.json').write_text(json.dumps({'files':manifest,'file_count':len(manifest),'total_bytes':sum(x['bytes'] for x in manifest.values())},indent=2)+'\n')
print(f'Staged {len(manifest)} verified files, {sum(x["bytes"] for x in manifest.values())/1e6:.1f} MB logical size; originals untouched.')
