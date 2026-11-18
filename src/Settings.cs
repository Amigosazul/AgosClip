using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace AgosClip;

public sealed class Settings
{
    /// <summary>Days to keep unpinned history. 0 = keep forever.</summary>
    public int RetentionDays { get; set; } = 30;
    public bool TakeOverWinV { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool PasteAfterSelect { get; set; } = true;
    public bool RecordImages { get; set; } = true;
    /// <summary>Frosted-glass (acrylic) backdrop on Windows 11 22H2+.</summary>
    public bool Glass { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Last version we showed a tray notice for, so each release is announced once.</summary>
    public string? NotifiedVersion { get; set; }

    /// <summary>%APPDATA%\AgosClip, or AGOSCLIP_DATA when set (used by --selftest).</summary>
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("AGOSCLIP_DATA") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgosClip");

    static string FilePath => Path.Combine(DataDir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, true);
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void ApplyAutoStart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
        {
            var exe = Environment.ProcessPath ?? "";
            key.SetValue("AgosClip", $"\"{exe}\" --background");
        }
        else key.DeleteValue("AgosClip", false);
    }
}
