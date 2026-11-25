using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace AgosClip;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DayCard : Bindable
{
    public bool IsPinned { get; init; }
    public DateOnly Date { get; init; }
    public int Count { get; init; }
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";

    static readonly string[] Week = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
    public static string WeekName(DateOnly d) => Week[(int)d.DayOfWeek];

    public static DayCard ForDay(DateOnly d, int count, DateOnly today)
    {
        int diff = today.DayNumber - d.DayNumber;
        string title = diff switch
        {
            0 => "今天",
            1 => "昨天",
            2 => "前天",
            _ => d.Year == today.Year ? $"{d.Month}月{d.Day}日" : $"{d.Year % 100:00}/{d.Month}/{d.Day}",
        };
        string week = diff <= 2 ? $"{d.Month}/{d.Day}" : WeekName(d);
        return new DayCard { Date = d, Count = count, Title = title, Sub = $"{week} · {count} 条" };
    }

    public static DayCard Pinned(int count) => new() { IsPinned = true, Count = count, Title = "固定", Sub = $"{count} 条" };
}

public sealed class ItemVm : Bindable
{
    public ClipItem Item { get; }
    public ItemVm(ClipItem item, bool showDate)
    {
        Item = item;
        var t = item.Time;
        string when = showDate ? t.ToString("M月d日 HH:mm", CultureInfo.InvariantCulture) : t.ToString("HH:mm");
        var parts = new StringBuilder(when);
        if (!string.IsNullOrEmpty(item.SourceApp)) parts.Append("  ·  ").Append(item.SourceApp);
        if (item.Kind == ClipKind.Image) parts.Append("  ·  ").Append(item.Width).Append('×').Append(item.Height);
        else if (item.Kind == ClipKind.Files)
        {
            int missing = item.Paths.Count(p => !File.Exists(p) && !Directory.Exists(p));
            parts.Append("  ·  ").Append(item.Paths.Length).Append(" 项");
            if (missing > 0) parts.Append("  ·  ").Append(missing == item.Paths.Length ? "已不存在" : $"{missing} 项已不存在");
        }
        else if (item.Text != null && item.Text.Length > 300) parts.Append("  ·  ").Append(item.Text.Length).Append(" 字");
        if (Badge != null) parts.Append("  ·  ").Append(Badge);
        if (item.CopyCount > 1) parts.Append("  ·  复制 ").Append(item.CopyCount).Append(" 次");
        Meta = parts.ToString();
    }

    public bool IsImage => Item.Kind == ClipKind.Image;
    public bool IsText => Item.Kind == ClipKind.Text;
    public bool IsFiles => Item.Kind == ClipKind.Files;
    /// <summary>Text with translation turned on; rich text ("富文本"/"表格") is translated with its formatting.</summary>
    public bool CanTranslate => IsText && App.Current?.Settings.EnableTranslate == true;
    public string TranslateTip => Badge != null ? "翻译，保留格式 (Ctrl+T)" : "翻译 (Ctrl+T)";

    /// <summary>"表格" / "富文本" when the item keeps formats beyond plain text.</summary>
    public string? Badge =>
        Item.Formats.Contains("XML Spreadsheet") || (Item.Formats.Contains("HTML Format") && Item.Text?.Contains('\t') == true) ? "表格"
        : Item.Formats.Contains("HTML Format") || Item.Formats.Contains("Rich Text Format") ? "富文本"
        : null;

    /// <summary>Files: "报告.docx" or "报告.docx 等 3 项"; second line lists more names.</summary>
    public string FilesTitle
    {
        get
        {
            var p = Item.Paths;
            if (p.Length == 0) return "";
            var first = Path.GetFileName(p[0].TrimEnd('\\')) is { Length: > 0 } n ? n : p[0];
            return p.Length == 1 ? first : $"{first} 等 {p.Length} 项";
        }
    }
    public string FilesDetail
    {
        get
        {
            var p = Item.Paths;
            if (p.Length == 1) return Path.GetDirectoryName(p[0]) ?? "";
            return string.Join("、", p.Skip(1).Take(4).Select(x => Path.GetFileName(x.TrimEnd('\\')))) + (p.Length > 5 ? " …" : "");
        }
    }
    public string FilesGlyph => Item.Paths.Length == 1 && Directory.Exists(Item.Paths[0]) ? "\uE8B7" : "\uE8A5"; // folder / document

    // Image preview size: fit in 340×110 without upscaling
    double Fit => Item.Width <= 0 || Item.Height <= 0 ? 1 : Math.Min(1.0, Math.Min(340.0 / Item.Width, 110.0 / Item.Height));
    public double DisplayWidth => Item.Width > 0 ? Math.Max(24, Math.Round(Item.Width * Fit)) : 120;
    public double DisplayHeight => Item.Height > 0 ? Math.Max(24, Math.Round(Item.Height * Fit)) : 80;
    public string Meta { get; }

    public bool Pinned
    {
        get => Item.Pinned;
        set { Item.Pinned = value; Raise(); Raise(nameof(PinGlyph)); }
    }
    bool _dragging;
    /// <summary>Issue #13: this card is being dragged to a new position.</summary>
    public bool Dragging { get => _dragging; set { _dragging = value; Raise(); } }

    public string PinGlyph =>Item.Pinned ? "" : ""; // Unpin / Pin

    string? _preview;
    public string Preview => _preview ??= MakePreview(Item.Text);

    static string MakePreview(string? text)
    {
        if (text == null) return "";
        var s = text.Length > 600 ? text[..600] : text;
        // collapse blank lines and trailing spaces so short previews stay readable
        var sb = new StringBuilder();
        int blank = 0;
        foreach (var raw in s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd().Replace('\t', ' ');
            if (line.Length == 0) { if (sb.Length > 0 && ++blank == 1) sb.Append('\n'); continue; }
            blank = 0;
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString().Trim();
    }

    BitmapImage? _thumb;
    bool _thumbTried;
    public BitmapImage? Thumb
    {
        get
        {
            if (_thumbTried) return _thumb;
            _thumbTried = true;
            var rel = Item.ThumbPath ?? Item.ImagePath;
            if (rel == null) return null;
            Watchdog.Step("load thumbnail " + rel);
            try
            {
                var path = Path.Combine(Settings.DataDir, rel);
                if (!File.Exists(path)) return null;
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                b.UriSource = new Uri(path);
                b.EndInit();
                b.Freeze();
                _thumb = b;
            }
            catch { }
            return _thumb;
        }
    }
}
