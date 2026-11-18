[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
Set-Location E:\CloudCLI\AgosClip\src
$b = dotnet build -c Release -nologo -v q 2>&1
$errs = $b | Select-String -Pattern ': error ' 
if ($errs) { $errs | Select-Object -First 30 | ForEach-Object { $_.Line }; exit 1 }
$out = 'E:\CloudCLI\tmp\agosclip-selftest'
$data = 'E:\CloudCLI\tmp\agosclip-selftest-data'
Remove-Item -Recurse -Force $out, $data -ErrorAction SilentlyContinue
$env:AGOSCLIP_DATA = $data
$exe = 'E:\CloudCLI\AgosClip\src\bin\Release\net8.0-windows10.0.17763.0\win-x64\AgosClip.exe'
$p = Start-Process -FilePath $exe -ArgumentList '--selftest', $out -PassThru -Wait
"exit code: $($p.ExitCode)"
if (Test-Path "$out\results.txt") { Get-Content -Encoding UTF8 "$out\results.txt" }
if (Test-Path "$data\agosclip.log") { '--- log ---'; Get-Content -Encoding UTF8 "$data\agosclip.log" | Select-Object -Last 30 }
