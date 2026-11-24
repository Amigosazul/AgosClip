using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace AgosClip;

/// <summary>
/// What the hook does with one key event. Pure state machine, so the whole Win+V sequence can be tested
/// without a real keyboard.
///
/// Windows opens the Start menu when the Win key goes up without any other key having been seen while it
/// was down. We swallow V, so on its own the shell sees Win down … Win up = "Win tapped" → Start menu.
/// The cure (the same as AutoHotkey's "menu mask key"): hold back the real Win-up and replace it with
/// dummy-key down, dummy-key up, Win up, injected in one SendInput call so nothing can get between them.
/// The older approach — injecting the dummy key asynchronously right after V — lost the race whenever Win
/// was released quickly, which is why the Start menu still popped up now and then.
/// </summary>
public sealed class WinVKeyLogic
{
    public enum Act
    {
        /// <summary>Let the event through.</summary>
        Pass,
        /// <summary>Swallow it.</summary>
        Swallow,
        /// <summary>Swallow Win+V and open the panel.</summary>
        SwallowAndOpen,
        /// <summary>Swallow this Win-up and inject dummy down/up + Win up instead.</summary>
        ReplaceWinUp,
    }

    bool _swallowingV;   // V-down was swallowed: swallow its V-up too
    bool _winHeld;       // physical Win is down (Win auto-repeats while held)
    bool _maskWin;       // this Win press opened the panel: its Win-up must be masked

    public void Reset() => _swallowingV = _winHeld = _maskWin = false;

    /// <param name="injectedByUs">Events we injected ourselves (dummy key, replacement Win-up) always pass.</param>
    /// <param name="winDown">Win is down (state before this event).</param>
    /// <param name="otherMods">Ctrl, Alt or Shift is down.</param>
    /// <param name="canInject">SendInput will reach the foreground window (not blocked by UIPI).</param>
    public Act Decide(int vk, bool down, bool injectedByUs, bool winDown, bool otherMods, bool canInject)
    {
        if (injectedByUs) return Act.Pass;
        bool isWin = vk == Native.VK_LWIN || vk == Native.VK_RWIN;
        if (isWin)
        {
            if (down)
            {
                if (!_winHeld) { _winHeld = true; _maskWin = false; } // a new press (not auto-repeat)
                return Act.Pass;
            }
            _winHeld = false;
            if (!_maskWin) return Act.Pass;
            _maskWin = false;
            // If the injection would be blocked (an elevated window is in front), swallowing the real Win-up
            // would leave Win stuck down — let it through; at worst the Start menu opens.
            return canInject ? Act.ReplaceWinUp : Act.Pass;
        }
        if (vk == Native.VK_V)
        {
            if (down && winDown && !otherMods)
            {
                _maskWin = true;
                if (_swallowingV) return Act.Swallow; // auto-repeat: the panel is already opening
                _swallowingV = true;
                return Act.SwallowAndOpen;
            }
            if (!down && _swallowingV)
            {
                _swallowingV = false;
                return Act.Swallow;
            }
        }
        return Act.Pass;
    }
}

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
    readonly WinVKeyLogic _logic = new();
    IntPtr _hook;

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
        _logic.Reset();
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
            int vk = (int)k.vkCode;
            if (vk == Native.VK_V || vk == Native.VK_LWIN || vk == Native.VK_RWIN)
            {
                int msg = (int)wParam;
                bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool isWinUp = !down && vk != Native.VK_V;
                var act = _logic.Decide(vk, down,
                    injectedByUs: k.dwExtraInfo == Native.OurInjectionTag,
                    winDown: Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN),
                    otherMods: Native.IsDown(Native.VK_CONTROL) || Native.IsDown(Native.VK_MENU) || Native.IsDown(Native.VK_SHIFT),
                    canInject: !isWinUp || InputReachesForeground());
                switch (act)
                {
                    case WinVKeyLogic.Act.SwallowAndOpen:
                        // Early mask too: covers Win being held long after V (the Win-up mask below is the guarantee).
                        _hookThread.BeginInvoke(() => Native.Send(Native.Key(Native.VK_DUMMY, false), Native.Key(Native.VK_DUMMY, true)));
                        var target = Native.GetForegroundWindow();
                        _ui.BeginInvoke(() => Pressed?.Invoke(target));
                        return new IntPtr(1);
                    case WinVKeyLogic.Act.Swallow:
                        return new IntPtr(1);
                    case WinVKeyLogic.Act.ReplaceWinUp:
                        // In order, in one call: dummy down, dummy up, then the Win-up we just held back.
                        uint sent = Native.Send(Native.Key(Native.VK_DUMMY, false), Native.Key(Native.VK_DUMMY, true), Native.WinKey((ushort)vk, up: true));
                        if (sent == 3) return new IntPtr(1);
                        App.Log($"Win-up mask: SendInput sent {sent}/3 (error {Marshal.GetLastWin32Error()}), letting the real Win-up through");
                        break;
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static readonly bool SelfElevated = IsElevated(Process.GetCurrentProcess().Id) ?? false;

    /// <summary>
    /// SendInput from a normal process is dropped by Windows (UIPI) when an elevated window has the focus.
    /// Unknown counts as "no", so a real Win-up is never swallowed without a working replacement.
    /// </summary>
    static bool InputReachesForeground()
    {
        if (SelfElevated) return true;
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return true;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0) return true;
        if (pid == (uint)Environment.ProcessId) return true;
        return IsElevated((int)pid) == false;
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);

    /// <summary>Null if it can't be told (typically: the process is elevated and we aren't).</summary>
    static bool? IsElevated(int pid)
    {
        IntPtr p = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid), t = IntPtr.Zero;
        if (p == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(p, 0x0008 /* TOKEN_QUERY */, out t)) return null;
            return GetTokenInformation(t, 20 /* TokenElevation */, out int elevated, 4, out _) ? elevated != 0 : null;
        }
        finally
        {
            if (t != IntPtr.Zero) CloseHandle(t);
            CloseHandle(p);
        }
    }

    public void Dispose()
    {
        try { Disable(); _hookThread.InvokeShutdown(); } catch { }
    }
}
