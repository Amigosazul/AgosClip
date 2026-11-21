using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace AgosClip;

/// <summary>A key combination like "Alt+Shift+A" (always at least one of Ctrl/Alt/Shift/Win + a non-modifier key).</summary>
public readonly record struct HotkeyCombo(ModifierKeys Modifiers, Key Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        Key.Snapshot => "PrintScreen",
        Key.Oem3 => "`",
        _ => k.ToString(),
    };

    public static HotkeyCombo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = ModifierKeys.None;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                case "printscreen": key = Key.Snapshot; break;
                case "`": key = Key.Oem3; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (Enum.TryParse<Key>(raw, true, out var k)) key = k;
                    else return null;
                    break;
            }
        }
        var c = new HotkeyCombo(mods, key);
        return c.IsValid ? c : null;
    }

    /// <summary>Needs a real key, and a modifier unless it's a function key / PrintScreen.</summary>
    public bool IsValid =>
        Key != Key.None && !IsModifierKey(Key) &&
        (Modifiers != ModifierKeys.None || Key is >= Key.F1 and <= Key.F24 or Key.Snapshot);

    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}

/// <summary>RegisterHotKey on a message-only window of the UI thread. One instance per hotkey.</summary>
public sealed class GlobalHotkey : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    static int _nextId = 0x5A00;
    readonly int _id = _nextId++;
    readonly HwndSource _wnd;
    bool _registered;

    public event Action? Pressed;
    public HotkeyCombo? Current { get; private set; }

    public GlobalHotkey()
    {
        _wnd = new HwndSource(new HwndSourceParameters("AgosClipHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _wnd.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == WM_HOTKEY && (int)w == _id) { handled = true; Pressed?.Invoke(); }
            return IntPtr.Zero;
        });
    }

    /// <summary>Registers <paramref name="combo"/> (or nothing if null). Returns false if another program owns it.</summary>
    public bool Set(HotkeyCombo? combo)
    {
        Clear();
        if (combo is not { IsValid: true } c) return true;
        uint mods = MOD_NOREPEAT;
        if (c.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (c.Modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (c.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        if (c.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;
        _registered = RegisterHotKey(_wnd.Handle, _id, mods, (uint)KeyInterop.VirtualKeyFromKey(c.Key));
        if (_registered) Current = c;
        else App.Log($"hotkey {c} could not be registered (error {Marshal.GetLastWin32Error()}): in use by another program?");
        return _registered;
    }

    public void Clear()
    {
        if (_registered) UnregisterHotKey(_wnd.Handle, _id);
        _registered = false;
        Current = null;
    }

    public void Dispose()
    {
        Clear();
        _wnd.Dispose();
    }
}
