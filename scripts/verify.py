"""Reproducible local checks and byte-exact, non-installing source transaction."""
from pathlib import Path
import base64
import hashlib
import json
import os
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
AUDIT = ROOT / 'audit-evidence'
AUDIT.mkdir(exist_ok=True)
RUNS = []
NODE = shutil.which('node') or r'D:\Program Files\nodejs\node.exe'

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest().upper()

def run(label, command, expected=0, cwd=ROOT, stdin=None):
    result = subprocess.run([str(x) for x in command], cwd=cwd, input=stdin,
                            capture_output=True, timeout=180)
    record = dict(label=label, command=[str(x) for x in command], cwd=str(cwd),
                  stdin='' if stdin is None else stdin.decode('utf-8'),
                  stdout=result.stdout.decode('utf-8', errors='replace'),
                  stderr=result.stderr.decode('utf-8', errors='replace'), exit_status=result.returncode)
    RUNS.append(record)
    (AUDIT / 'process-runs.json').write_text(json.dumps(RUNS, ensure_ascii=False, indent=2), encoding='utf-8')
    print(label + ': exit=' + str(result.returncode))
    print(record['stdout'], end='')
    if result.returncode != expected:
        print(record['stderr'])
        raise RuntimeError(label + ' unexpected exit')
    return result

manifest = json.loads((ROOT/'analysis/source_manifest.json').read_text(encoding='utf-8-sig'))
entry = next(x for x in manifest if x['relative_path'].replace('\\', '/') == 'public_web/signin/signin.js')
target = Path(entry['original'])
assert sha(target) == entry['sha256'], 'Original target changed since inspection'
pristine = target.read_bytes()
baseline = AUDIT/'pristine-signin.js'
baseline.write_bytes(pristine)
input_path = ROOT/'tests/fixture-input.json'
probe = ROOT/'tests/probe_source.cjs'
run('BASELINE', [NODE, probe, target, input_path], expected=2)
extension = (ROOT/'src/C30SuperLogin.Core/Injection/login.js').read_bytes()
changed = pristine + b'\n// C30 Super Login runtime extension; installed source is not patched.\n' + extension
modified = AUDIT/'MODIFIED_FILE.js'
modified.write_bytes(changed)
run('MODIFIED', [NODE, probe, modified, input_path])

# Produce a native Git patch, retaining CR bytes in the original source.
before, after = AUDIT/'patch-before', AUDIT/'patch-after'
before.mkdir(exist_ok=True); after.mkdir(exist_ok=True)
(before/'MODIFIED_FILE.js').write_bytes(pristine)
(after/'MODIFIED_FILE.js').write_bytes(changed)
diff = run('CREATE_DIFF', ['git', '-c', 'core.autocrlf=false', 'diff', '--no-index', '--binary', '--no-ext-diff',
                         '--src-prefix=a/', '--dst-prefix=b/', 'patch-before/MODIFIED_FILE.js',
                         'patch-after/MODIFIED_FILE.js'], expected=1, cwd=AUDIT).stdout
diff = diff.replace(b'a/patch-before/MODIFIED_FILE.js', b'a/MODIFIED_FILE.js').replace(b'b/patch-after/MODIFIED_FILE.js', b'b/MODIFIED_FILE.js')
patch = AUDIT/'DIFF_FILE.patch'
patch.write_bytes(diff)
reconstruct = AUDIT/'patch-reconstructed'
reconstruct.mkdir(exist_ok=True)
(reconstruct/'MODIFIED_FILE.js').write_bytes(pristine)
run('APPLY_DIFF', ['git', '-c', 'core.autocrlf=false', 'apply', '--whitespace=nowarn', str(patch)], cwd=reconstruct)
assert sha(reconstruct/'MODIFIED_FILE.js') == sha(modified), 'Patch reconstruction mismatch'

encoded = base64.b64encode(pristine).decode('ascii')
rollback = AUDIT/'ROLLBACK.sh'
rollback.write_text('#!/bin/sh\nset -eu\n[ "$#" -eq 1 ] || { echo "usage: ROLLBACK.sh TARGET_COPY" >&2; exit 64; }\n'
    + 'command -v base64 >/dev/null 2>&1 || { echo "base64 required" >&2; exit 69; }\n'
    + 'dest=$1\ntmp="${dest}.rollback.$$"\ntrap \'rm -f -- "$tmp"\' EXIT HUP INT TERM\n'
    + "base64 -d >\"$tmp\" <<'C30_PRISTINE_BASE64'\n"
    + '\n'.join(encoded[i:i+76] for i in range(0, len(encoded), 76))
    + '\nC30_PRISTINE_BASE64\n'
    + 'mv -f -- "$tmp" "$dest"\ntrap - EXIT HUP INT TERM\nprintf "%s\\n" "RESTORED pristine-signin.js"\n', encoding='utf-8', newline='\n')
os.chmod(rollback, 0o755)
restored = AUDIT/'rollback-copy.js'
restored.write_bytes(changed)
bash = Path(r'C:\Program Files\Git\bin\bash.exe')
run('EXECUTE_ROLLBACK', [bash, rollback, restored])
run('ROLLBACK', [NODE, probe, restored, input_path], expected=2)
assert sha(restored) == sha(target) == entry['sha256'], 'Restored hash mismatch'
modified.write_bytes(changed)  # GOAL remains applied to the designated modified copy.

run('APP_BUILD', ['dotnet', 'build', 'src/C30SuperLogin.Desktop/C30SuperLogin.Desktop.csproj', '-c', 'Release', '--nologo'])
run('CORE_TESTS', ['dotnet', 'run', '--project', 'tests/C30SuperLogin.Tests/C30SuperLogin.Tests.csproj', '-c', 'Release', '--no-launch-profile'])
run('JS_TESTS', [NODE, 'tests/test_injection.cjs'])
run('NATIVE_FIXTURE_TEST', [sys.executable, '-B', '-X', 'utf8', 'scripts/test_native.py'])
run('ACCOUNT_CLI_TESTS', [sys.executable, '-B', '-X', 'utf8', 'scripts/test_accounts.py'])
run('ORIGINAL_CONTRACT_TESTS', [NODE, 'analysis/probe_login_contract.cjs'])
integrity = []
for item in manifest:
    original, snapshot = sha(item['original']), sha(item['snapshot'])
    integrity.append(dict(path=item['original'], original=original, snapshot=snapshot,
                          expected=item['sha256'], unchanged=original == snapshot == item['sha256']))
assert all(x['unchanged'] for x in integrity)

report = dict(TARGET=str(target), changed_symbol='window.C30SuperLogin.run(options): login, list_saved, login_saved',
    goal='Runtime-injected probe/login using the existing mirco.login bridge; no installed-file modification',
    original_sha256=sha(target), modified_sha256=sha(modified), restored_sha256=sha(restored),
    diff_sha256=sha(patch), rollback_sha256=sha(rollback),
    process_runs=RUNS, integrity=integrity,
    limitations=['BASELINE/MODIFIED/ROLLBACK are offline DOM/CEF-contract observations, not server authentication.',
                 'Native fixture is an owned x86 test process, not the installed C30.',
                 'Production PostHog/Sentry upload remains untested until project configuration is supplied.',
                 'No trial/activation checks are modified.'])
previous_path = AUDIT/'VERIFICATION.json'
if previous_path.exists():
    previous = json.loads(previous_path.read_text(encoding='utf-8'))
    assert previous['TARGET'] == str(target) and previous['original_sha256'] == sha(target), 'Transaction target identity changed'
    report['supplemental_gates'] = previous.get('supplemental_gates', [])
    report['previous_process_runs'] = previous.get('previous_process_runs', []) + [previous['process_runs']]
(AUDIT/'VERIFICATION.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
text = 'TARGET: ' + str(target) + '\nChanged symbol: window.C30SuperLogin.run(options)\n'
for field in ('original_sha256', 'modified_sha256', 'restored_sha256', 'diff_sha256', 'rollback_sha256'):
    text += field + ': ' + report[field] + '\n'
text += '\nInput file: ' + str(input_path) + '\nInput bytes:\n' + input_path.read_text(encoding='utf-8') + '\n'
for item in RUNS:
    text += '\n=== ' + item['label'] + ' ===\nCOMMAND: ' + json.dumps(item['command'], ensure_ascii=False) + '\nCWD: ' + item['cwd']
    text += '\nSTDIN:\n' + item['stdin'] + '\nSTDOUT (literal):\n' + item['stdout'] + '\nSTDERR (literal):\n' + item['stderr'] + '\nEXIT: ' + str(item['exit_status']) + '\n'
text += '\nINTEGRITY: ' + str(len(integrity)) + '/' + str(len(integrity)) + ' original and snapshot hashes unchanged\n'
text += '\n'.join('LIMITATION: ' + x for x in report['limitations']) + '\n'
with (AUDIT/'VERIFICATION.txt').open('a', encoding='utf-8') as ledger:
    ledger.write('\n=== RECHECK OF THE SAME FOUR TRANSACTION ROLES ===\n' + text)
for role in (modified, patch, AUDIT/'VERIFICATION.txt', rollback):
    assert role.read_bytes()
    print('REOPENED ' + str(role.resolve()) + ' sha256=' + sha(role))
print('TRANSACTION PASS: installed original unchanged; diff reconstructs copy; rollback hash matches; extension remains applied')
