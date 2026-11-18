[Console]::OutputEncoding = [Text.Encoding]::UTF8
$dir = 'E:\CloudCLI\tmp\agosclip-install'
# Uses the TestBuild installer (own AppId "AgosClip Test", no taskkill, no registry), so a real
# AgosClip installation on this PC is never replaced, closed or unregistered.
$ver = ([xml](Get-Content -Encoding UTF8 "$PSScriptRoot\..\src\AgosClip.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /Q /DTestBuild "/DAppVersion=$ver" "$PSScriptRoot\..\installer\AgosClip.iss"
if ($LASTEXITCODE -ne 0) { 'test installer build failed'; exit 1 }
$setup = "$PSScriptRoot\..\dist\AgosClip-SetupTest-$ver.exe"
$real = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object DisplayName -eq 'AgosClip 剪贴板'
$realProc = (Get-Process AgosClip -ErrorAction SilentlyContinue | Measure-Object).Count
$realRun = (Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -ErrorAction SilentlyContinue).AgosClip
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', "/DIR=$dir", '/LOG=E:\CloudCLI\tmp\agosclip-setup.log' -PassThru -Wait
"install exit: $($p.ExitCode); exe present: $(Test-Path "$dir\AgosClip.exe")"
$un = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object DisplayName -eq 'AgosClip 安装测试'
"uninstall entry: $($un.DisplayName) / $($un.DisplayVersion)"
"test start menu link: $(Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\AgosClip Test.lnk"); real link still there: $(Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\AgosClip.lnk")"
$env:AGOSCLIP_DATA = 'E:\CloudCLI\tmp\agosclip-install-data'
Remove-Item -Recurse -Force $env:AGOSCLIP_DATA, 'E:\CloudCLI\tmp\agosclip-install-out' -ErrorAction SilentlyContinue
$t = Start-Process "$dir\AgosClip.exe" -ArgumentList '--selftest', 'E:\CloudCLI\tmp\agosclip-install-out' -PassThru -Wait
"installed selftest exit: $($t.ExitCode); $(Get-Content -Encoding UTF8 E:\CloudCLI\tmp\agosclip-install-out\results.txt | Select-String 'PASSED|FAIL')"
Remove-Item Env:\AGOSCLIP_DATA
$u = Start-Process "$dir\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES' -PassThru -Wait
Start-Sleep -Seconds 2
"uninstall exit: $($u.ExitCode); exe still present: $(Test-Path "$dir\AgosClip.exe")"
"test uninstall entry left: $([bool](Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object DisplayName -eq 'AgosClip 安装测试'))"
"test start menu link left: $(Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\AgosClip Test.lnk")"
$real2 = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object DisplayName -eq 'AgosClip 剪贴板'
"real install untouched: record $([bool]$real)->$([bool]$real2) ($($real2.InstallLocation)), processes $realProc->$((Get-Process AgosClip -ErrorAction SilentlyContinue | Measure-Object).Count), run key same: $($realRun -eq (Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -ErrorAction SilentlyContinue).AgosClip)"
Remove-Item -Recurse -Force $dir, 'E:\CloudCLI\tmp\agosclip-install-data', 'E:\CloudCLI\tmp\agosclip-install-out', $setup -ErrorAction SilentlyContinue
