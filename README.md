# AgosClip

A Windows clipboard history that replaces Win+V. It records text and images, keeps one entry per distinct content, lets you find any day's history by clicking date cards or a calendar, and deletes unpinned items after N days (default 30). Everything stays on the local machine.

- `src/`: the C# WPF (.NET 8) app
- `installer/`: the Inno Setup script and Chinese installer strings
- `tools/build.ps1`: self-contained publish, then `dist\AgosClip-Setup-<ver>.exe`
- `tools/selftest.ps1`: builds, then runs `AgosClip.exe --selftest` against a scratch data folder (`AGOSCLIP_DATA`). It covers storage, clipboard capture and paste-back, and renders the UI to PNG.
- `tools/installtest.ps1`: silent install, self-test the installed copy, then uninstall. It uses a separate test installer identity, so a real installation is never touched.
- `tools/release.sh`: publishes a GitHub release with the installer and its `.sha256`, which the in-app updater requires
- `tools/make_icon.py`: regenerates `src/Assets/AgosClip.ico`

Build on Windows with the .NET 8 SDK and Inno Setup 6 (per-user install).

Data lives in `%APPDATA%\AgosClip`: `history.db`, `images\yyyy-MM-dd\`, `settings.json` and `agosclip.log`.
Win+V is captured with a low-level keyboard hook, and no registry or Explorer changes are made. When the hook is removed, by turning the setting off, quitting or uninstalling, the system Win+V comes back.

## Online upgrade

设置 → 更新 → 检查更新 reads the latest GitHub release of `Amigosazul/AgosClip`. The release must include `AgosClip-Setup-x.y.z.exe` and `AgosClip-Setup-x.y.z.exe.sha256`. After the download is verified, the installer runs with `/SILENT /RESTARTAPP`. It keeps the previous install folder and restarts the app.
