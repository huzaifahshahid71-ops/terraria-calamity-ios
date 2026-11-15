#!/usr/bin/env python3
"""Collect only Terraria guest diagnostics and recent LiveContainer crash reports."""
import argparse
import json
import pathlib
import re
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--version', default='v1')
parser.add_argument('--device', required=True, help='devicectl device identifier')
parser.add_argument('--app-id', required=True, help='installed container app bundle ID')
parser.add_argument('--guest-documents', required=True, help='guest Documents path relative to the container root')
args = parser.parse_args()
version = args.version
if not re.fullmatch(r'v[0-9]+', version):
    parser.error('--version must look like v20')
OUT = ROOT / 'metadata' / version
OUT.mkdir(parents=True, exist_ok=True)
DEVICE = args.device
BUNDLE = args.app_id
DOCS = args.guest_documents.rstrip('/')

def run(*args):
    return subprocess.run(['xcrun', 'devicectl', '--timeout', '40', *args], text=True)

for name in ['game-boot-status.txt', 'tml-client-console.txt', 'Terraria-Runtime-Crash.txt', 'tml-fatal.txt', 'sdl-trace.txt', 'tml-run/tModLoader-Logs/client.log']:
    run('device', 'copy', 'from', '--device', DEVICE, '--domain-type', 'appDataContainer', '--domain-identifier', BUNDLE, '--source', DOCS + '/' + name, '--destination', str(OUT / pathlib.Path(name).name))

# Full bundled clients use a separate writable cwd so old development Content cannot shadow assets.
run('device', 'copy', 'from', '--device', DEVICE, '--domain-type', 'appDataContainer', '--domain-identifier', BUNDLE, '--source', DOCS + '/client-run/tModLoader-Logs/client.log', '--destination', str(OUT / 'client-bundled.log'))

listing = OUT / 'crash-list.json'
result = run('device', 'info', 'files', '--device', DEVICE, '--domain-type', 'systemCrashLogs', '--search', 'LiveContainer', '--json-output', str(listing))
if result.returncode == 0 and listing.exists():
    candidates = set()
    def walk(value):
        if isinstance(value, dict):
            for v in value.values(): walk(v)
        elif isinstance(value, list):
            for v in value: walk(v)
        elif isinstance(value, str):
            if re.fullmatch(r'LiveContainer-[^/]+\.ips', value): candidates.add(value)
            elif '/' in value and re.fullmatch(r'LiveContainer-[^/]+\.ips', value.rsplit('/', 1)[-1]): candidates.add(value)
    walk(json.loads(listing.read_text()))
    by_name = {}
    for candidate in sorted(candidates, key=len):
        by_name[pathlib.PurePosixPath(candidate).name] = candidate
    for name in sorted(by_name, reverse=True)[:3]:
        run('device', 'copy', 'from', '--device', DEVICE, '--domain-type', 'systemCrashLogs', '--source', by_name[name], '--destination', str(OUT / name))
    if not by_name:
        print('No matching crash filenames found; crash-list.json is saved for inspection.')
print('Diagnostics saved to:', OUT)
