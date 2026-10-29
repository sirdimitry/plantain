#!/usr/bin/env python3
"""Check the publishable source tree; never print a matching secret value."""
import pathlib
import re
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
listed = subprocess.check_output(['git', 'ls-files', '-c', '-o', '--exclude-standard', '-z'], cwd=root)
paths = {root / p.decode() for p in listed.split(b'\0') if p}
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
