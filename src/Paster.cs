using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgosClip;

public static class Paster
{
    /// <summary>
    /// Puts an item on the clipboard. Returns false if the item's data is gone or the clipboard stayed locked.
    /// <paramref name="formats"/> are the extra formats stored with a text item (HTML/RTF/spreadsheet).
    /// </summary>
    public static bool SetClipboard(ClipItem item, System.Collections.Generic.IReadOnlyList<ClipFormat>? formats = null)
        => SetClipboard(item, formats, out _);

    public static bool SetClipboard(ClipItem item, System.Collections.Generic.IReadOnlyList<ClipFormat>? formats, out string? problem)
    {
        // Fully rendered Win32 write (see ClipboardWriter): WPF's delayed-rendering SetDataObject could
        // deadlock for ~10 s against anyone reading the clipboard at the same moment.
        problem = null;
        var payload = new ClipboardWriter.Payload();
        if (item.Kind == ClipKind.Text)
        {
            if (item.Text == null) return false;
            payload.Text(item.Text);
            // rich formats next to plain text: the target app picks the best one it understands
            foreach (var f in formats ?? Array.Empty<ClipFormat>())
            {
                if (f.IsText) payload.Bytes8(f.Name, f.Data); // HTML (UTF-8), RTF, CSV: 8-bit text + NUL
                else payload.Add(f.Name, f.Data);              // e.g. Excel's XML Spreadsheet, raw bytes
            }
        }
        else if (item.Kind == ClipKind.Files)
        {
            var existing = item.Paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (existing.Length == 0) { problem = "这些文件都已经不在原来的位置了"; return false; }
            payload.Files(existing);
            // always "copy": pasting from history must never move the user's original files
            payload.Add("Preferred DropEffect", BitConverter.GetBytes(1 /* DROPEFFECT_COPY */));
            payload.Text(string.Join(Environment.NewLine, existing)); // for text editors
            if (existing.Length < item.Paths.Length)
                problem = $"有 {item.Paths.Length - existing.Length} 个文件已经不在原来的位置，只粘贴了 {existing.Length} 个";
        }
        else
        {
            if (item.ImagePath == null) return false;
            var path = Path.Combine(Settings.DataDir, item.ImagePath);
            if (!File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            payload.Dib(bmp);           // CF_DIB for most apps (Windows synthesises CF_BITMAP / CF_DIBV5)
            payload.Add("PNG", bytes);  // keeps transparency for apps that read PNG
        }
        return ClipboardWriter.Write(payload);
    }

    /// <summary>Gives focus back to the target window and sends Ctrl+V to it.</summary>
    public static void PasteInto(IntPtr target, Dispatcher ui)
    {
        if (target == IntPtr.Zero || !Native.IsWindow(target)) return;
        Watchdog.Step("paste: switch to target window");
        Native.ForceForeground(target);
        var timer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Release any modifier the user may still be holding, then Ctrl+V.
            var inputs = new System.Collections.Generic.List<Native.INPUT>();
            foreach (var vk in new[] { Native.VK_LWIN, Native.VK_RWIN, Native.VK_MENU, Native.VK_SHIFT })
                if (Native.IsDown(vk)) inputs.Add(Native.Key((ushort)vk, true));
            inputs.Add(Native.Key((ushort)Native.VK_CONTROL, false));
            inputs.Add(Native.Key((ushort)Native.VK_V, false));
            inputs.Add(Native.Key((ushort)Native.VK_V, true));
            inputs.Add(Native.Key((ushort)Native.VK_CONTROL, true));
            Native.Send(inputs.ToArray());
        };
        timer.Start();
    }
}
