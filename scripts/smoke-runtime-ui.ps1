param([string]$Executable = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RuntimeUiNative {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
}
'@
$root = Split-Path $PSScriptRoot -Parent
if (!$Executable) { $Executable = Join-Path $root 'src\C30SuperLogin.Desktop\bin\Release\net10.0-windows\C30SuperLogin.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts\verification\runtime-oobe\ui-' + [guid]::NewGuid().ToString('N')) }
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $out -Force | Out-Null
$records = [Collections.Generic.List[object]]::new()
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$window = $null
function Check([bool]$value, [string]$message) { if (!$value) { throw $message }; $records.Add(@{check=$message;passed=$true}); Write-Output ('PASS ' + $message) }
function Wait-Until([scriptblock]$condition, [string]$message) {
    for ($i=0; $i -lt 100; $i++) { if (& $condition) { return }; Start-Sleep -Milliseconds 100 }
    throw $message
}
function Find-Id($parent, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),$id
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
}
function Find-Name($parent, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty),$name
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
}
function Find-Help($parent, [string]$text) {
    $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::HelpTextProperty),$text
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
}
function Windows($process) {
    $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty),$process.Id
    return [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$c)
}
function Wait-Window($process, [string]$id, [string]$name) {
    for ($i=0; $i -lt 100; $i++) {
        foreach ($candidate in (Windows $process)) {
            if (($id -and $candidate.Current.AutomationId -eq $id) -or ($name -and $candidate.Current.Name -eq $name)) { return $candidate }
            # Owned Avalonia windows appear below their owner in the UIA tree.
            $child = if ($id) { Find-Id $candidate $id } else { Find-Name $candidate $name }
            if ($child -and $child.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { return $child }
        }
        $process.Refresh(); if ($process.HasExited) { throw 'Owned app exited before expected window' }; Start-Sleep -Milliseconds 100
    }
    throw ('Window not found: ' + $id + $name)
}
function Invoke-Element($element) {
    if (!$element) { throw 'Required UI element missing' }
    $pattern=$null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) { $pattern.Select() }
    elseif ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) { $pattern.Invoke() }
    elseif ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern,[ref]$pattern)) { $pattern.Toggle() }
    else { throw ('No action pattern: ' + $element.Current.Name) }
    Start-Sleep -Milliseconds 250
}
function Select-Combo($parent, [string]$id, [string]$name) {
    $combo=Find-Id $parent $id
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 200
    $option=Find-Name $combo $name
    if (!$option) { $option=Find-Name $parent $name }
    Invoke-Element $option
}
function Set-Field($parent, [string]$id, [string]$value) {
    (Find-Id $parent $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}
function Capture($target, [string]$name) {
    $handle=[IntPtr]$target.Current.NativeWindowHandle
    $rect=New-Object RuntimeUiNative+RECT
    if (![RuntimeUiNative]::GetWindowRect($handle,[ref]$rect)) { throw 'Capture rectangle failed' }
    $bitmap=New-Object Drawing.Bitmap ($rect.Right-$rect.Left),($rect.Bottom-$rect.Top)
    $graphics=[Drawing.Graphics]::FromImage($bitmap); $dc=$graphics.GetHdc()
    try { $ok=[RuntimeUiNative]::PrintWindow($handle,$dc,2) } finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    try { if (!$ok) { throw 'PrintWindow failed' }; $bitmap.Save((Join-Path $out ($name + '.png')),[Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
function Close-Window($target) { $null=[RuntimeUiNative]::PostMessage([IntPtr]$target.Current.NativeWindowHandle,0x10,[IntPtr]::Zero,[IntPtr]::Zero) }
function Start-Owned([string]$label, [bool]$completed=$false) {
    $data=Join-Path $out $label; New-Item -ItemType Directory -Path $data -Force | Out-Null
    @{InstallRoot=(Join-Path $data 'NO_C30');AutoDetectInstall=$false;LaunchC30=$false;Theme=$(if($env:C30_UI_TEST_DARK -eq '1'){'Dark'}else{'Light'});OnboardingVersion=$(if($completed){1}else{0});RuntimeMode=0;UseAdministrator=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding UTF8
    $process=Start-Process -FilePath $Executable -ArgumentList @('--data-dir', ('"' + $data + '"')) -WindowStyle Hidden -PassThru
    $owned.Add($process)
    return @{process=$process;data=$data}
}
try {
    $fixture=Start-Owned 'completed'; $p=$fixture.process; $data=$fixture.data
    $window=Wait-Window $p 'OnboardingWindow' ''
    Start-Sleep -Milliseconds 450; Capture $window 'oobe-welcome'
    Check ($null -ne (Find-Name $window '上课前，少点几下。')) 'fresh profile opens first-run welcome'
    Invoke-Element (Find-Id $window 'OobeNext')
    Check ($null -eq (Find-Id $window 'OobeAdministrator')) 'OOBE removes the administrator option'
    Check ($null -ne (Find-Name $window '连接 C30 必须使用管理员权限，授权由 Windows 处理，CI 自身无需管理员权限。')) 'OOBE explains required Windows elevation instead of offering an opt-out'
    Invoke-Element (Find-Id $window 'OobeOnDemand'); Capture $window 'oobe-runtime'
    Invoke-Element (Find-Id $window 'OobeNext')
    Check ($null -ne (Find-Name $window '把账号准备好')) 'OOBE includes account import and creation before completion'
    Check ($null -ne (Find-Id $window 'OobeDetectInstall')) 'OOBE exposes one-shot C30 directory detection'
    Invoke-Element (Find-Id $window 'OobeNext')
    Check ($null -ne (Find-Id $window 'OobeAccountUsername')) 'empty account setup requires an explicit skip rather than silently completing'
    Invoke-Element (Find-Id $window 'OobeImportAccounts')
    Wait-Until { Test-Path -LiteralPath (Join-Path $data 'last-operation.json') } 'isolated account import did not produce a result'
    Wait-Until { (Find-Id $window 'OobeNext').Current.IsEnabled } 'account import did not unlock OOBE'
    $operation=Get-Content -LiteralPath (Join-Path $data 'last-operation.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($operation.Code -eq 'not_running') 'OOBE import produces an actual no-C30 result, not fabricated account success'
    Set-Field $window 'OobeAccountLabel' 'LOCAL OOBE Fixture'
    Set-Field $window 'OobeAccountUsername' 'LOCAL_OOBE_ACCOUNT'
    Set-Field $window 'OobeAccountPassword' 'LOCAL_SYNTHETIC_PASSWORD'
    Wait-Until { $s=Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json; $s.Profiles.Count -eq 1 } 'OOBE account did not auto-save'
    $settings=Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($settings.Profiles[0].Number -eq 1 -and $settings.OnboardingVersion -eq 0) 'OOBE auto-saves an encrypted account with short ID before completion'
    Invoke-Element (Find-Id $window 'OobeNewAccount')
    Set-Field $window 'OobeAccountUsername' 'LOCAL_OOBE_ACCOUNT'
    Set-Field $window 'OobeAccountPassword' 'LOCAL_SYNTHETIC_PASSWORD'
    Start-Sleep -Milliseconds 1100
    $settings=Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($settings.Profiles.Count -eq 1 -and $settings.Profiles[0].Number -eq 1) 'OOBE merges a duplicate without changing its numeric ID'
    $listing=Join-Path $data 'oobe-accounts.json'
    $cliOut=Join-Path $data 'cli-stdout.txt'; $cliError=Join-Path $data 'cli-stderr.txt'
    $cli=Start-Process -FilePath $Executable -ArgumentList @('accounts','list','--data-dir',('"'+$data+'"'),'--output',('"'+$listing+'"')) -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $cliOut -RedirectStandardError $cliError
    $owned.Add($cli)
    Write-Output ('ACCOUNT_RELOAD_EXIT=' + $cli.ExitCode)
    Write-Output ([IO.File]::ReadAllText($cliOut)); Write-Output ([IO.File]::ReadAllText($cliError))
    if ($cli.ExitCode -ne 0) { throw 'OOBE account CLI reload failed' }
    $accounts=Get-Content -LiteralPath $listing -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($accounts.Count -eq 1 -and $accounts[0].Number -eq 1 -and $accounts[0].CanLogin) 'CLI can decrypt and read the account saved inside OOBE'
    $credential=Get-ChildItem -LiteralPath $data -Filter '*.credential' -File | Select-Object -First 1
    Check ($null -ne $credential -and !([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($credential.FullName))).Contains('LOCAL_SYNTHETIC_PASSWORD')) 'OOBE credential file does not contain the plaintext fixture password'
    Capture $window 'oobe-accounts'; Invoke-Element (Find-Id $window 'OobeNext')
    foreach ($id in @('OobeUsageStatistics','OobeErrorReports','OobeUpdates')) {
        Check ((Find-Id $window $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) ($id + ' defaults ON in OOBE')
    }
    Select-Combo $window 'OobeWarning' '提前 10 秒'
    (Find-Id $window 'OobeAutomation').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 200
    Set-Field $window 'OobeSubjectId' 'LOCAL_SUBJECT'
    Set-Field $window 'OobeSubjectName' 'LOCAL Lesson'
    Invoke-Element (Find-Id $window 'OobeAddRule')
    $settings=Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($settings.Rules.Count -eq 1 -and $settings.Rules[0].ProfileId -eq $settings.Profiles[0].Id -and $settings.PreLoginWarningSeconds -eq 10) 'OOBE configures a course-account binding and login warning before entering main UI'
    Capture $window 'oobe-preferences'; Invoke-Element (Find-Id $window 'OobeNext'); Capture $window 'oobe-summary'
    Check ($null -ne (Find-Name $window '已选：仅调用时运行。关闭主窗口后直接退出。')) 'OOBE summary reflects on-demand selection'
    Invoke-Element (Find-Id $window 'OobeNext')
    $window=Wait-Window $p '' 'C30 Super Login'; Start-Sleep -Milliseconds 500
    $settings=Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($settings.OnboardingVersion -eq 1 -and $settings.RuntimeMode -eq 1 -and $settings.UseAdministrator) 'OOBE completion persists on-demand mode and required administrator access, including old false settings'
    Check ($settings.Telemetry.PostHogEnabled -and $settings.Telemetry.SentryEnabled) 'both reporting preferences remain default ON'
    Invoke-Element (Find-Id $window 'Navigation5')
    Check ($null -ne (Find-Name $window '相关链接')) 'About groups external links under a clear related-links heading'
    Check ($null -ne (Find-Help $window '参考项目：了解其他教学软件自动化的思路。')) 'EasiAuto link explains that it is a reference project, including for screen readers'
    Check ($null -ne (Find-Help $window '课表与课前自动化工具，可配合本助手使用。')) 'ClassIsland link explains timetable and automation use, including for screen readers'
    Check ($null -ne (Find-Name $window '查看参考项目 ↗') -and $null -ne (Find-Name $window '了解 ClassIsland ↗')) 'both external links retain descriptive clickable actions'
    Check ($null -ne (Find-Id $window 'EasiAutoLogo') -and $null -ne (Find-Id $window 'ClassIslandLogo')) 'both related projects show their own upstream icons'
    Check ($null -ne (Find-Name $window '预览版暂未开放在线更新。')) 'unconfigured update service shows friendly preview wording'
    Capture $window 'about'
    Invoke-Element (Find-Id $window 'Navigation4')
    Check ($null -eq (Find-Id $window 'UseAdministrator')) 'Settings removes the administrator opt-out too'
    Invoke-Element (Find-Name $window '重新查看首次引导')
    $guide=Wait-Window $p 'OnboardingWindow' ''
    Invoke-Element (Find-Id $guide 'OobeNext'); Invoke-Element (Find-Id $guide 'OobeResident'); Close-Window $guide
    Start-Sleep -Milliseconds 450; $p.Refresh()
    $settings=Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check (!$p.HasExited -and $settings.RuntimeMode -eq 1) 'cancelling reopened OOBE keeps main app open and does not save partial choices'
    Select-Combo $window 'RuntimeMode' '后台常驻'
    $settings=Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($settings.RuntimeMode -eq 0) 'Settings can switch from on-demand to resident'
    Capture $window 'settings-resident'
    Select-Combo $window 'RuntimeMode' '仅调用时运行'
    $settings=Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($settings.RuntimeMode -eq 1) 'Settings can switch back to on-demand'
    Capture $window 'settings-ondemand'; Close-Window $window
    Check ($p.WaitForExit(10000) -and $p.ExitCode -eq 0) 'on-demand window close exits gracefully instead of staying resident'
    $fixture=Start-Owned 'cancelled'; $p=$fixture.process
    $window=Wait-Window $p 'OnboardingWindow' ''; Close-Window $window
    Check ($p.WaitForExit(10000) -and $p.ExitCode -eq 0) 'cancelling fresh OOBE exits gracefully'
    $settings=Get-Content -LiteralPath (Join-Path $fixture.data 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check (!$settings.OnboardingVersion) 'cancelled fresh OOBE is offered again on next launch'
    $fixture=Start-Owned 'resident' $true; $p=$fixture.process
    $window=Wait-Window $p '' 'C30 Super Login'; Start-Sleep -Milliseconds 400
    $mainHandle=[IntPtr]$window.Current.NativeWindowHandle; Close-Window $window
    Wait-Until { ![RuntimeUiNative]::IsWindowVisible($mainHandle) } 'resident window did not hide'
    $p.Refresh(); Check (!$p.HasExited) 'resident close hides the window while the owned process remains alive'
    @{passed=$true;executable=$Executable;checks=$records;data=$data;real_uac='not_attempted';real_c30='not_attempted';startup_registration='none'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding UTF8
    Write-Output ('RESULT ' + $records.Count + ' runtime/OOBE/About checks PASS; isolated nonexistent C30 root; no UAC or real login')
} catch {
    $failure=$_.ToString()
    try {
        if ($window) { Capture $window 'failure'; $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { @{id=$_.Current.AutomationId;name=$_.Current.Name;type=$_.Current.ControlType.ProgrammaticName} } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'failure-tree.json') -Encoding UTF8 }
    } catch { Write-Output ('Failure capture: ' + $_) }
    @{passed=$false;error=$failure;checks=$records} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding UTF8
    throw $failure
} finally {
    foreach ($process in $owned) { $process.Refresh(); if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() } }
}
