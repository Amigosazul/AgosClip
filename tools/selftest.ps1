param(
    [switch]$AnimationOnly,
    [string]$Dotnet = 'dotnet'
)

[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('AgosClip-selftest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$build = Join-Path $scratch 'build'
$out = Join-Path $scratch 'results'
$oldData = $env:AGOSCLIP_DATA
$env:AGOSCLIP_DATA = Join-Path $scratch 'data'
try {
    & $Dotnet build (Join-Path $root 'src\AgosClip.csproj') -c Release -p:RuntimeIdentifier= -p:SelfContained=false -o $build -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $mode = if ($AnimationOnly) { '--selftest-animation' } else { '--selftest' }
    & $Dotnet (Join-Path $build 'AgosClip.dll') $mode $out
    $result = $LASTEXITCODE
    $report = Join-Path $out 'results.txt'
    if (Test-Path -LiteralPath $report) { Get-Content -Encoding UTF8 -LiteralPath $report }
    if ($result -ne 0) { throw "Self-test failed ($result). Results: $scratch" }
    Write-Output "Self-test passed. Screenshots and test data: $scratch"
}
finally {
    $env:AGOSCLIP_DATA = $oldData
}
