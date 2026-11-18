using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace AgosClip;

/// <summary>
/// Frosted-glass backdrop via the Windows 11 (22H2+) DWM system backdrop. The blur is done by the
/// compositor, so it reflects whatever is really behind the window. Falls back to solid colours on
/// older Windows, when the user turned off "transparency effects", or when the setting is off.
/// </summary>
public static class Glass
{
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int Acrylic = 3, Mica = 2, None = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int l, r, t, b; }

    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

    public static bool Supported => Environment.OSVersion.Version.Build >= 22621;

    static bool SystemTransparency()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch { return true; }
    }

    /// <summary>Whether glass brushes/backdrop should be used right now.</summary>
    public static bool Active => Supported && App.Current.Settings.Glass && SystemTransparency();

    /// <summary>Turns the backdrop on or off for a window whose handle exists.</summary>
    public static void Apply(Window w, int kind = Acrylic)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        Theme.StyleWindow(hwnd);
        bool on = Active;
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } ct)
            ct.BackgroundColor = on ? Colors.Transparent : ((SolidColorBrush)Application.Current.Resources["WindowBg"]).Color;
        if (on)
        {
            w.Background = Brushes.Transparent;
            var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
        }
        else w.SetResourceReference(Window.BackgroundProperty, "WindowBg");
        if (Supported)
        {
            int t = on ? kind : None;
            Native.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref t, 4);
        }
    }
}
