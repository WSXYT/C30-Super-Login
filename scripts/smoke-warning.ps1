param([string]$Executable = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class C30WarningCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
'@
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'src\C30SuperLogin.Desktop\bin\Release\net10.0-windows\C30SuperLogin.exe'
if ($Executable) { $exe = (Resolve-Path -LiteralPath $Executable).Path }
$out = Join-Path $root ('artifacts\verification\pre-login-warning\run-' + [guid]::NewGuid().ToString('N'))
if ($OutputDirectory) { $out = [IO.Path]::GetFullPath($OutputDirectory) }
New-Item -ItemType Directory -Path $out -Force | Out-Null
$cli = Get-Content -LiteralPath (Join-Path $root 'artifacts\verification\accounts-cli\result.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$records = @()
foreach ($case in @('skip','now','countdown','defer')) {
    $data = Join-Path $out $case
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    Get-ChildItem -LiteralPath $cli.data -File | Where-Object { $_.Name -eq 'settings.json' -or $_.Extension -eq '.credential' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $data }
    $settings = Get-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding UTF8 -Raw | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName PreLoginWarningSeconds -NotePropertyValue $(if ($case -eq 'countdown') { 1 } else { 30 }) -Force
    $settings | Add-Member -NotePropertyName AutoDetectInstall -NotePropertyValue $false -Force
    $settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding UTF8
    $arguments = @('login','--id',$settings.Profiles[0].Id,'--trigger','classisland','--headless','--data-dir',$data)
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $terminated = $false
    try {
        for ($i=0; $i -lt 100; $i++) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break }; if ($p.HasExited) { throw 'Warning exited before opening' }; Start-Sleep -Milliseconds 100 }
        $handle = $p.MainWindowHandle
        if ($handle -eq 0) { throw 'No warning window' }
        Start-Sleep -Milliseconds 150
        $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),'WarningCountdown'
        if (!$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)) { throw 'Missing countdown' }
        if ($case -eq 'skip') {
            $rect = New-Object C30WarningCapture+RECT; $null=[C30WarningCapture]::GetWindowRect($handle,[ref]$rect)
            $bitmap = New-Object System.Drawing.Bitmap ($rect.Right-$rect.Left),($rect.Bottom-$rect.Top)
            $graphics=[System.Drawing.Graphics]::FromImage($bitmap); $dc=$graphics.GetHdc()
            try { $captured=[C30WarningCapture]::PrintWindow($handle,$dc,2) } finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
            if (!$captured) { throw 'Warning capture failed' }
            $bitmap.Save((Join-Path $out 'warning.png'),[System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
        }
        if ($case -ne 'countdown') {
            $id = switch ($case) { 'skip' { 'WarningSkip' }; 'now' { 'WarningNow' }; 'defer' { 'WarningDefer' } }
            $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),$id
            $button=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
            if (!$button) { throw ('Missing warning button ' + $id) }
            $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        }
        if ($case -eq 'defer') {
            Start-Sleep -Milliseconds 1200
            if ($p.HasExited -or [C30WarningCapture]::IsWindowVisible($handle) -or (Test-Path -LiteralPath (Join-Path $data 'last-operation.json'))) { throw 'Deferral did not hide and hold login' }
            $second=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
            try { if (!$second.WaitForExit(10000) -or $second.ExitCode -ne 2) { throw ('Concurrent warning was not rejected; observed exit=' + $second.ExitCode) } }
            finally { if (!$second.HasExited) { $second.Kill(); $second.WaitForExit() } }
            # Exercise the real one-minute timer, not an accelerated test-only path.
            $deadline=[DateTime]::UtcNow.AddSeconds(70)
            while (![C30WarningCapture]::IsWindowVisible($handle) -and [DateTime]::UtcNow -lt $deadline) {
                if ($p.HasExited -or (Test-Path -LiteralPath (Join-Path $data 'last-operation.json'))) { throw 'Deferral submitted or exited before showing the reminder again' }
                Start-Sleep -Milliseconds 200
            }
            if (![C30WarningCapture]::IsWindowVisible($handle)) { throw 'Reminder did not reappear after one-minute deferral' }
            $window=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
            $condition=New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),'WarningSkip'
            $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            if (!$p.WaitForExit(10000)) { throw 'Deferred reminder did not exit after skip' }
            $result=Get-Content -LiteralPath (Join-Path $data 'last-operation.json') -Encoding UTF8 -Raw | ConvertFrom-Json
            if ($result.Code -ne 'skipped' -or $result.Submitted -or $p.ExitCode -ne 0) { throw 'Deferred reminder skip did not hold login' }
            $observed='hidden without login; second warning rejected; reminder reappeared after one minute; skip exited=0 without submission'
        } else {
            if (!$p.WaitForExit(15000)) { throw ('Warning did not finish: ' + $case) }
            $result=Get-Content -LiteralPath (Join-Path $data 'last-operation.json') -Encoding UTF8 -Raw | ConvertFrom-Json
            $expected = if ($case -eq 'skip') { 'skipped' } else { 'not_running' }
            $exitExpected = if ($case -eq 'skip') { 0 } else { 2 }
            if ($result.Code -ne $expected -or $result.Submitted -or $p.ExitCode -ne $exitExpected) { throw ('Unexpected isolated warning outcome: ' + $case) }
            $observed=$expected + '; real_authentication=not_attempted'
        }
        $records += [pscustomobject]@{case=$case;executable=$exe;arguments=$arguments;exit_status=$p.ExitCode;terminated_by_test=$terminated;observed=$observed}
        Write-Output ('PASS warning ' + $case + ': ' + $observed)
    } finally { if (!$p.HasExited) { $p.Kill(); $p.WaitForExit() } }
}
$records | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding UTF8
Write-Output ('WARNING RESULT ' + (Join-Path $out 'result.json'))
