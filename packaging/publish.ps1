# Windows build: SDK from global.json, Python 3.12+, NSIS 3.11 on PATH or MAKENSIS.
param([ValidateSet('Complete', 'Payload', 'Installer')][string] $Phase = 'Complete')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $repo 'dist'
$payload = Join-Path $dist 'Citrus-win-x64'
$generated = Join-Path $dist 'nsis'
$output = Join-Path $dist 'Citrus-Setup.exe'
$installerScript = Join-Path $PSScriptRoot 'Citrus.nsi'
Push-Location $repo
try {
    $dotnetCommand = if ($env:DOTNET) { $env:DOTNET } else { 'dotnet' }
    $nsisCommand = if ($env:MAKENSIS) { $env:MAKENSIS } else { 'makensis' }
    $pythonCommand = if ($env:PYTHON) { $env:PYTHON } else { 'python' }
    foreach ($path in @($payload, $generated, (Join-Path $dist 'third-party-sources'),
                         (Join-Path $dist 'Citrus-ThirdParty-Sources.zip'), (Join-Path $dist 'source-distribution.json'))) {
        $resolvedOutput = [IO.Path]::GetFullPath($path)
        if (-not $resolvedOutput.StartsWith([IO.Path]::GetFullPath($dist) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output escaped distribution directory' }
        for ($ancestor = $resolvedOutput; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
            try { $entry = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop }
            catch [Management.Automation.ItemNotFoundException] { continue }
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing linked output path' }
        }
        if (Test-Path -LiteralPath $resolvedOutput) {
            if ($Phase -ne 'Installer') { Remove-Item -LiteralPath $resolvedOutput -Recurse -Force }
        }
    }
    if ($Phase -ne 'Installer') {
        & $dotnetCommand publish src/Citrus.Windows/Citrus.Windows.csproj -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:RestoreLockedMode=true -m:1 -nodeReuse:false -o $payload
        if ($LASTEXITCODE) { throw 'Self-contained publish failed' }
        & $pythonCommand packaging/package.py prepare $payload --generated $generated
        if ($LASTEXITCODE) { throw 'Payload verification failed' }
    }
    foreach ($name in @('identity.nsh', 'version.nsh', 'paths.nsh', 'install.nsh', 'uninstall.nsh', 'verify.nsh')) {
        $includePath = Join-Path $generated $name
        if (-not (Test-Path -LiteralPath $includePath -PathType Leaf)) {
            throw "Missing generated NSIS include: $includePath (generated directory: $generated)"
        }
    }
    Write-Host "Generated NSIS includes: $generated"
    if ($Phase -ne 'Payload') {
        & $pythonCommand packaging/package.py verify-prepared $payload --generated $generated
        if ($LASTEXITCODE) { throw 'Prepared payload verification failed' }
        & $nsisCommand /V3 /NOCD "/DGENERATED=$generated" "/DOUTPUT=$output" $installerScript
        if ($LASTEXITCODE) { throw 'NSIS compilation failed' }
        & $pythonCommand packaging/package.py checksums $dist
        if ($LASTEXITCODE) { throw 'Checksums failed' }
    }
} finally { Pop-Location }
