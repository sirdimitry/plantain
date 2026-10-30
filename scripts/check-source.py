#!/usr/bin/env python3
"""Check the publishable source tree; never print a matching secret value."""
import pathlib
import re
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
# FIX: скрипт падал с трейсбеком в копии без .git (экспорт архива, распакованный
# релиз). Теперь git используется, когда доступен, иначе — обход дерева с теми
# же исключениями, что заданы в .gitignore.
SKIP_DIRS = {'build', 'dist', '.build', '.git', '.swiftpm', '__pycache__'}
def list_files():
    try:
        listed = subprocess.check_output(
            ['git', 'ls-files', '-c', '-o', '--exclude-standard', '-z'],
            cwd=root, stderr=subprocess.DEVNULL)
        return {root / p.decode() for p in listed.split(b'\0') if p}
    except (subprocess.CalledProcessError, FileNotFoundError):
        files = set()
        for path in root.rglob('*'):
            relative = path.relative_to(root)
            if path.is_dir() or any(part in SKIP_DIRS for part in relative.parts):
                continue
            files.add(path)
        return files
paths = list_files()
# Assemble detectors so the scanner does not flag its own source literals.
patterns = [
    rb'gh' + rb'[pousr]_[A-Za-z0-9]{25,}',
    rb'github_' + rb'pat_[A-Za-z0-9_]{30,}',
    rb'-----BEGIN ' + rb'(?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    rb'/Users/' + rb'[^\s/]+/',
]
errors = []
for path in sorted(paths):
    if not path.is_file():
        continue
    relative = path.relative_to(root)
    if path.suffix.lower() in {'.har', '.p12', '.p8', '.keychain', '.dmg', '.zip'}:
        errors.append(f'{relative}: forbidden source artifact')
    data = path.read_bytes()
    if any(re.search(pattern, data) for pattern in patterns):
        errors.append(f'{relative}: potentially private marker')
if errors:
    print('\n'.join(errors), file=sys.stderr)
    sys.exit(1)
print(f'Source audit passed: {len(paths)} publishable files; no matching secret or home-path markers.')
