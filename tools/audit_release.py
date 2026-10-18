"""Check the publication surface without printing potentially sensitive matches."""
import argparse
import os
import re
import zipfile
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('--source', type=Path, required=True)
p.add_argument('--archive', type=Path, required=True)
p.add_argument('--assembly', type=Path)
p.add_argument('--forbid', action='append', default=[])
args = p.parse_args()
skip = {'.git', 'bin', 'obj', 'artifacts', 'dist', 'release', '__pycache__', '.vs'}
private_files = re.compile(r'^(settings|status|games-catalog|hardware-probe|nvidia-probe)\.json$|^tuning-.*\.json$|\.log$|\.pdb$', re.I)
secret = re.compile(rb'gh[pousr]_[a-zA-Z0-9]{20,}|github_pat_[a-zA-Z0-9_]{30,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----')
user_path = re.compile(rb'[a-zA-Z]:[\\/]Users[\\/][a-zA-Z0-9_. -]+[\\/]')
failures = []
checked = 0

def inspect(name, data):
    global checked
    checked += 1
    if private_files.search(Path(name).name):
        failures.append((name, 'private runtime file / debug symbols'))
    if secret.search(data):
        failures.append((name, 'credential-like content'))
    if user_path.search(data):
        failures.append((name, 'absolute Windows user path'))
    for value in args.forbid:
        for variant in {value, value.replace('\\', '/')}:
            if variant.encode('utf-8').lower() in data.lower() or variant.encode('utf-16le').lower() in data.lower():
                failures.append((name, 'forbidden local identifier'))
                break

for root, dirs, files in os.walk(args.source):
    dirs[:] = [d for d in dirs if d not in skip]
    for file in files:
        path = Path(root) / file
        inspect(str(path.relative_to(args.source)), path.read_bytes())
with zipfile.ZipFile(args.archive) as archive:
    allowed = {'Luma.exe', 'KUR.cmd', 'LICENSE', 'README.md', 'DOTNET-LICENSE.txt', 'DOTNET-THIRD-PARTY-NOTICES.txt', 'WINDOWS-DESKTOP-LICENSE.txt', 'scripts/install.ps1', 'scripts/uninstall.ps1', 'scripts/setup.ps1'}
    for entry in archive.infolist():
        if entry.is_dir():
            continue
        name = entry.filename.replace('\\', '/')
        if name not in allowed:
            failures.append((name, 'unexpected release entry'))
        inspect('ZIP/' + name, archive.read(entry))
    if 'Luma.exe' not in archive.namelist():
        failures.append(('ZIP', 'missing executable'))
if args.assembly:
    inspect('compiled-app-assembly', args.assembly.read_bytes())
if failures:
    for name, category in sorted(set(failures)):
        print(f'FAIL: {name}: {category}')
    raise SystemExit(1)
print(f'PASS: {checked} source/package/assembly entries audited; no excluded data detected.')
