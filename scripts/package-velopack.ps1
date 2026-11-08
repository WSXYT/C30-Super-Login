param([string]$Version = '0.1.0', [string]$UpdateUrl = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Version must be a semantic release version' }
if ($UpdateUrl -and ($UpdateUrl -notmatch '^https://' -or ([uri]$UpdateUrl).UserInfo -or ([uri]$UpdateUrl).Fragment)) { throw 'UpdateUrl must be HTTPS without credentials or fragment' }
$identity = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$publish = Join-Path $root ('artifacts\velopack\publish-' + $identity)
$out = Join-Path $root 'artifacts\velopack\releases'
if ($OutputDirectory) { $out = [IO.Path]::GetFullPath($OutputDirectory) }
$finalOut = [IO.Path]::GetFullPath($out)
$out = $finalOut + '.staging-' + $identity
$toolDir = Join-Path $root 'artifacts\tools\velopack'
$tool = Join-Path $toolDir 'vpk.exe'
$oldTemp = $env:TEMP; $oldTmp = $env:TMP
$packageTemp = Join-Path $root ('artifacts\velopack\temp-' + $identity)
New-Item -ItemType Directory -Path $packageTemp -Force | Out-Null
# Keep large disposable pack files on the workspace volume, not the system disk.
$env:TEMP = $packageTemp; $env:TMP = $packageTemp
Push-Location $root
try {
    if (!(Test-Path -LiteralPath $tool)) {
        & dotnet tool install vpk --version 1.2.161 --tool-path $toolDir
        if ($LASTEXITCODE -ne 0) { throw 'Velopack CLI install failed' }
    }
    & .\native\build.cmd
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
    & dotnet publish .\src\C30SuperLogin.Desktop\C30SuperLogin.Desktop.csproj -c Release -r win-x64 --self-contained true --nologo "-p:Version=$Version" "-p:C30UpdateUrl=$UpdateUrl" -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed' }
    Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $publish
    foreach ($name in @('licenses','docs')) {
        $source = if ($name -eq 'licenses') { Join-Path $root 'references\licenses' } else { Join-Path $root 'docs' }
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $publish $name) -Recurse }
    }
    if (Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Name -match '\.credential$|^settings\.json$|^DuiLib\.dll$|^teach\.exe$|^signin\.js$' }) { throw 'Private/vendor files found in package source' }
    & $tool --skip-updates --yes --legacyConsole pack --packId C30SuperLogin --packTitle 'C30 登录助手' --packVersion $Version --packDir $publish --mainExe C30SuperLogin.exe --runtime win-x64 --channel win --icon .\src\C30SuperLogin.Desktop\Assets\app.ico --releaseNotes .\docs\release-notes\0.1.0.md --outputDir $out
    if ($LASTEXITCODE -ne 0) { throw 'Velopack pack failed' }
    $setup = Get-ChildItem -LiteralPath $out -Filter '*-Setup.exe' -File | Where-Object Name -ne 'C30SuperLogin-Setup.exe' | Select-Object -First 1
    if (!$setup) { throw 'Setup executable missing' }
    Copy-Item -LiteralPath $setup.FullName -Destination (Join-Path $out 'C30SuperLogin-Setup.exe') -Force
    if (!(Test-Path -LiteralPath (Join-Path $out 'releases.win.json'))) { throw 'Update feed missing' }
    $hashes = Get-ChildItem -LiteralPath $out -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' -and $_.Name -ne 'package-result.json' } | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.Name }
    [IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'),[string[]]$hashes,[Text.Encoding]::UTF8)
    [pscustomobject]@{version=$Version;sdk_version='1.2.161';publish=$publish;output=$out;setup=(Join-Path $out 'C30SuperLogin-Setup.exe');update_url=$UpdateUrl;self_contained=$true;code_signed=$false;production_update_tested=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'package-result.json') -Encoding UTF8
    # Never let an older package with the same version satisfy the current run.
    # Resolve and constrain both moves to the explicitly selected output parent.
    $parent = [IO.Path]::GetDirectoryName($finalOut) + [IO.Path]::DirectorySeparatorChar
    $previous = $finalOut + '.previous-' + $identity
    foreach ($path in @($out,$finalOut,$previous)) {
        if (![IO.Path]::GetFullPath($path).StartsWith($parent,[StringComparison]::OrdinalIgnoreCase)) { throw 'Package output move escaped output parent' }
    }
    if (Test-Path -LiteralPath $finalOut) { Move-Item -LiteralPath $finalOut -Destination $previous }
    Move-Item -LiteralPath $out -Destination $finalOut
    $out = $finalOut
    [pscustomobject]@{version=$Version;sdk_version='1.2.161';publish=$publish;output=$out;setup=(Join-Path $out 'C30SuperLogin-Setup.exe');update_url=$UpdateUrl;self_contained=$true;code_signed=$false;production_update_tested=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'package-result.json') -Encoding UTF8
    Get-ChildItem -LiteralPath $out -File | Select-Object Name,Length
    Write-Output ('VELOPACK RESULT ' + (Join-Path $out 'package-result.json'))
} finally { $env:TEMP = $oldTemp; $env:TMP = $oldTmp; Pop-Location }
