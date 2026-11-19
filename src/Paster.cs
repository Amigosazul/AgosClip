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
        problem = null;
        var data = new DataObject();
        if (item.Kind == ClipKind.Text)
        {
            if (item.Text == null) return false;
            data.SetText(item.Text, TextDataFormat.UnicodeText);
            // rich formats first-class next to plain text: the target app picks the best one it understands
            foreach (var f in formats ?? Array.Empty<ClipFormat>())
            {
                try
                {
                    if (f.IsText) data.SetData(f.Name, System.Text.Encoding.UTF8.GetString(f.Data));
                    else data.SetData(f.Name, new MemoryStream(f.Data));
                }
                catch (Exception ex) { App.Log($"paste: format {f.Name} skipped: {ex.GetType().Name}"); }
            }
        }
        else if (item.Kind == ClipKind.Files)
        {
            var existing = item.Paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (existing.Length == 0) { problem = "这些文件都已经不在原来的位置了"; return false; }
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(existing);
            data.SetFileDropList(list);
            // always "copy": pasting from history must never move the user's original files
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1 /* DROPEFFECT_COPY */)));
            data.SetText(string.Join(Environment.NewLine, existing), TextDataFormat.UnicodeText); // for text editors
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
            data.SetImage(bmp);                       // CF_DIB / CF_BITMAP for most apps
            data.SetData("PNG", new MemoryStream(bytes)); // keeps transparency for apps that read PNG
        }

        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetDataObject(data, true); return true; }
            catch (COMException) { Thread.Sleep(30); }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        return false;
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
