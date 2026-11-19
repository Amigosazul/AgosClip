using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgosClip;

public static class Paster
{
    /// <summary>Puts an item on the clipboard. Returns false if the item's data is gone or the clipboard stayed locked.</summary>
    public static bool SetClipboard(ClipItem item)
    {
        var data = new DataObject();
        if (item.Kind == ClipKind.Text)
        {
            if (item.Text == null) return false;
            data.SetText(item.Text, TextDataFormat.UnicodeText);
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
