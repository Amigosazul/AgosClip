using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgosClip;

/// <param name="Tag">GitHub release tag that holds the package (a pre-release, so it never becomes "latest").</param>
/// <param name="Sha256">Compiled in: a mirror can't hand us a different file.</param>
public sealed record PluginInfo(string Id, string Name, string Description, string Tag, string FileName, long Size,
                                string Sha256, string[] Files)
{
    public string Url => $"https://github.com/{Updater.Repo}/releases/download/{Tag}/{FileName}";
}

/// <summary>
/// Optional downloads that keep the installer small. A plugin is data only (models, native DLLs); the code
/// that uses it ships in the app. Each install goes to its own folder (plugins\&lt;id&gt;\&lt;stamp&gt;) and
/// plugins\&lt;id&gt;\current.txt names the active one, so a reinstall never has to overwrite a DLL that is
/// loaded right now; old folders are removed at the next start.
/// </summary>
public static class Plugins
{
    public static readonly PluginInfo Ocr = new(
        "ocr", "文字识别",
        "基于 ONNX Runtime 和 PaddleOCR（RapidOCR）的离线文字识别，中英文混排效果好。截图、钉图、图片记录都能识别；也用于图片翻译。",
        "plugin-ocr-1", "AgosClip-OCR-1.zip", 19089376,
        "91111b57a1cf8ae2a84a4a417f52dd1111c6b979fadf500ec7fab1ea1243f117",
        new[] { "onnxruntime.dll", "det.onnx", "rec.onnx" });

    public static string Root => Path.Combine(Settings.ConfigDir, "plugins");
    static string Home(PluginInfo p) => Path.Combine(Root, p.Id);
    static string Pointer(PluginInfo p) => Path.Combine(Home(p), "current.txt");

    /// <summary>Raised on the UI thread after an install or uninstall.</summary>
    public static event Action? Changed;

    public static bool HasFiles(PluginInfo p, string dir) => p.Files.All(f => File.Exists(Path.Combine(dir, f)));

    /// <summary>The active install folder, or null when the plugin isn't installed.</summary>
    public static string? ActiveDir(PluginInfo p)
    {
        // self-test: point straight at an unpacked package
        if (p == Ocr && Environment.GetEnvironmentVariable("AGOSCLIP_OCR_DIR") is { Length: > 0 } env) return env;
        try
        {
            if (!File.Exists(Pointer(p))) return null;
            var name = File.ReadAllText(Pointer(p)).Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var dir = Path.Combine(Home(p), name);
            return HasFiles(p, dir) ? dir : null;
        }
        catch { return null; }
    }

    public static bool IsInstalled(PluginInfo p) => ActiveDir(p) != null;

    /// <summary>Downloads (multi-source, resumable, SHA-256 checked), unpacks and activates the plugin.</summary>
    public static async Task InstallAsync(PluginInfo p, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var mirrors = App.Current?.LatestUpdate?.Mirrors ?? Updater.DefaultMirrors;
        var info = new UpdateInfo(new Version(1, 0, 0), "", p.FileName, p.Sha256, p.Size,
            $"https://github.com/{Updater.Repo}/releases/tag/{p.Tag}", mirrors, p.Url);
        var downloads = Path.Combine(Root, ".download");
        var zip = await Updater.DownloadAsync(info, progress, ct, downloads);
        progress?.Report(new DownloadProgress(1, "", 0, "正在解压…"));

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var target = Path.Combine(Home(p), stamp);
        await Task.Run(() =>
        {
            var tmp = target + ".part";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(zip, tmp); // refuses entries that would land outside tmp
            if (!HasFiles(p, tmp)) throw new InvalidDataException("插件包里缺少文件");
            Directory.Move(tmp, target);
            File.WriteAllText(Pointer(p), stamp);
            try { File.Delete(zip); } catch { }
        }, ct);
        if (p == Ocr) RapidOcr.Unload(); // next use loads the new models
        App.Log($"plugin {p.Id}: installed to {target}");
        Changed?.Invoke();
    }

    /// <summary>Deactivates the plugin and deletes what it can; files still in use go at the next start.</summary>
    public static void Uninstall(PluginInfo p)
    {
        if (p == Ocr) RapidOcr.Unload();
        try { File.Delete(Pointer(p)); } catch { }
        Sweep(p);
        App.Log($"plugin {p.Id}: uninstalled");
        Changed?.Invoke();
    }

    /// <summary>Removes inactive install folders and leftover downloads (called at start-up and after uninstall).</summary>
    public static void Sweep()
    {
        Sweep(Ocr);
        try
        {
            var dl = Path.Combine(Root, ".download");
            // keep partial downloads for a day so a retry can resume them
            if (Directory.Exists(dl))
                foreach (var f in new DirectoryInfo(dl).EnumerateFiles().Where(f => f.LastWriteTime < DateTime.Now.AddDays(-1)))
                    try { f.Delete(); } catch { }
        }
        catch { }
    }

    static void Sweep(PluginInfo p)
    {
        try
        {
            if (!Directory.Exists(Home(p))) return;
            var active = ActiveDir(p);
            foreach (var d in Directory.GetDirectories(Home(p)))
            {
                if (active != null && string.Equals(Path.GetFullPath(d), Path.GetFullPath(active), StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(d, true); } catch { /* DLL still loaded: next start */ }
            }
        }
        catch (Exception ex) { App.Log($"plugin {p.Id}: sweep failed: {ex.Message}"); }
    }

    /// <summary>Disk use of the installed plugin, for the settings page.</summary>
    public static long InstalledSize(PluginInfo p)
    {
        try { return ActiveDir(p) is { } d ? new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
        catch { return 0; }
    }
}
