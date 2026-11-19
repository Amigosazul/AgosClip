<div align="center">

<img src="src/Assets/AgosClip-256.png" width="72" alt="AgosClip">

# AgosClip

**替代 Win+V 的 Windows 剪贴板历史**：文字和图片都能记，点日期卡片就能找到某一天复制过的内容，数据只存在你自己的电脑上。

[下载最新版](https://github.com/Amigosazul/AgosClip/releases/latest) · [反馈问题](https://github.com/Amigosazul/AgosClip/issues/new)

</div>

## 功能

- **接管 Win+V**：按 Win+V 弹出 AgosClip，选中一条就直接粘贴回原来的窗口。关掉这个选项、退出或卸载后，系统自带的 Win+V 会恢复。
- **按日期查找**：面板顶部是日期卡片（固定 · 今天 · 昨天 · 11月16日 …），点哪天就看哪天的记录。📅 按钮会打开月历，有记录的日期下面有圆点。
- **文字和图片**：截图、复制的图片都会记录，带缩略图。同样的内容只保留一条，再复制一次会把它移到最新。
- **搜索**：可以搜全部历史，也可以按来源程序搜（比如“微信”）。
- **固定**：
  - 单条记录可以固定，固定的不会被自动清理。
  - 整个面板可以 📌 固定在桌面上，点别处也不关闭，方便一边看一边粘贴。
- **智能定位**：输入框有焦点时在输入框附近打开，否则在鼠标附近打开；面板打开后也可以拖动。
- **毛玻璃界面**：Windows 11 上背景是半透明的毛玻璃，浅色、深色跟随系统。
- **隐私**：
  - KeePass、1Password 这类密码软件标记为“不进入剪贴板历史”的内容会跳过。
  - 不联网，没有账号；只有检查更新时会访问 GitHub。

### 快捷键

| 按键 | 作用 |
|---|---|
| Win+V | 打开 / 关闭面板 |
| ↑ ↓ | 选择 |
| Enter / 单击 | 粘贴 |
| ← → | 切换日期 |
| Del | 删除这一条 |
| Ctrl+P | 固定 / 取消固定这一条 |
| Esc | 清空搜索 / 关闭 |

## 安装

从 [Releases](https://github.com/Amigosazul/AgosClip/releases/latest) 下载 `AgosClip-Setup-x.y.z.exe`，双击安装。

- 不需要管理员权限，也不用预装 .NET。默认装到 `%LOCALAPPDATA%\Programs\AgosClip`，安装时可以换位置，升级时会沿用之前的位置。
- 安装包没有代码签名，第一次运行 Windows 会弹出 SmartScreen 提示，点“更多信息 → 仍要运行”。
- 国内直接从 GitHub 下载很慢的话，可以用每个版本发布说明里的镜像链接。
- 支持 Windows 10 1809 及以上、Windows 11，只有 64 位版本。

## 设置

托盘图标右键 → 设置：

| 页面 | 内容 |
|---|---|
| 通用 | 用 Win+V 打开、选中后自动粘贴、开机自动启动、毛玻璃效果 |
| 记录 | 保留天数（1 天 ~ 永久，默认 30 天）、记录图片、清空历史 |
| 存储与日志 | 数据位置（可以搬到其他盘）、空间占用、日志大小上限（1/5/10/50 MB） |
| 更新 | 检查更新、一键升级、每天自动检查 |
| 关于与反馈 | 版本、一键反馈 |

## 数据存在哪里

| 内容 | 位置 |
|---|---|
| 历史记录 `history.db`、图片 `images\`、日志 `agosclip.log` | 数据位置：默认 `%APPDATA%\AgosClip`，可以在“存储与日志”里改 |
| 设置 `settings.json`（几 KB） | 固定放在 `%APPDATA%\AgosClip` |

更改数据位置时，会先把历史、图片和日志复制过去，确认完整后才删除原来的。卸载时可以选择保留或删除记录。

## 在线升级

设置 → 更新 → 检查更新。专门为国内网络做了处理：

- **查版本**：先用 GitHub API；连不上时，改用 jsDelivr 获取仓库里的 [`latest.json`](latest.json)。
- **下载**：GitHub 直连再加几个 GitHub 镜像，先测速；安装包分成 1 MB 的块，同时从多条线路下载。慢或中断的线路会被自动换掉，下载中断后再点一次会接着下。实测国内不开代理时，50 MB 从 6 分钟降到 46 秒。
- **校验**：SHA-256 只从 GitHub API（asset digest）或 `latest.json` 获取，不信任镜像。校验通过后静默安装，装好后自动重新打开。

## 反馈问题

设置 → 关于与反馈 → 一键反馈，或者托盘菜单 → 反馈问题…

1. 生成一个诊断文件。里面有版本、系统信息、设置、记录条数和最近的日志，**没有任何复制的内容**；用户名、电脑名和用户目录会被替换。
2. 在资源管理器里选中这个文件，同时把它复制到剪贴板。
3. 打开预先填好的 GitHub 新建问题页面，把文件拖进去或按 Ctrl+V，写几句发生了什么，再提交。

日志只记录程序做了什么，比如来源程序、剪贴板格式、为什么跳过、界面是否卡顿，不记录内容。

## 开发

C# / WPF / .NET 8，安装包用 Inno Setup 6 打包。

```
src/            应用源码
  App.xaml.cs           启动、托盘、单实例、日志
  ClipboardMonitor.cs   剪贴板监听（独立 STA 线程）
  WinVHook.cs           Win+V 低级键盘钩子（独立线程）
  PanelWindow.*         弹出面板
  SettingsWindow.*      设置窗口
  Store.cs              SQLite 存储
  DataMover.cs          搬移数据位置
  Updater.cs            在线升级（多线路分块下载）
  Feedback.cs           诊断文件和一键反馈
  SelfTest.cs           自测（--selftest）
installer/      Inno Setup 脚本和中文界面文字
tools/          构建、自测、安装测试、发布脚本
latest.json     最新版本信息（发布脚本自动维护）
```

在 Windows 上：

```powershell
tools\selftest.ps1     # 构建，在独立临时数据目录里跑完整自测（会测试系统剪贴板）并生成截图
tools\selftest.ps1 -AnimationOnly  # 只验证窗口动效、交互和主题，不改系统剪贴板
# dotnet 不在 PATH 时可加 -Dotnet 'D:\Data\dotnet\dotnet.exe'
tools\build.ps1        # 打包独立运行的程序，生成 dist\AgosClip-Setup-<ver>.exe
tools\installtest.ps1  # 用单独的测试安装标识做静默安装、自测、卸载，不影响本机已安装的版本
```

发布（需要已登录的 `gh`）：先改 `src/AgosClip.csproj` 里的 `<Version>`，构建出安装包后运行

```bash
tools/release.sh AgosClip-Setup-x.y.z.exe notes.md
```

它会创建 GitHub Release（安装包加 `.sha256`），更新并推送 `latest.json`，再刷新 jsDelivr 缓存。要换掉失效的镜像，改 `latest.json` 里的 `mirrors`，推送后刷新 jsDelivr 缓存即可，不用发新版本。
