#!/usr/bin/env python3
"""Repackage a verified private full game IPA with a matching native framework update."""
import zipfile,pathlib,plistlib,hashlib,json,re,argparse
r=pathlib.Path(__file__).resolve().parents[1];p=argparse.ArgumentParser();p.add_argument('--base',required=True);p.add_argument('--version',required=True);p.add_argument('--audio',action='store_true');p.add_argument('--fna3d',action='store_true',help='also replace FNA3D.framework/FNA3D');p.add_argument('--moltenvk',action='store_true',help='add Frameworks/MoltenVK.framework (v29+)');p.add_argument('--promotion',action='store_true',help='Info.plist CADisableMinimumFrameDurationOnPhone (120 Hz)');args=p.parse_args();assert re.fullmatch('v[0-9]+',args.version)
base=r/args.base;output=r/f'build/Terraria-Calamity-{args.version}-Unsigned.ipa';root='Payload/TerrariaRuntimeProbe.app/';native=root+'Frameworks/SDL2.framework/SDL2';info=root+'Info.plist';audio=root+'Frameworks/FAudio.framework/FAudio';fna3d=root+'Frameworks/FNA3D.framework/FNA3D';changed=[]
with zipfile.ZipFile(base) as z,zipfile.ZipFile(output,'w',allowZip64=True) as out:
 for item in z.infolist():
  data=z.read(item)
  if item.filename==native:data=(r/'build/natives-ios/SDL2.framework/SDL2').read_bytes();changed.append(item.filename)
  elif args.fna3d and item.filename==fna3d:data=(r/'build/natives-ios/FNA3D.framework/FNA3D').read_bytes();changed.append(item.filename)
  elif args.audio and item.filename==audio:data=(r/'build/natives-ios/FAudio.framework/FAudio').read_bytes();changed.append(item.filename)
  elif item.filename==info:
   d=plistlib.loads(data);d['CFBundleVersion']=args.version[1:];d['CFBundleShortVersionString']='0.'+args.version[1:]
   if args.promotion:d['CADisableMinimumFrameDurationOnPhone']=True
   data=plistlib.dumps(d);changed.append(item.filename)
  out.writestr(item,data)
 added=[]
 if args.moltenvk:
  src=r/'build/moltenvk-src/MoltenVK/MoltenVK/dynamic/MoltenVK.xcframework/ios-arm64/MoltenVK.framework'
  existing=set(z.namelist())
  for f in sorted(src.rglob('*')):
   if f.is_file():
    name=root+'Frameworks/MoltenVK.framework/'+f.relative_to(src).as_posix();assert name not in existing,name
    zi=zipfile.ZipInfo(name,date_time=(2026,11,14,0,0,0));zi.compress_type=zipfile.ZIP_DEFLATED;zi.external_attr=(0o100755 if f.name=='MoltenVK' else 0o100644)<<16
    out.writestr(zi,f.read_bytes());added.append(name)
assert sorted(changed)==sorted([native,info]+([audio] if args.audio else [])+([fna3d] if args.fna3d else []))
with zipfile.ZipFile(base) as old,zipfile.ZipFile(output) as new:
 assert new.testzip() is None
 assert new.namelist()==old.namelist()+added
 for name in old.namelist():
  if name not in changed:assert new.read(name)==old.read(name),name
with output.open('rb') as f:
 h=hashlib.sha256()
 for chunk in iter(lambda:f.read(1024*1024),b''):h.update(chunk)
report={'ipa':str(output),'bytes':output.stat().st_size,'sha256':h.hexdigest(),'base':str(base),'changed_entries':changed,'added_entries':added,'promotion_120hz':args.promotion,'all_other_entries_byte_identical':True,'zip_crc':'passed','phone_test':'pending'}
(r/f'metadata/{args.version}').mkdir(parents=True, exist_ok=True)
(r/f'metadata/{args.version}/package-verification.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))
