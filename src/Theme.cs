using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AgosClip;

/// <summary>Follows the Windows light/dark app setting; picks translucent brushes when glass is active.</summary>
public static class Theme
{
    public static bool IsDark { get; private set; }
    public static bool IsGlass { get; private set; }

    static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Apply(bool? forceDark = null, bool? forceGlass = null)
    {
        bool dark = forceDark ?? SystemIsDark();
        bool glass = forceGlass ?? Glass.Active;
        IsDark = dark;
        IsGlass = glass;
        var r = Application.Current.Resources;
        void Set(string key, string hex) => r[key] = Freeze(new SolidColorBrush(C(hex)));

        // Shared
        Set("Fg", dark ? "#F5F5F7" : "#1D1D1F");
        Set("SubFg", dark ? "#A1A1A6" : "#6E6E73");
        Set("Accent", dark ? "#0A84FF" : "#007AFF");
        Set("AccentFg", "#FFFFFF");
        Set("SegAccent", dark ? "#8CC8FF" : "#007AFF");
        Set("Dim", dark ? "#33000000" : "#1A000000");
        Set("WindowBg", dark ? "#1E1E20" : "#F2F2F5");

        if (glass)
        {
            Set("PanelBg", dark ? "#26101012" : "#40FFFFFF");
            Set("Hairline", dark ? "#24FFFFFF" : "#99FFFFFF");
            Set("Track", dark ? "#1FFFFFFF" : "#12000000");
            Set("SegSelected", dark ? "#3DFFFFFF" : "#F2FFFFFF");
            Set("CardBg", dark ? "#17FFFFFF" : "#A6FFFFFF");
            Set("CardHover", dark ? "#24FFFFFF" : "#D9FFFFFF");
            Set("CardBorder", dark ? "#1AFFFFFF" : "#12000000");
            Set("SelBg", dark ? "#400A84FF" : "#29007AFF");
            Set("InputBg", dark ? "#1AFFFFFF" : "#12000000");
            Set("ImageBg", dark ? "#14FFFFFF" : "#0D000000");
            Set("OverlayBg", dark ? "#FF2A2A2D" : "#FFFAFAFC");
            Set("KeyBg", dark ? "#1AFFFFFF" : "#B3FFFFFF");
        }
        else
        {
            Set("PanelBg", dark ? "#1E1E20" : "#F2F2F5");
            Set("Hairline", dark ? "#3A3A3C" : "#D1D1D6");
            Set("Track", dark ? "#2C2C2E" : "#E3E3E8");
            Set("SegSelected", dark ? "#48484A" : "#FFFFFF");
            Set("CardBg", dark ? "#2C2C2E" : "#FFFFFF");
            Set("CardHover", dark ? "#323234" : "#F9F9FB");
            Set("CardBorder", dark ? "#3A3A3C" : "#E1E1E6");
            Set("SelBg", dark ? "#1F3550" : "#E5F0FF");
            Set("InputBg", dark ? "#2C2C2E" : "#E3E3E8");
            Set("ImageBg", dark ? "#252527" : "#EDEDF0");
            Set("OverlayBg", dark ? "#2A2A2D" : "#FAFAFC");
            Set("KeyBg", dark ? "#2C2C2E" : "#FFFFFF");
        }

        // Soft light falling on the top of the panel
        var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        sheen.GradientStops.Add(new GradientStop(C(glass ? (dark ? "#14FFFFFF" : "#66FFFFFF") : "#00FFFFFF"), 0));
        sheen.GradientStops.Add(new GradientStop(C("#00FFFFFF"), 1));
        r["Sheen"] = Freeze(sheen);
    }

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>Win11 rounded corners + dark/light frame for a window handle (no-op on Win10).</summary>
    public static void StyleWindow(IntPtr hwnd, bool round = true)
    {
        int dark = IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, 20, ref dark, 4);          // DWMWA_USE_IMMERSIVE_DARK_MODE (also tints acrylic)
        if (round)
        {
            int pref = 2;                                              // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(hwnd, 33, ref pref, 4);       // DWMWA_WINDOW_CORNER_PREFERENCE
        }
    }
}
