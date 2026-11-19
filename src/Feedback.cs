using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace AgosClip;

/// <summary>
/// Log viewing and one-click feedback: writes a plain-text diagnostics file (no clipboard content,
/// personal paths/names redacted), selects it in Explorer, puts it on the clipboard as a file, and
/// opens a pre-filled GitHub issue so the user just drags/pastes the file in.
/// </summary>
public static class Feedback
{
    public static string LogFile => Path.Combine(Settings.ConfigDir, "agosclip.log");
    public static string OldLogFile => Path.Combine(Settings.ConfigDir, "agosclip.old.log");

    public static void OpenLog()
    {
        if (!File.Exists(LogFile)) App.Log("log opened from settings");
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{LogFile}\"") { UseShellExecute = true });
    }

    public static void OpenLogFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LogFile}\"") { UseShellExecute = true });

    /// <summary>Replaces things that identify the person or machine.</summary>
    public static string Redact(string s)
    {
        var pairs = new List<(string, string)>();
        void Add(string? value, string token)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length >= 2) pairs.Add((value, token));
        }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        Add(Environment.UserName, "<user>");
        Add(Environment.MachineName, "<pc>");
        foreach (var (value, token) in pairs.OrderByDescending(p => p.Item1.Length))
            s = s.Replace(value, token, StringComparison.OrdinalIgnoreCase);
        return s;
    }

    static IEnumerable<string> TailLines(string path, int max)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, Encoding.UTF8);
            var q = new Queue<string>();
            string? line;
            while ((line = r.ReadLine()) != null) { q.Enqueue(line); if (q.Count > max) q.Dequeue(); }
            return q.ToArray();
        }
        catch (Exception ex) { return new[] { "(could not read log: " + ex.Message + ")" }; }
    }

    public static string SystemSummary()
    {
        var sb = new StringBuilder();
        var os = Environment.OSVersion.Version;
        sb.AppendLine($"AgosClip {Updater.Current.ToString(3)}");
        sb.AppendLine($"Windows {os.Major}.{os.Minor}.{os.Build} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        sb.AppendLine($"Glass supported: {Glass.Supported}, active: {Theme.IsGlass}, dark: {Theme.IsDark}");
        var pri = System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
        sb.AppendLine($"Monitors: {System.Windows.Forms.Screen.AllScreens.Length}, primary {pri?.Width}x{pri?.Height}");
        return sb.ToString();
    }

    /// <summary>Builds the diagnostics text file and returns its path.</summary>
    public static string WriteDiagnostics()
    {
        var app = App.Current;
        var sb = new StringBuilder();
        sb.AppendLine("AgosClip 诊断信息 / diagnostics");
        sb.AppendLine("不包含任何剪贴板内容；用户名、电脑名和用户目录已替换。");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        sb.AppendLine("== System ==");
        sb.Append(SystemSummary());
        sb.AppendLine();
        sb.AppendLine("== Settings ==");
        var st = app.Settings;
        sb.AppendLine($"RetentionDays={st.RetentionDays} TakeOverWinV={st.TakeOverWinV} PasteAfterSelect={st.PasteAfterSelect} " +
                      $"RecordImages={st.RecordImages} Glass={st.Glass} StartWithWindows={st.StartWithWindows} AutoCheckUpdates={st.AutoCheckUpdates}");
        sb.AppendLine($"Paused={App.Paused} DataLocation={(st.DataLocation == null ? "(default)" : st.DataLocation)} DataDir={Settings.DataDir}");
        sb.AppendLine($"PanelPosition={(st.PanelX is int x ? $"{x},{st.PanelY}" : "(follow caret)")}");
        sb.AppendLine($"Install dir: {AppContext.BaseDirectory}");
        sb.AppendLine();
        sb.AppendLine("== History statistics (counts only) ==");
        try
        {
            var counts = app.Store.DayCounts();
            sb.AppendLine($"Items: {counts.Values.Sum()} over {counts.Count} days, pinned: {app.Store.PinnedCount()}");
            if (Directory.Exists(Store.ImagesDir))
            {
                var files = new DirectoryInfo(Store.ImagesDir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                sb.AppendLine($"Image files: {files.Count}, {files.Sum(f => f.Length) / 1048576.0:0.#} MB");
            }
            if (File.Exists(Store.DbFile)) sb.AppendLine($"history.db: {new FileInfo(Store.DbFile).Length / 1024} KB");
        }
        catch (Exception ex) { sb.AppendLine("(stats failed: " + ex.Message + ")"); }
        sb.AppendLine();
        sb.AppendLine("== Log (most recent last) ==");
        foreach (var l in TailLines(OldLogFile, 200).Concat(TailLines(LogFile, 600))) sb.AppendLine(l);

        var dir = Path.Combine(Path.GetTempPath(), "AgosClip-Feedback");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"AgosClip-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, Redact(sb.ToString()), new UTF8Encoding(true));
        return path;
    }

    public static string IssueUrl(string fileName)
    {
        var os = Environment.OSVersion.Version;
        var body = $"""
            **发生了什么 / What happened**


            **怎么操作的 / Steps**
            1.
            2.

            **环境 / Environment**
            - AgosClip {Updater.Current.ToString(3)}
            - Windows {os.Major}.{os.Minor}.{os.Build}

            **诊断文件 / Diagnostics**
            请把 `{fileName}` 拖到这个输入框里，或者直接按 Ctrl+V 粘贴（它已经复制到剪贴板，也在资源管理器里选中了）。
            文件里没有剪贴板内容，用户名和路径已替换，上传前可以先打开检查。
            """;
        return $"https://github.com/{Updater.Repo}/issues/new?title={Uri.EscapeDataString("反馈：")}&body={Uri.EscapeDataString(body)}";
    }

    /// <summary>The whole one-click flow. Returns the diagnostics path.</summary>
    public static string Start()
    {
        App.Log($"feedback started (v{Updater.Current.ToString(3)})");
        var path = WriteDiagnostics();
        try
        {
            var files = new StringCollection { path };
            Clipboard.SetFileDropList(files);
        }
        catch (Exception ex) { App.Log("feedback: clipboard failed: " + ex.Message); }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        Process.Start(new ProcessStartInfo(IssueUrl(Path.GetFileName(path))) { UseShellExecute = true });
        return path;
    }
}
