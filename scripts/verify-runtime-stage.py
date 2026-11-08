"""Extend the fixed transaction roles with actual current distributable UI checks."""
from pathlib import Path
import argparse
import difflib
import hashlib
import json
import os
import subprocess
import sys
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--stage-result', required=True)
args = parser.parse_args()
stage_path = Path(args.stage_result).resolve()
stage = json.loads(stage_path.read_text('utf-8-sig'))
assert stage['passed']
OUT = ROOT/'artifacts/verification/runtime-stage'/('run-' + uuid.uuid4().hex)
OUT.mkdir(parents=True)
runs = []
sha = lambda path: hashlib.sha256(Path(path).read_bytes()).hexdigest().upper()

def run(label, executable, dark=False):
    dest = OUT/label
    command = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $OutputEncoding=[Console]::OutputEncoding; & "
    command += "'" + str(ROOT/'scripts/smoke-runtime-ui.ps1') + "' -Executable '" + str(executable) + "' -OutputDirectory '" + str(dest) + "'"
    argv = ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command]
    result = subprocess.run(argv, cwd=ROOT, env=dict(os.environ, C30_UI_TEST_DARK='1' if dark else '0'), capture_output=True, timeout=240)
    record = dict(label=label, command=argv, cwd=str(ROOT), stdin='', stdout=result.stdout.decode('utf-8', errors='replace'), stderr=result.stderr.decode('utf-8', errors='replace'), exit_status=result.returncode)
    runs.append(record)
    (OUT/'process-runs.json').write_text(json.dumps(runs, ensure_ascii=False, indent=2), encoding='utf-8')
    print(label + ': exit=' + str(result.returncode), flush=True)
    print(record['stdout'], flush=True)
    if result.returncode:
        print(record['stderr'], flush=True)
        raise RuntimeError('Observed UI failure; literal output: ' + str(OUT/'process-runs.json'))
    evidence = json.loads((dest/'result.json').read_text('utf-8-sig'))
    assert evidence['passed'] and len(evidence['checks']) >= 31
    return evidence

release = Path(stage['release'])
package = stage['velopack_package']
payload = Path(package['publish'])
assert sha(stage['setup']) == stage['setup_sha256']
assert sha(stage['zip']) == stage['zip_sha256']
checks = [run('release-light', release/'C30SuperLogin.exe'), run('package-light', payload/'C30SuperLogin.exe'), run('package-dark', payload/'C30SuperLogin.exe', True)]

# Documentation/provenance must match the final source, not an earlier same-version build.
for directory in (release, payload):
    for name in ('README.md', 'THIRD_PARTY_NOTICES.md'):
        assert (directory/name).read_bytes() == (ROOT/name).read_bytes(), 'Stale document: ' + str(directory/name)
    for source_root, dest_name in ((ROOT/'docs', 'docs'), (ROOT/'references/licenses', 'licenses')):
        for source in source_root.rglob('*'):
            if source.is_file():
                assert (directory/dest_name/source.relative_to(source_root)).read_bytes() == source.read_bytes(), 'Stale asset: ' + str(source)

with zipfile.ZipFile(stage['zip']) as z:
    for path in release.rglob('*'):
        if path.is_file(): assert z.read(release.name + '/' + path.relative_to(release).as_posix()) == path.read_bytes()

icons = json.loads((ROOT/'src/C30SuperLogin.Desktop/Assets/PROJECT-ICONS.json').read_text('utf-8'))
for icon in icons:
    assert sha(ROOT/'src/C30SuperLogin.Desktop/Assets'/icon['path']) == icon['sha256']
    assert (ROOT/'references/licenses'/f"{icon['project']}-LICENSE.txt").stat().st_size > 1000

audit = ROOT/'audit-evidence'
before = ROOT/'artifacts/verification/oobe-v2/before'
diff = []
for original in before.rglob('*'):
    if original.is_file() and original.suffix in ('.cs', '.ps1', '.svg'):
        relative = original.relative_to(before)
        diff.extend(difflib.unified_diff(original.read_text('utf-8-sig').splitlines(True), (ROOT/relative).read_text('utf-8-sig').splitlines(True), fromfile='before/' + relative.as_posix(), tofile='after/' + relative.as_posix()))
patch = OUT/'runtime-oobe.patch'
patch.write_text(''.join(diff), encoding='utf-8')
original_report = json.loads((audit/'VERIFICATION.json').read_text('utf-8'))
assert sha(original_report['TARGET']) == original_report['original_sha256'] == original_report['restored_sha256']
assert sha(audit/'MODIFIED_FILE.js') == original_report['modified_sha256']
summary = dict(passed=True, gate='runtime-oobe-completion', controller_command=[sys.executable, '-B', '-X', 'utf8', str(Path(__file__).resolve()), '--stage-result', str(stage_path)], stage_result=str(stage_path), output=str(OUT), ui_checks=checks, process_runs=runs, source_patch=str(patch), source_hashes={str(p.relative_to(ROOT)): sha(p) for p in (ROOT/'src').rglob('*') if p.is_file() and 'obj' not in p.parts and 'bin' not in p.parts}, icons=icons, setup=stage['setup'], setup_sha256=sha(stage['setup']), zip=stage['zip'], zip_sha256=sha(stage['zip']), limitations=['Real C30 import/authentication and real UAC/integrity-boundary handshake remain unverified.', 'Resident means current Windows session; no startup task/service registered.', 'Official Sentry SDK/production telemetry and online update source remain pending.'])
(OUT/'result.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
original_report.setdefault('supplemental_gates', []).append(summary)
(audit/'VERIFICATION.json').write_text(json.dumps(original_report, ensure_ascii=False, indent=2), encoding='utf-8')
with (audit/'VERIFICATION.txt').open('a', encoding='utf-8') as ledger:
    ledger.write('\n=== REFINED OOBE / REQUIRED ELEVATION / UPSTREAM ICONS ===\n')
    ledger.write('RESULT: ' + str(OUT/'result.json') + '\nSOURCE_PATCH: ' + str(patch) + '\n')
    ledger.write('CHANGED: OnboardingWindow constructor/Render/Accounts/Preferences/ImportAccounts/SaveAccount; Settings.UseAdministrator (required, no opt-out); MainWindow.Preferences/RelatedLink/About; SVG-driven app icon.\n')
    for record in runs:
        ledger.write('\nCOMMAND: ' + json.dumps(record['command'], ensure_ascii=False) + '\nCWD: ' + record['cwd'] + '\nSTDIN:\n' + record['stdin'] + '\nSTDOUT (literal):\n' + record['stdout'] + '\nSTDERR (literal):\n' + record['stderr'] + '\nEXIT: ' + str(record['exit_status']) + '\n')
    ledger.write('RETAINED_FAILURES: runtime-oobe/ui-first (description absent from UIA, shell exit 1; added accessible descriptions); ui-accessible (owned guide nested below owner, shell exit 1; corrected UIA traversal); ui-owned-window (OS ShowWindow did not restore Avalonia visibility, Expand timed out, shell exit 1; now separate owned resident fixture, no OS-level visibility injection).\n')
    ledger.write('RETAINED_TOOL_ERRORS: missing Pillow module (prior turn, exit 1); initial dispatcher double-await compile failure fixed (prior turn); lean-ctx rejected shell by its own allowlist (exit 1), native shell used; initial multi-operation patch rejected before writes; wrong GitHub EasiAuto branch returned HTTP 404, corrected to master; raw.githubusercontent.com DNS failure (curl exit 6 both normal and escalated), GitHub API content decoding succeeded; initial PowerShell JSON read without UTF8 failed, corrected UTF8 parsing; quoted regex command had PowerShell parser error (exit 1), corrected via stdin script.\n')
    ledger.write('RETAINED_FILE_READ_ERRORS: CHANGELOG.md and Core/Store.cs absent (nonterminating path errors); corrected to docs/release-notes/0.1.0.md and Core/Storage.cs.\n')
    ledger.write('RETAINED_RUNTIME_GATE_ERROR: artifacts/verification/runtime-stage/run-6027bcee7d864801b8c61567f982d557/process-runs.json; shell exit 1, OOBE account CLI reload failed even though valid account JSON was printed. Harness relied on LASTEXITCODE for a GUI-subsystem executable; corrected to Start-Process -Wait -PassThru with explicit child ExitCode/stdout/stderr, not a weakened account assertion.\n')
    ledger.write('Three original behaviors retained and rerun by parent build verification: BASELINE FEATURE_ABSENT bridge_calls=0 exit=2; MODIFIED SUBMITTED bridge_calls=1 quoted_username=valid password=redacted authentication=unconfirmed exit=0; ROLLBACK FEATURE_ABSENT bridge_calls=0 exit=2; restored SHA256 equals original.\n')
    for text in summary['limitations']: ledger.write('LIMITATION: ' + text + '\n')
for name in ('MODIFIED_FILE.js', 'DIFF_FILE.patch', 'VERIFICATION.txt', 'ROLLBACK.sh'):
    path=audit/name
    assert path.read_bytes()
    print('REOPENED ' + str(path) + ' SHA256=' + sha(path), flush=True)
print('RUNTIME STAGE PASS ' + str(OUT/'result.json'), flush=True)
