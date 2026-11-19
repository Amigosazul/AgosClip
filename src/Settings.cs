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
    /// <summary>Upper bound for agosclip.log + agosclip.old.log together.</summary>
    public int LogMaxMB { get; set; } = 5;

    /// <summary>Custom folder for history.db + images; null = the default (ConfigDir).</summary>
    public string? DataLocation { get; set; }

    static readonly string? ConfigOverride = Environment.GetEnvironmentVariable("AGOSCLIP_DATA");
    internal static bool UsesDefaultConfigDir => string.IsNullOrWhiteSpace(ConfigOverride);

    /// <summary>Where settings.json and the log live: %APPDATA%\AgosClip, or AGOSCLIP_DATA for local testing.</summary>
    public static string ConfigDir { get; } = UsesDefaultConfigDir
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgosClip") : ConfigOverride!;

    /// <summary>Where history.db and images\ live. Defaults to ConfigDir; the user can move it in settings.</summary>
    public static string DataDir { get; private set; } = ConfigDir;

    public static bool IsDefaultLocation(string dir) =>
        string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(ConfigDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Points DataDir at the saved location. Returns false (and stays on the default) if that folder is missing.</summary>
    public bool ApplyDataLocation()
    {
        if (string.IsNullOrWhiteSpace(DataLocation) || IsDefaultLocation(DataLocation)) { DataDir = ConfigDir; return true; }
        if (!Directory.Exists(DataLocation)) { DataDir = ConfigDir; return false; }
        DataDir = Path.GetFullPath(DataLocation);
        return true;
    }

    /// <summary>Switches DataDir (and the saved setting) to a folder. Callers move the files first.</summary>
    public void SetDataLocation(string dir)
    {
        DataLocation = IsDefaultLocation(dir) ? null : Path.GetFullPath(dir);
        ApplyDataLocation();
        Save();
        RememberDataDir();
    }

    /// <summary>Lets the uninstaller find a custom data folder.</summary>
    public static void RememberDataDir()
    {
        if (Environment.GetEnvironmentVariable("AGOSCLIP_DATA") is { Length: > 0 }) return; // self-test: never touch the real registry
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\AgosClip");
            k.SetValue("DataDir", DataDir);
        }
        catch { }
    }

    static string FilePath => Path.Combine(ConfigDir, "settings.json");
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
        Directory.CreateDirectory(ConfigDir);
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
