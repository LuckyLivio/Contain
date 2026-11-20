"""Deterministic PR #22 condition, NOT historical EXEs and NOT runnable PE fixtures.

Create two differently labelled ZIPs with identical app.exe bytes. No execution.
Source: PR #22 / 5c7ad35, historical artifact 13943722617 unavailable (HTTP 403
in the recorded onboarding task). See docs/phase-one.md for original hash evidence.
"""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile


def generate(output):
    output.mkdir(parents=True, exist_ok=False)
    pe = bytearray(256)
    pe[:2] = b'MZ'
    pe[60:64] = (128).to_bytes(4, 'little')
    pe[128:134] = b'PE\0\0\x64\x86'
    pe[152:154] = b'\x0b\x02'
    for label in ('baseline', 'candidate'):
        path = output / (label + '.zip')
        with zipfile.ZipFile(path, 'w') as archive:
            for name, data in [('app.exe', pe), ('critical.dll', b'SYNTHETIC'),
                               ('label.txt', label.encode('ascii'))]:
                info = zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_STORED
                archive.writestr(info, data)
    spec = dict(schema_version=1, target='windows-x64',
                sha256=hashlib.sha256((output / 'candidate.zip').read_bytes()).hexdigest(),
                entry='app.exe', required_files=['critical.dll'],
                launch=dict(args=[], timeout_ms=1000, expected_exit=0, window_title=None),
                tests=[dict(name='synthetic self-test NOT EXECUTABLE', program='app.exe',
                            args=['--self-test'], expected_exit=0, timeout_ms=1000)])
    (output / 'candidate-spec.json').write_text(json.dumps(spec, indent=2) + '\n', encoding='utf-8')
    (output / 'provenance.json').write_text(json.dumps(dict(
        synthetic=True, native_execution=False, historical_exes=False,
        source='PR #22 / 5c7ad35 identical actual EXE condition; original artifacts not obtained',
        exe_sha256=hashlib.sha256(pe).hexdigest()), indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    generate(parser.parse_args().output)
