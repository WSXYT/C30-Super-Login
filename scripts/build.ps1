param([switch]$Verify, [string]$Python = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & .\native\build.cmd
    if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }
    & dotnet build .\src\C30SuperLogin.Desktop\C30SuperLogin.Desktop.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Application build failed: $LASTEXITCODE" }
    if ($Verify) {
        if (!$Python) {
            $localPython = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python311\python.exe'
            if (Test-Path -LiteralPath $localPython) { $Python = $localPython } else { $Python = 'python' }
        }
        & $Python -B -X utf8 .\scripts\verify.py
        if ($LASTEXITCODE -ne 0) { throw "Verification failed: $LASTEXITCODE" }
    }
    $releaseRoot = Join-Path $root 'artifacts\release'
    $identity = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
    $final = Join-Path $releaseRoot 'C30SuperLogin-0.1.0-win-x64'
    $dest = Join-Path $releaseRoot ('staging-' + $identity)
    & dotnet publish .\src\C30SuperLogin.Desktop\C30SuperLogin.Desktop.csproj -c Release -r win-x64 --self-contained false --nologo -o $dest
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
    foreach ($name in @('C30SuperLogin.exe','C30SuperLogin.dll','C30SuperLogin.Core.dll','C30Native.exe','C30Agent.dll','Avalonia.Controls.dll','FluentAvalonia.dll','libSkiaSharp.dll','libHarfBuzzSharp.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $dest $name))) { throw "Missing release file: $name" }
    }
    Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $dest -Force
    $licenses = Join-Path $root 'references\licenses'
    if (Test-Path -LiteralPath $licenses) { Copy-Item -LiteralPath $licenses -Destination (Join-Path $dest 'licenses') -Recurse }
    if (Test-Path -LiteralPath (Join-Path $root 'docs')) { Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination (Join-Path $dest 'docs') -Recurse }
    if (Get-ChildItem -LiteralPath $dest -Recurse -File | Where-Object { $_.Name -match '\.credential$|^settings\.json$|^DuiLib\.dll$|^teach\.exe$|^signin\.js$' }) { throw 'Private/vendor fixture data found in release' }
    if (Get-ChildItem -LiteralPath $dest -Recurse -File | Where-Object { $_.Name -match '^Sentry|^PostHog' }) { throw 'Unexpected stale telemetry SDK in clean publish' }
    $hashes = Get-ChildItem -LiteralPath $dest -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.FullName.Substring($dest.Length+1).Replace('\','/') }
    [IO.File]::WriteAllLines((Join-Path $dest 'SHA256SUMS.txt'), [string[]]$hashes, [Text.Encoding]::UTF8)
    # Publish into fresh staging; preserve old releases instead of merging stale WPF/SDK files.
    foreach ($path in @($dest,$final)) {
        if (![IO.Path]::GetFullPath($path).StartsWith([IO.Path]::GetFullPath($releaseRoot) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Release move escaped release root' }
    }
    if (Test-Path -LiteralPath $final) {
        $previous = $final + '.previous-' + $identity
        if (![IO.Path]::GetFullPath($previous).StartsWith([IO.Path]::GetFullPath($releaseRoot) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Previous release path escaped release root' }
        Move-Item -LiteralPath $final -Destination $previous
    }
    Move-Item -LiteralPath $dest -Destination $final
    $dest = $final
    $zip = $dest + '.zip'
    Compress-Archive -LiteralPath $dest -DestinationPath $zip -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
        if (!($entries | Where-Object { $_ -match '[/\\]C30SuperLogin\.exe$' }) -or !($entries | Where-Object { $_ -match '[/\\]C30Agent\.dll$' })) { throw 'Archive content verification failed' }
        Write-Output ('VERIFIED ZIP entries=' + $entries.Count)
    } finally { $archive.Dispose() }
    Get-Item -LiteralPath (Join-Path $dest 'C30SuperLogin.exe'),$zip | Select-Object FullName,Length
    Get-FileHash -LiteralPath $zip -Algorithm SHA256
} finally { Pop-Location }
