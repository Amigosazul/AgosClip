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
    /// <summary>Issue #5: the full-width date-card strip. Off = compact title row with ‹ › and the calendar.</summary>
    public bool ShowDateCards { get; set; }
    /// <summary>Screenshot + pin feature and its global hotkey (empty = no hotkey, tray menu only).</summary>
    public bool EnableScreenshot { get; set; } = true;
    public string ScreenshotHotkey { get; set; } = "Alt+Shift+A";
    /// <summary>Screen recording (region → MP4). Off until the user turns it on in settings.</summary>
    public bool EnableRecording { get; set; }
    /// <summary>Starts a recording; pressed again while recording it stops and saves.</summary>
    public string RecordHotkey { get; set; } = "Alt+Shift+R";
    /// <summary>Where recordings go; null = Videos\AgosClip.</summary>
    public string? RecordFolder { get; set; }
    public int RecordFps { get; set; } = 30;
    public bool RecordCursor { get; set; } = true;
    /// <summary>Sound the computer plays (WASAPI loopback of the default output device).</summary>
    public bool RecordSystemAudio { get; set; } = true;
    /// <summary>The default microphone, mixed with the system sound.</summary>
    public bool RecordMic { get; set; }
    /// <summary>After saving, put the .mp4 on the clipboard as a file so it can be pasted into a chat.</summary>
    public bool RecordCopyFile { get; set; } = true;

    /// <summary>Translation buttons (text and pictures). Sends the text to an online service only when clicked.</summary>
    public bool EnableTranslate { get; set; } = true;
    /// <summary>Translator target language code, or "auto" (Chinese → English, everything else → Chinese).</summary>
    public string TranslateTarget { get; set; } = "auto";
    /// <summary>Put the recognised text on the clipboard as soon as OCR finishes.</summary>
    public bool OcrAutoCopy { get; set; }

    public static string DefaultRecordFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AgosClip");
    public string RecordDir => string.IsNullOrWhiteSpace(RecordFolder) ? DefaultRecordFolder : RecordFolder!;
    /// <summary>Issue #11: panel size in DIP after the user resized it; 0 = the default 400 × 580.</summary>
    public double PanelWidth { get; set; }
    public double PanelHeight { get; set; }

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
