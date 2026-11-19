# Focused WPF UI test: no clipboard capture, installation, tray icon or global hotkey hook.
param(
    [string]$Dotnet = 'dotnet',
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutDir) { $OutDir = Join-Path $root 'dist\search-selftest' }
$OutDir = [IO.Path]::GetFullPath($OutDir)
& $Dotnet build (Join-Path $root 'src\AgosClip.csproj') -c Release -nologo -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Join-Path $root 'src\bin\Release\net8.0-windows10.0.17763.0\win-x64\AgosClip.exe'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('AgosClip-searchtest-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$oldData = $env:AGOSCLIP_DATA
try {
    $env:AGOSCLIP_DATA = Join-Path $scratch 'data'
    $p = Start-Process -FilePath $exe -ArgumentList '--selftest-search', ('"' + $OutDir + '"') -Wait -PassThru
    $result = Join-Path $OutDir 'results.txt'
    if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result -Encoding UTF8 }
    "Search UI test exit: $($p.ExitCode); screenshots and report: $OutDir"
    $testExit = $p.ExitCode
}
finally {
    $env:AGOSCLIP_DATA = $oldData
    # Delete only the unique scratch directory created by this invocation; keep the review output.
    $resolvedScratch = (Resolve-Path -LiteralPath $scratch).Path
    if ($resolvedScratch -ne [IO.Path]::GetFullPath($scratch)) { throw 'Unexpected scratch directory' }
    Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
}
exit $testExit
