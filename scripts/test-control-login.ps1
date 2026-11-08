$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts\verification\control-login'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$fixture = Join-Path $root 'tests\C30SuperLogin.ControlFixture\bin\Release\net10.0-windows\C30SuperLogin.ControlFixture.exe'
$runner = Join-Path $root 'tests\C30SuperLogin.Tests\bin\Release\net10.0-windows\C30SuperLogin.Tests.exe'
$records = @()
foreach ($case in @('normal','duplicate','wrong-pid','unprotected','changed-user')) {
    $file = Join-Path $out ($case + '-' + [guid]::NewGuid().ToString('N') + '.json')
    $p = Start-Process -FilePath $fixture -ArgumentList @($file,$case) -WindowStyle Hidden -PassThru
    try {
        for ($i=0; $i -lt 100; $i++) { if (Test-Path -LiteralPath ($file + '.window')) { break }; if ($p.HasExited) { throw 'Fixture exited' }; Start-Sleep -Milliseconds 100 }
        if (!(Test-Path -LiteralPath ($file + '.window'))) { throw 'Fixture window missing' }
        $handle = [long](Get-Content -LiteralPath ($file + '.window') -Raw)
        Start-Sleep -Milliseconds 500
        $targetPid = if ($case -eq 'wrong-pid') { $PID } else { $p.Id }
        $target = $targetPid.ToString() + ':' + $handle.ToString()
        $lines = & $runner --control-target $target 2>&1
        $code = $LASTEXITCODE
        $expected = if ($case -eq 'normal') { 0 } else { 2 }
        if ($code -ne $expected) { throw "Control fixture $case exit $code expected $expected stdout=$lines" }
        if ($case -eq 'normal') {
            for ($i=0; $i -lt 30 -and !(Test-Path -LiteralPath $file); $i++) { Start-Sleep -Milliseconds 100 }
            $result = Get-Content -LiteralPath $file -Encoding UTF8 -Raw | ConvertFrom-Json
            if ($result.submitted -ne 1 -or !$result.synthetic_username_ok -or !$result.synthetic_password_ok) { throw 'Control values or submission count mismatch' }
        } elseif (Test-Path -LiteralPath $file) { throw 'Rejected fixture still received submit' }
        if ($case -eq 'changed-user') {
            $mutation = Get-Content -LiteralPath ($file + '.mutation') -Encoding UTF8 -Raw | ConvertFrom-Json
            if (!$mutation.synthetic_username_changed -or !$mutation.synthetic_password_assigned) { throw 'Changed-user fixture did not exercise the post-fill account check' }
        }
        $records += [pscustomobject]@{case=$case;target=$target;command=@($runner,'--control-target',$target);stdout=($lines -join "`n");stderr='';exit_status=$code;observed=$(if ($case -eq 'normal') { 'one owned-fixture submission; synthetic values matched' } else { 'no submission' })}
        Write-Output ("PASS control fallback $case exit=$code; real_authentication=not_attempted")
    } finally {
        if (!$p.HasExited) { $null=$p.CloseMainWindow(); if (!$p.WaitForExit(3000)) { $p.Kill(); $p.WaitForExit() } }
    }
}
$records | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding UTF8
