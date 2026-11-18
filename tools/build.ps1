# Publishes AgosClip (self-contained win-x64) and builds dist\AgosClip-Setup-<ver>.exe
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
$root = Split-Path -Parent $PSScriptRoot
$ver = ([xml](Get-Content -Encoding UTF8 "$root\src\AgosClip.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Remove-Item -Recurse -Force "$root\publish" -ErrorAction SilentlyContinue
$out = dotnet publish "$root\src\AgosClip.csproj" -c Release -r win-x64 --self-contained true -o "$root\publish" -nologo -v q -p:DebugType=none 2>&1
if ($LASTEXITCODE -ne 0) { $out; exit 1 }
$mb = [math]::Round(((Get-ChildItem -Recurse "$root\publish" | Measure-Object Length -Sum).Sum / 1MB), 1)
"published $ver : $mb MB"
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
& $iscc /Q "/DAppVersion=$ver" "$root\installer\AgosClip.iss"
if ($LASTEXITCODE -ne 0) { exit 1 }
Get-Item "$root\dist\AgosClip-Setup-$ver.exe" | ForEach-Object { "{0}  {1:N1} MB" -f $_.FullName, ($_.Length / 1MB) }
