"""Exercise the actual WinExe CLI with synthetic accounts and a nonexistent C30 root."""
from pathlib import Path
import json
import os
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT/'artifacts/verification/accounts-cli'
OUT.mkdir(parents=True, exist_ok=True)
DATA = OUT/('run-' + uuid.uuid4().hex)
DATA.mkdir()
EXE = ROOT/'src/C30SuperLogin.Desktop/bin/Release/net10.0-windows/C30SuperLogin.exe'
ENV = dict(os.environ)
for key in ('C30_POSTHOG_KEY', 'C30_POSTHOG_HOST', 'C30_SENTRY_DSN'): ENV.pop(key, None)
(DATA/'settings.json').write_text(json.dumps(dict(InstallRoot=str(DATA/'NO_C30_INSTALLATION'), AutoDetectInstall=False, LaunchC30=False, OnboardingVersion=1)), encoding='utf-8')
runs = []

def run(label, arguments, expected=0, payload=None):
    command = [str(EXE), *arguments, '--headless', '--data-dir', str(DATA)]
    text = '' if payload is None else json.dumps(payload)
    result = subprocess.run(command, input=text, capture_output=True, text=True, encoding='utf-8', env=ENV, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
    runs.append(dict(label=label, command=command, stdin=text, stdout=result.stdout, stderr=result.stderr, exit_status=result.returncode))
    (OUT/'process-runs.json').write_text(json.dumps(runs, indent=2), encoding='utf-8')
    assert result.returncode == expected, (label, result.returncode, result.stderr)
    print('PASS ' + label)

def read(name): return json.loads((DATA/name).read_text(encoding='utf-8-sig'))

profile = str(uuid.uuid4()); duplicate = str(uuid.uuid4())
fixture = dict(label='LOCAL Account Fixture', username='LOCAL_ACCOUNT_ONLY', password='LOCAL_TEST_PASSWORD')
run('add custom profile ID from stdin', ['accounts', 'add', '--id', profile], payload=fixture)
assert read('last-account-result.json')['profile_id'] == profile
run('list persisted account records', ['accounts', 'list', '--output', str(DATA/'list.json')])
assert len(read('list.json')) == 1 and read('list.json')[0]['Id'] == profile
assert read('list.json')[0]['Number'] == 1 and read('last-account-result.json')['id'] == 1
run('duplicate account keeps canonical ID', ['accounts', 'add', '--id', duplicate], payload=fixture)
assert read('last-account-result.json')['profile_id'] == profile
run('reloading still shows one account with no password', ['accounts', 'list', '--output', str(DATA/'list.json')])
assert len(read('list.json')) == 1 and fixture['password'] not in (DATA/'list.json').read_text()
run('conflicting profile ID is rejected', ['accounts', 'add', '--id', profile], expected=2, payload=dict(fixture, username='DIFFERENT_LOCAL_ACCOUNT'))
run('unknown ID rejected before any injection', ['login', '--id', str(uuid.uuid4())], expected=2)
assert read('last-operation.json')['Code'] == 'profile_missing'
run('known ID reaches discovery without exposing credentials', ['login', '--id', profile], expected=2)
assert read('last-operation.json')['Code'] == 'not_running'
run('short numeric ID reaches the same account without credentials in arguments', ['login', '--id', '1'], expected=2)
assert read('last-operation.json')['Code'] == 'not_running'
run('unknown numeric ID is rejected rather than selecting another account', ['login', '--id', '9999'], expected=2)
assert read('last-operation.json')['Code'] == 'profile_missing'
run('invalid numeric ID is rejected before target discovery', ['login', '--id', '-1'], expected=2)
assert read('last-operation.json')['Code'] == 'profile_missing'
run('adding without an ID automatically assigns the next short number', ['accounts', 'add'], payload=dict(fixture, username='LOCAL_SECOND_NUMBER_ACCOUNT'))
second = read('last-account-result.json')
assert second['id'] == 2
# Keep the one-account fixture expected by downstream UI tests; number 2 stays reserved.
settings = read('settings.json'); settings['Profiles'] = [p for p in settings['Profiles'] if p['Id'] != second['profile_id']]
(DATA/'settings.json').write_text(json.dumps(settings), encoding='utf-8')
(DATA/(second['profile_id'].replace('-', '') + '.credential')).unlink()
settings = read('settings.json'); alias = str(uuid.uuid4()); settings['ProfileAliases'] = {alias: profile}
(DATA/'settings.json').write_text(json.dumps(settings), encoding='utf-8')
run('old merged ID alias still resolves', ['login', '--id', alias], expected=2)
assert read('last-operation.json')['Code'] == 'not_running'
run('telemetry test with no project reports unconfigured', ['telemetry-test'], expected=2)
assert all(x['State'] == 'not_configured' for x in read('last-delivery.json'))
run('status preserves default telemetry ON', ['status', '--output', str(DATA/'status.json')])
assert read('status.json')['posthog_enabled'] and read('status.json')['sentry_enabled']
(OUT/'result.json').write_text(json.dumps(dict(passed=True, count=len(runs), data=str(DATA), production_uploads='none', target='nonexistent fixture root', process_runs=runs), indent=2), encoding='utf-8')
print(f'RESULT {len(runs)}/{len(runs)} PASS; real_authentication=not_attempted; secrets_on_command_line=none')
