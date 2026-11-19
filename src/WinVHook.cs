using System;
using System.Runtime.InteropServices;
using System.Threading;
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
    readonly Dispatcher _hookThread;
    IntPtr _hook;
    bool _swallowingV;

    public event Action<IntPtr>? Pressed;

    /// <summary>
    /// The hook lives on its own thread with its own message loop. Windows silently drops a low-level
    /// hook whose thread doesn't answer in time, so it must never share the (sometimes busy) UI thread.
    /// </summary>
    public WinVHook(Dispatcher ui)
    {
        _ui = ui;
        _proc = Callback;
        Dispatcher? d = null;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            d = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "winv-hook" };
        t.Start();
        ready.Wait();
        _hookThread = d!;
    }

    public bool Enabled => _hook != IntPtr.Zero;

    public void Enable() => _hookThread.Invoke(() =>
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) App.Log("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
    });

    public void Disable() => _hookThread.Invoke(() =>
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    });

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
                        // A harmless key between Win-down and Win-up keeps the Start menu closed.
                        // Sent from the hook thread right after this callback, so it works even if the UI is busy.
                        _hookThread.BeginInvoke(() => Native.Send(Native.Key(Native.VK_DUMMY, false), Native.Key(Native.VK_DUMMY, true)));
                        var target = Native.GetForegroundWindow();
                        _ui.BeginInvoke(() => Pressed?.Invoke(target));
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

    public void Dispose()
    {
        try { Disable(); _hookThread.InvokeShutdown(); } catch { }
    }
}
