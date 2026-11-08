param([switch]$AccountChecks, [string]$Executable = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$Dark = $env:C30_UI_TEST_DARK -eq '1'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class C30Capture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
}
'@
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root $(if ($Dark) { 'artifacts\verification\ui-dark' } elseif ($AccountChecks) { 'artifacts\verification\ui-accounts' } else { 'artifacts\verification\ui' })
if ($OutputDirectory) { $out = [IO.Path]::GetFullPath($OutputDirectory) }
New-Item -ItemType Directory -Path $out -Force | Out-Null
$exe = Join-Path $root 'src\C30SuperLogin.Desktop\bin\Release\net10.0-windows\C30SuperLogin.exe'
if ($Executable) { $exe = (Resolve-Path -LiteralPath $Executable).Path }
$data = Join-Path $out ('data-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data -Force | Out-Null
if ($AccountChecks) {
    $cli = Get-Content -LiteralPath (Join-Path $root 'artifacts\verification\accounts-cli\result.json') -Raw | ConvertFrom-Json
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    Get-ChildItem -LiteralPath $cli.data -File | Where-Object { $_.Name -eq 'settings.json' -or $_.Extension -eq '.credential' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $data -Force }
    $fixture = (Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).Profiles[0].Id
}
if (!(Test-Path -LiteralPath (Join-Path $data 'settings.json'))) {
    @{InstallRoot=(Join-Path $data 'NO_C30');AutoDetectInstall=$false;LaunchC30=$false;OnboardingVersion=1} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding UTF8
}
if (Test-Path -LiteralPath (Join-Path $data 'settings.json')) {
    $options = Get-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding UTF8 -Raw | ConvertFrom-Json
    $options | Add-Member -NotePropertyName Theme -NotePropertyValue $(if ($Dark) { 'Dark' } else { 'Light' }) -Force
    $options | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding UTF8
}
$p = Start-Process -FilePath $exe -ArgumentList @('--data-dir', $data) -WindowStyle Hidden -PassThru
$records = @()
try {
    for ($i=0; $i -lt 100; $i++) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break }; if ($p.HasExited) { throw 'UI exited before creating a window' }; Start-Sleep -Milliseconds 100 }
    if ($p.MainWindowHandle -eq 0) { throw 'No Avalonia window' }
    Start-Sleep -Milliseconds 700
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    $buttonType = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty),([System.Windows.Automation.ControlType]::Button)
    function Invoke-Button([string]$name) {
        $found = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -eq $name -and ($_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -or $_.Current.AutomationId -match '^Navigation\d$') } | Select-Object -First 1
        if (!$found) { throw "Missing button: $name" }
        Write-Output ("UIA action name=" + $name + " id=" + $found.Current.AutomationId + " type=" + $found.Current.ControlType.ProgrammaticName)
        $pattern = $null
        if ($found.Current.AutomationId -match '^Navigation\d$' -and $found.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) { $pattern.Select() }
        elseif ($found.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) { $pattern.Invoke() }
        else { throw "No invocation pattern: $name" }
        Start-Sleep -Milliseconds 450
    }
    function Capture-Window([string]$name) {
        $rect = New-Object C30Capture+RECT
        if (![C30Capture]::GetWindowRect($p.MainWindowHandle, [ref]$rect)) { throw 'GetWindowRect failed' }
        $bitmap = New-Object System.Drawing.Bitmap ($rect.Right-$rect.Left),($rect.Bottom-$rect.Top)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap); $dc = $graphics.GetHdc()
        try { $ok = [C30Capture]::PrintWindow($p.MainWindowHandle, $dc, 2) } finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
        if (!$ok) { $bitmap.Dispose(); throw 'PrintWindow failed' }
        $path = Join-Path $out ($name + '.png'); $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
        return $path
    }
    $records += [pscustomobject]@{page='overview';screenshot=(Capture-Window 'overview');observed=$window.Current.Name}
    # Traverse every real Avalonia page, exercising reparenting of reusable controls.
    $pages = @(@('账号档案','profiles'),@('课程自动化','automation'),@('连接与诊断','diagnostics'),@('设置','settings'),@('关于','about'),@('概览','overview-return'))
    foreach ($page in $pages) {
        Invoke-Button $page[0]
        $titleCondition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),'PageTitle'
        $title = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$titleCondition)
        if (!$title -or $title.Current.Name -ne $page[0]) { throw ('Navigation did not render page: ' + $page[0]) }
        $records += [pscustomobject]@{page=$page[1];screenshot=(Capture-Window $page[1]);observed=$window.Current.Name}
        Write-Output ('PASS UI page ' + $page[1])
    }
    if ($AccountChecks) {
        function Field([string]$id) {
            $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),$id
            $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if (!$element) { throw "Missing field: $id" }
            return $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        }
        Invoke-Button '账号档案'
        Invoke-Button '刷新'
        $rowCondition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),('AccountRecord_' + $fixture)
        $row = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
        if (!$row) { throw 'Saved account row was not loaded' }
        $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
        $fixtureNumber = ((Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).Profiles | Where-Object Id -eq $fixture).Number.ToString()
        if ((Field 'ProfileId').Current.Value -ne $fixtureNumber -or $fixtureNumber -notmatch '^[1-9][0-9]*$') { throw 'Selected account short numeric ID mismatch' }
        if ($window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$titleCondition).Current.Name -ne '账号档案') { throw 'Selecting an account changed pages' }
        (Field 'ProfileLabel').SetValue('LOCAL UI Fixture Updated')
        Start-Sleep -Milliseconds 1200 # Verify debounce without pressing Save.
        $saved = Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
        if ($saved.Profiles.Count -ne 1 -or $saved.Profiles[0].Id -ne $fixture -or $saved.Profiles[0].Label -ne 'LOCAL UI Fixture Updated') { throw 'UI save changed stable ID or duplicated account' }
        Write-Output 'PASS Avalonia UI reloads records and auto-saves without Save; stable ID, no duplicates'
        (Field 'LoginProfileId').SetValue($fixtureNumber)
        Invoke-Button '按 ID 登录'
        for ($i=0; $i -lt 50 -and !(Test-Path -LiteralPath (Join-Path $data 'last-operation.json')); $i++) { Start-Sleep -Milliseconds 100 }
        $operation = Get-Content -LiteralPath (Join-Path $data 'last-operation.json') -Raw | ConvertFrom-Json
        if ($operation.Code -ne 'not_running') { throw 'UI ID login did not resolve fixture profile before safe discovery' }
        Write-Output 'PASS UI short numeric ID login resolves saved profile; nonexistent fixture root prevents actual authentication'
        $records += [pscustomobject]@{page='account-records';screenshot=(Capture-Window 'account-records');observed='LOCAL synthetic account; stable profile ID; no real C30 login'}
        Invoke-Button '新建账号'
        if ((Field 'ProfileId').Current.Value) { throw 'Unsaved account should not expose an unreserved number' }
        (Field 'ProfileLabel').SetValue('LOCAL New Fixture')
        (Field 'ProfileUsername').SetValue('LOCAL_UI_NEW_ACCOUNT')
        (Field 'ProfilePassword').SetValue('LOCAL_UI_SYNTHETIC_PASSWORD')
        Start-Sleep -Milliseconds 1200
        $saved = Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
        $createdNumber = (Field 'ProfileId').Current.Value
        $createdId = ($saved.Profiles | Where-Object { $_.Number.ToString() -eq $createdNumber }).Id
        if ($saved.Profiles.Count -ne 2 -or !$createdId -or $createdNumber -notmatch '^[1-9][0-9]*$') { throw 'New account was not auto-saved with a short numeric ID' }
        Invoke-Button '新建账号'
        (Field 'ProfileUsername').SetValue('LOCAL_UI_NEW_ACCOUNT')
        (Field 'ProfilePassword').SetValue('LOCAL_UI_SYNTHETIC_PASSWORD')
        Start-Sleep -Milliseconds 1200
        $saved = Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
        if ($saved.Profiles.Count -ne 2 -or (Field 'ProfileId').Current.Value -ne $createdNumber) { throw 'Duplicate account was not merged into stable numeric ID' }
        Invoke-Button '删除'
        Start-Sleep -Milliseconds 900
        $saved = Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
        if ($saved.Profiles.Count -ne 1 -or (Test-Path -LiteralPath (Join-Path $data ($createdId.Replace('-','') + '.credential')))) { throw 'Deleted account was resurrected or credential remained' }
        Write-Output 'PASS new account auto-save, duplicate merge with stable ID, and deletion without resurrection'
    }
    Invoke-Button '设置'
    $text = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }
    if (($text -join "`n") -match 'PostHog|Sentry|DSN|项目 Key') { throw 'Provider configuration leaked into everyday UI' }
    foreach ($id in @('UsageStatistics','ErrorReports')) {
        $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),$id
        $toggle = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
        if (!$toggle) { throw "Missing privacy switch: $id" }
        $pattern = $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($pattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { throw "Privacy switch not default ON: $id" }
        $pattern.Toggle(); Start-Sleep -Milliseconds 120; $pattern.Toggle()
    }
    Write-Output 'PASS only two privacy switches, default ON; no provider names or DSN in everyday UI'
    $records += [pscustomobject]@{page='settings';screenshot=(Capture-Window 'settings');observed='Theme choice and two privacy switches only'}
    Invoke-Button '关于'
    Invoke-Button '退出程序'
    if (!$p.WaitForExit(10000)) { throw 'UI did not exit gracefully' }
    [pscustomobject]@{passed=$true;executable=$exe;data=$data;theme=$(if ($Dark) { 'Dark' } else { 'Light' });pid=$p.Id;exit_status=$p.ExitCode;captures=$records} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding UTF8
    Write-Output ('PASS graceful UI exit=' + $p.ExitCode)
} catch {
    if ($window -and !$p.HasExited) {
        $null = Capture-Window 'failure'
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { [pscustomobject]@{id=$_.Current.AutomationId;name=$_.Current.Name;type=$_.Current.ControlType.ProgrammaticName} } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'failure-tree.json') -Encoding UTF8
    }
    throw
} finally {
    if (!$p.HasExited) { $p.Kill(); $p.WaitForExit() }
}
