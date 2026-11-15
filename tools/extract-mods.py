#!/usr/bin/env python3
"""Verify user-supplied TMOD packages and extract managed references without executing them."""
import argparse, hashlib, io, json, shutil, struct, zlib
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
def string(stream):
    n = shift = 0
    for _ in range(5):
        byte = stream.read(1)
        if not byte: raise ValueError('Truncated TMOD string')
        b = byte[0]; n |= (b & 127) << shift
        if b < 128:
            data = stream.read(n)
            if len(data) != n: raise ValueError('Truncated TMOD string')
            return data.decode('utf-8')
        shift += 7
    raise ValueError('Invalid TMOD string length')
def integer(stream): return struct.unpack('<i', stream.read(4))[0]
def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('packages', nargs='+', type=Path)
    args = parser.parse_args(); reports = []
    (ROOT/'metadata').mkdir(exist_ok=True)
    (ROOT/'inputs/mod-packages').mkdir(parents=True, exist_ok=True)
    for package in args.packages:
        data = package.read_bytes(); stream = io.BytesIO(data)
        if stream.read(4) != b'TMOD': raise ValueError('Not a TMOD package')
        loader = string(stream); digest = stream.read(20); stream.read(256)
        length = integer(stream); start = stream.tell()
        if length != len(data)-start or hashlib.sha1(data[start:]).digest() != digest:
            raise ValueError('TMOD payload checksum/length mismatch')
        name, version = string(stream), string(stream)
        if name not in {'CalamityMod', 'CalamityModMusic'}: raise ValueError('Expected CalamityMod or CalamityModMusic')
        count = integer(stream)
        if not 0 <= count < 1000000: raise ValueError('Invalid TMOD file count')
        table = [(string(stream), integer(stream), integer(stream)) for _ in range(count)]
        out = ROOT/'inputs/mods'/name; out.mkdir(parents=True, exist_ok=True)
        for path, plain, stored in table:
            if not 0 <= stored <= plain: raise ValueError('Invalid TMOD entry length')
            blob = stream.read(stored)
            if len(blob) != stored: raise ValueError('Truncated TMOD entry')
            decoded = zlib.decompress(blob, -15) if stored != plain else blob
            if len(decoded) != plain: raise ValueError('TMOD decoded length mismatch')
            if path.lower().endswith('.dll') or path == 'Info':
                (out/Path(path.replace('\\','/')).name).write_bytes(decoded)
        if stream.tell() != len(data): raise ValueError('Unexpected trailing TMOD data')
        target = ROOT/'inputs/mod-packages'/f'{name}.tmod'
        if package.resolve() != target.resolve(): shutil.copy2(package, target)
        reports.append({'name':name, 'version':version, 'loader':loader, 'file_count':count, 'sha256':hashlib.sha256(data).hexdigest()})
        print(f'Verified {name} {version}: {count} entries')
    (ROOT/'metadata/selected-mod-verification.json').write_text(json.dumps(reports,indent=2)+'\n')
if __name__ == '__main__': main()
