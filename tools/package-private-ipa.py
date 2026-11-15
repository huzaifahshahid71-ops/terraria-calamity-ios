#!/usr/bin/env python3
"""Package/verify the private full-game candidate, enforcing the tested audio framework."""
import argparse,hashlib,json,plistlib,zipfile
from pathlib import Path
R=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--version',default='v23');a=p.parse_args()
source=R/f'app/Phone/bin/{a.version}/Release/net10.0-ios/ios-arm64/TerrariaRuntimeProbe.ipa'
output=R/f'build/Terraria-Calamity-{a.version}-Unsigned.ipa'
base='Payload/TerrariaRuntimeProbe.app/'
fixed_audio=R/'build/natives-ios/FAudio.framework/FAudio'
key=base+'Frameworks/FAudio.framework/FAudio'
assert source.is_file() and fixed_audio.is_file()
with zipfile.ZipFile(source) as z,zipfile.ZipFile(output,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=6,allowZip64=True) as out:
    assert key in z.namelist()
    for info in z.infolist():
        out.writestr(info,fixed_audio.read_bytes() if info.filename==key else z.read(info))
manifest=json.loads((R/'metadata/assets.json').read_text())
with zipfile.ZipFile(output) as z:
    assert z.testzip() is None
    info=plistlib.loads(z.read(base+'Info.plist'))
    assert info['CFBundleDisplayName']=='Terraria Calamity'
    assert 'UIInterfaceOrientationPortrait' not in info['UISupportedInterfaceOrientations']
    for path,entry in manifest['files'].items():
        target=base+('BundledMods/'+path[5:] if path.startswith('Mods/') else path)
        assert hashlib.sha256(z.read(target)).hexdigest()==entry['sha256'],target
    assert json.loads(z.read(base+'BundledMods/enabled.json'))==['CalamityModMusic','CalamityMod']
    assert z.read(key)==fixed_audio.read_bytes()
    # v34: the shipped tModLoader is the woven one plus AOT-baked Calamity IL edits; build/tml-bundle is the source of truth.
    for dependency in ['tModLoader.dll','FNA.dll','ReLogic.dll','TerrariaHooks.dll','ManagedHooks.dll','MonoMod.Utils.dll']:
        expected=R/'build/tml-bundle'/dependency
        if expected.exists():assert z.read(base+dependency)==expected.read_bytes(),dependency+' is stale'
report={'ipa':str(output),'bytes':output.stat().st_size,'sha256':hashlib.file_digest(output.open('rb'),'sha256').hexdigest() if hasattr(hashlib,'file_digest') else hashlib.sha256(output.read_bytes()).hexdigest(),'version':info['CFBundleVersion'],'verified_asset_files':manifest['file_count'],'bundled_mods':['CalamityModMusic','CalamityMod'],'faudio_sha256':hashlib.sha256(fixed_audio.read_bytes()).hexdigest(),'zip_crc':'passed','phone_test':'pending'}
(R/f'metadata/{a.version}').mkdir(parents=True, exist_ok=True)
(R/f'metadata/{a.version}/package-verification.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))
