"""Finish the current UI/install-discovery gate; extend, never replace, the four audit roles."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import sys
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/verification/stage-completion' / ('run-' + uuid.uuid4().hex)
OUT.mkdir(parents=True)
RUNS = []

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest().upper()

def run(label, args, env=None):
    result = subprocess.run([str(x) for x in args], cwd=ROOT, capture_output=True, timeout=600, env=env)
    record = dict(label=label, command=[str(x) for x in args], cwd=str(ROOT), stdin='',
                  stdout=result.stdout.decode('utf-8', errors='replace'), stderr=result.stderr.decode('utf-8', errors='replace'),
                  exit_status=result.returncode)
    RUNS.append(record)
    (OUT/'process-runs.json').write_text(json.dumps(RUNS, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'{label}: exit={result.returncode}', flush=True)
    print(record['stdout'][-4000:], end='', flush=True)
    if result.returncode:
        print(record['stderr'], flush=True)
        raise RuntimeError(label + ' failed; literal output retained in ' + str(OUT/'process-runs.json'))

def ps(script, extra=(), env=None):
    # Force a known console encoding, including child command output.
    command = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $OutputEncoding=[Console]::OutputEncoding; & "
    command += "'" + str(ROOT/script).replace("'", "''") + "' "
    command += ' '.join(str(x) if str(x).startswith('-') and str(x)[1:].isalnum() else "'" + str(x).replace("'", "''") + "'" for x in extra)
    return ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command]

run('BUILD_VERIFY_PUBLISH', ps(Path('scripts/build.ps1'), ['-Verify', '-Python', sys.executable]))
release = ROOT/'artifacts/release/C30SuperLogin-0.1.0-win-x64'
exe = release/'C30SuperLogin.exe'
run('RUNTIME_OOBE_RELEASE_UI', ps(Path('scripts/smoke-runtime-ui.ps1'), ['-Executable', exe, '-OutputDirectory', OUT/'runtime-oobe-release']))
run('CONTROL_FIXTURE_BUILD', ['dotnet', 'build', 'tests/C30SuperLogin.ControlFixture/C30SuperLogin.ControlFixture.csproj', '-c', 'Release', '--nologo'])
run('CONTROL_LOGIN_FIVE_CASES', ps(Path('scripts/test-control-login.ps1')))
run('PUBLIC_CI_SYNTHETIC_JS', ['node', 'tests/test_injection.cjs', '--synthetic'])
for dark in (False, True):
    label = 'release-dark' if dark else 'release-light'
    environment = dict(os.environ, C30_UI_TEST_DARK='1' if dark else '0')
    # Switch arguments must remain PowerShell tokens, rather than quoted positional strings.
    command = ps(Path('scripts/smoke-ui.ps1'), ['-AccountChecks', '-Executable', exe, '-OutputDirectory', OUT/label])
    command[-1] = command[-1].replace("'-AccountChecks'", '-AccountChecks').replace("'-Executable'", '-Executable').replace("'-OutputDirectory'", '-OutputDirectory')
    run(label.upper(), command, environment)
    result = json.loads((OUT/label/'result.json').read_text(encoding='utf-8-sig'))
    assert result['passed'] and result['exit_status'] == 0

run('VELOPACK_SELF_CONTAINED_PACKAGE', ps(Path('scripts/package-velopack.ps1')))
package_root = ROOT/'artifacts/velopack/releases'
package = json.loads((package_root/'package-result.json').read_text(encoding='utf-8-sig'))
payload = Path(package['publish'])/'C30SuperLogin.exe'
run('RUNTIME_OOBE_PACKAGE_UI', ps(Path('scripts/smoke-runtime-ui.ps1'), ['-Executable', payload, '-OutputDirectory', OUT/'runtime-oobe-package']))
run('VELOPACK_OFFICIAL_LOCAL_FEED_TESTS', ['dotnet', 'run', '--project', 'tests/C30SuperLogin.Desktop.Tests/C30SuperLogin.Desktop.Tests.csproj', '-c', 'Release', '--no-launch-profile', '--', '--local-feed', package_root])
run('PRE_LOGIN_WARNING_FOUR_CASES', ps(Path('scripts/smoke-warning.ps1'), ['-Executable', payload, '-OutputDirectory', OUT/'warnings']))
run('SELF_CONTAINED_PAYLOAD_UI', ps(Path('scripts/smoke-ui.ps1'), ['-AccountChecks', '-Executable', payload, '-OutputDirectory', OUT/'installer-payload-ui']))
feed = json.loads((package_root/'releases.win.json').read_text(encoding='utf-8-sig'))
for asset in feed['Assets']:
    assert sha(package_root/asset['FileName']) == asset['SHA256']
    with zipfile.ZipFile(package_root/asset['FileName']) as z:
        names = [entry.filename.replace('\\', '/') for entry in z.infolist()]
        assert any(name.endswith('/C30SuperLogin.exe') for name in names)
        assert any(name.endswith('/C30Native.exe') for name in names)
        assert any(name.endswith('/C30Agent.dll') for name in names)
        assert not any(Path(name).name.lower() in ('settings.json', 'duilib.dll', 'teach.exe', 'signin.js') or name.lower().endswith('.credential') for name in names)
        for file in Path(package['publish']).rglob('*'):
            if file.is_file():
                relative = file.relative_to(package['publish']).as_posix()
                matches = [name for name in names if name.endswith('/' + relative)]
                # Official vpk pack omits symbol files and the runtime crash-dump tool.
                if not matches and relative in ('C30SuperLogin.Core.pdb', 'C30SuperLogin.pdb', 'createdump.exe'):
                    continue
                assert len(matches) == 1 and z.read(matches[0]) == file.read_bytes(), 'NuGet package differs from current publish: ' + relative
for image in (ROOT/'docs/images').glob('*.png'):
    assert image.read_bytes().startswith(b'\x89PNG\r\n\x1a\n')

# Read real installed metadata without launching C30, injecting, logging in or sending telemetry.
data = OUT/'installation-status-data'
data.mkdir()
(data/'settings.json').write_text(json.dumps(dict(InstallRoot=r'D:\Program Files (x86)\Datedu\teach', AutoDetectInstall=True, LaunchC30=False)), encoding='utf-8')
status_path = OUT/'installation-status.json'
run('REAL_INSTALLATION_STATUS_READ_ONLY', [exe, 'status', '--headless', '--output', status_path, '--data-dir', data])
status = json.loads(status_path.read_text(encoding='utf-8-sig'))
assert status['posthog_enabled'] and status['sentry_enabled'] and status['auto_detect_install']
assert Path(status['install_root']).is_dir() and Path(status['latest_installed_directory'], 'teach.exe').is_file()

archive = Path(str(release)+'.zip')
with zipfile.ZipFile(archive) as z:
    entries = {x.filename.replace('\\', '/'): x for x in z.infolist() if not x.is_dir()}
    for file in release.rglob('*'):
        if file.is_file():
            name = release.name + '/' + file.relative_to(release).as_posix()
            assert z.read(entries[name]) == file.read_bytes(), 'ZIP differs from published file: ' + name
    for name in entries:
        assert not name.lower().endswith('.credential') and Path(name).name.lower() not in ('settings.json', 'duilib.dll', 'teach.exe', 'signin.js')
    count = len(entries)
for line in (release/'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
    digest, name = line.split('  ', 1)
    assert sha(release/name) == digest

summary = dict(passed=True, gate='product-completion', controller_command=[sys.executable, '-B', '-X', 'utf8', str(ROOT/'scripts/complete-stage.py')], output=str(OUT), release=str(release), zip=str(archive), zip_sha256=sha(archive),
               setup=package['setup'], setup_sha256=sha(package['setup']), velopack_package=package,
               zip_files=count, real_installation_status=status, process_runs=RUNS,
               limitations=['Real C30 authentication remains unconfirmed; no real credentials used.',
                            'Latest means locally installed; no official online update endpoint has been verified.',
                            'Production telemetry and official Sentry SDK integration remain pending.',
                            'Only C30 is in scope; Seewo Whiteboard support was withdrawn.',
                            'Real UAC/medium-to-high integrity handshake not yet verified; no startup registration.'])
(OUT/'result.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')

# Preserve all pre-existing BASELINE/MODIFIED/ROLLBACK records and append this gate.
audit = ROOT/'audit-evidence'
report_path = audit/'VERIFICATION.json'
report = json.loads(report_path.read_text(encoding='utf-8'))
report.setdefault('supplemental_gates', []).append(summary)
report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
with (audit/'VERIFICATION.txt').open('a', encoding='utf-8') as f:
    f.write('\n=== SUPPLEMENTAL UI / INSTALLATION / RELEASE GATE ===\n')
    f.write('CHANGED: AccountProfile.Number, Settings.NextProfileNumber/ProfileNumberAliases, AccountBook.EnsureNumbers/Resolve, ClassIsland.Export, MainWindow.Profiles; short numeric automation IDs start at 1; existing GUID calls remain compatible. Also Installation.Discover/Inspect, Coordinator.RunAsync, ControlLogin.ExecuteAsync username read-back; both telemetry defaults remain true.\n')
    f.write('SUPPLEMENTAL_JSON: ' + str(OUT/'result.json') + '\n')
    f.write('ZIP: ' + str(archive) + '\nZIP_SHA256: ' + sha(archive) + '\n')
    f.write('SETUP: ' + package['setup'] + '\nSETUP_SHA256: ' + sha(package['setup']) + '\n')
    for record in RUNS:
        f.write('\nCOMMAND: ' + json.dumps(record['command'], ensure_ascii=False) + '\nCWD: ' + record['cwd'])
        f.write('\nSTDIN:\n' + record['stdin'] + '\nSTDOUT (literal):\n' + record['stdout'] + '\nSTDERR (literal):\n' + record['stderr'] + '\nEXIT: ' + str(record['exit_status']) + '\n')
    f.write('\nREAL_INSTALLATION_STATUS (local only):\n' + status_path.read_text(encoding='utf-8-sig') + '\n')
    f.write('\n'.join('LIMITATION: ' + item for item in summary['limitations']) + '\n')
    f.write('RETAINED_ERROR: artifacts/installation-tests.log; first manual-root isolation assertion failed, process exit -532462766. Normalize now preserves nonexistent explicit paths; corrected run exit 0 in artifacts/installation-tests-corrected.log.\n')
    f.write('RETAINED_TOOL_ERRORS: apply_patch could not match BOM-prefixed smoke-ui.ps1 first line; source=apply_patch, no command exit status. A following Python text match raised AssertionError; later UTF-8-safe targeted edit succeeded. Missing file reads used MainWindow.Profiles.cs / Accounts.cs; corrected to MainWindow.cs / AccountBook.cs.\n')
    f.write('RETAINED_WARNING_ERROR: artifacts/warning-smoke.log; concurrency assertion failed, shell exit 1. Shutdown was requested too early during framework initialization; posting Shutdown to the dispatcher fixed it. Corrected four-case run exit 0 in artifacts/warning-smoke-corrected.log.\n')
    f.write('RETAINED_CONTROL_ERROR: artifacts/verification/stage-completion/run-1dc08dcded75412cbf14d1b28a7066be/process-runs.json; CONTROL_LOGIN_FOUR_CASES exit 1, Control values or submission count mismatch. Fixture recorded submitted=1, synthetic_username_ok=false, synthetic_password_ok=true. Original four cases passed on the next observed run; root cause remains unconfirmed. ControlLogin.ExecuteAsync now verifies only the non-secret username before Invoke; changed-user fixture verifies no submission. Owned fixture now uses ShowWithoutActivation/WS_EX_NOACTIVATE and publishes readiness after Shown. Pre-change hashes: artifacts/verification/control-login/continuation-before.json.\n')
    f.write('CONTROL_REPEATS: artifacts/verification/control-login/continuation-repeats.json; three consecutive five-case runs exited 0; real authentication not attempted.\n')
    f.write('RETAINED_PACKAGE_ERROR: artifacts/verification/stage-completion/run-c5d8ea7ea53d45e883dfef861209aeb9/process-runs.json; VELOPACK_SELF_CONTAINED_PACKAGE exit 1; official vpk reported System.IO.IOException: disk space insufficient at C:/Users/ws675/AppData/Local/Temp/velopack/temp.1/C30SuperLogin-0.1.0-full.nupkg. Packaging now gives the child process a unique disposable TEMP/TMP under the workspace volume; no system files were removed.\n')
    f.write('RETAINED_READ_ERRORS: Get-Content src/C30SuperLogin.Desktop/Cli.cs produced nonterminating PathNotFound (combined command exit 0); Get-Content src/C30SuperLogin.Core/Commands.cs exit 1. Corrected by listing actual source files and reading src/C30SuperLogin.Desktop/Commands.cs, exit 0.\n')
    f.write('NUMERIC_ID_EVIDENCE: artifacts/verification/numeric-id/before.json (pre-change source hashes), baseline.json (numeric login profile_missing, exit 2), core-tests.json (ten new short-ID checks). Actual CLI, UI and ClassIsland export checks are included in this gate. Numeric IDs are local aliases, not authentication tokens.\n')
    f.write('RUNTIME_OOBE_CHANGED: Settings.UseAdministrator is required (legacy false ignored), RuntimeAccess.RouteAsync bounded elevated operations, Program admin-host routing, MainWindow runtime mode/About/RelatedLink, five-step OnboardingWindow including account auto-save/import, preferences and ClassIsland binding/export; SVG-driven app icon and upstream project icons. UI gates use nonexistent C30 roots and do not prove real UAC or authentication.\n')
    for phase in ('baseline', 'modified'):
        evidence = ROOT/'artifacts/verification/numeric-id'/f'{phase}.json'
        if evidence.exists():
            f.write('\nNUMERIC_ID_' + phase.upper() + ' (literal command records):\n' + evidence.read_text(encoding='utf-8') + '\n')
    for source in list((ROOT/'src').rglob('*.cs')) + list((ROOT/'tests').rglob('*.cs')) + list((ROOT/'scripts').glob('*.ps1')) + list((ROOT/'scripts').glob('*.py')):
        if 'obj' not in source.parts and 'bin' not in source.parts: f.write('SOURCE_SHA256 ' + sha(source) + ' ' + str(source) + '\n')

target = Path(report['TARGET'])
assert sha(target) == report['original_sha256'] == report['restored_sha256']
assert sha(audit/'MODIFIED_FILE.js') == report['modified_sha256']
for name in ('MODIFIED_FILE.js', 'DIFF_FILE.patch', 'VERIFICATION.txt', 'ROLLBACK.sh'):
    path = audit/name
    assert path.read_bytes()
    print('REOPENED ' + str(path) + ' sha256=' + sha(path), flush=True)
print('STAGE PASS zip=' + str(archive) + ' sha256=' + sha(archive), flush=True)
print('STAGE RESULT ' + str(OUT/'result.json'), flush=True)
