using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AgosClip;

/// <summary>
/// Low-level keyboard hook that swallows Win+V before Windows sees it, so the system clipboard
/// panel never opens and ours does instead. Nothing in the registry is changed; when the hook is
/// removed (setting off, app exit, uninstall) the system Win+V works again immediately.
/// </summary>
public sealed class WinVHook : IDisposable
{
    readonly Native.LowLevelKeyboardProc _proc; // keep delegate alive
    readonly Dispatcher _ui;
    IntPtr _hook;
    bool _swallowingV;

    public event Action? Pressed;

    public WinVHook(Dispatcher ui)
    {
        _ui = ui;
        _proc = Callback;
    }

    public bool Enabled => _hook != IntPtr.Zero;

    public void Enable()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) App.Log("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
    }

    public void Disable()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;

            if (k.vkCode == Native.VK_V && k.dwExtraInfo != Native.OurInjectionTag)
            {
                if (down && (Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN))
                         && !Native.IsDown(Native.VK_CONTROL) && !Native.IsDown(Native.VK_MENU) && !Native.IsDown(Native.VK_SHIFT))
                {
                    if (!_swallowingV) // ignore auto-repeat
                    {
                        _swallowingV = true;
                        _ui.BeginInvoke(() =>
                        {
                            // A harmless key between Win-down and Win-up keeps the Start menu closed.
                            Native.Send(Native.Key(Native.VK_DUMMY, false), Native.Key(Native.VK_DUMMY, true));
                            Pressed?.Invoke();
                        });
                    }
                    return new IntPtr(1);
                }
                if (up && _swallowingV)
                {
                    _swallowingV = false;
                    return new IntPtr(1);
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Disable();
}
