"""Real x86 cross-process hook test against an owned hidden fixture, not C30."""
from pathlib import Path
import ctypes
import json
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
out = ROOT / 'artifacts' / 'verification'
out.mkdir(parents=True, exist_ok=True)
state, receipt = out/'fixture-state.json', out/'fixture-receipt.json'
for p in (state, receipt):
    if p.exists(): p.unlink()
fixture_dir = ROOT/'artifacts/native/fixture'
process = subprocess.Popen([str(fixture_dir/'C30Fixture.exe'), str(state), str(receipt)], cwd=fixture_dir, creationflags=subprocess.CREATE_NO_WINDOW)
records=[]
try:
    for _ in range(100):
        if state.exists() and state.stat().st_size: break
        if process.poll() is not None: raise RuntimeError(f'fixture exited {process.returncode}')
        time.sleep(.05)
    target=json.loads(state.read_text())
    script='window.C30SuperLogin = {fixture: true};'
    command=[str(ROOT/'artifacts/native/C30Native.exe'),'--pid',str(target['pid']),'--hwnd',target['hwnd']]
    result=subprocess.run(command,input=script,capture_output=True,text=True,encoding='utf-8',timeout=10)
    records.append(dict(command=command,stdin=script,stdout=result.stdout,stderr=result.stderr,exit_status=result.returncode))
    print(result.stdout,end='')
    native=json.loads(result.stdout)
    assert result.returncode==0 and native['Queued']>=1, native
    observed=json.loads(receipt.read_text())
    assert observed['agent_loaded'] and observed['correct_ui_thread'] and observed['script_marker'], observed
    print('PASS real cross-process x86 DLL hook, correct target UI thread, ExecJS call')
    print('NOTE fixture ExecJS records dispatch only; it does not emulate CEF or authenticate an account')
    (out/'native-test.json').write_text(json.dumps(dict(process_runs=records,observed=observed,passed=True),indent=2),encoding='utf-8')
finally:
    if state.exists():
        target=json.loads(state.read_text()); ctypes.windll.user32.PostMessageW(int(target['hwnd'],16),0x0010,0,0)
    try: process.wait(timeout=3)
    except subprocess.TimeoutExpired: process.terminate(); process.wait()
