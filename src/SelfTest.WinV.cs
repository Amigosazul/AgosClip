using System.Linq;

namespace AgosClip;

internal static partial class SelfTest
{
    /// <summary>Win+V key sequences through the hook's decision logic (the real hook needs a desktop).</summary>
    static void WinVKeyTests()
    {
        const int W = Native.VK_LWIN, V = Native.VK_V;
        string Run(params (int Vk, bool Down, bool WinDown, bool Mods, bool CanInject, bool Injected)[] keys)
        {
            var l = new WinVKeyLogic();
            return string.Join(",", keys.Select(k => l.Decide(k.Vk, k.Down, k.Injected, k.WinDown, k.Mods, k.CanInject)));
        }
        (int, bool, bool, bool, bool, bool) K(int vk, bool down, bool winDown, bool mods = false, bool canInject = true, bool injected = false)
            => (vk, down, winDown, mods, canInject, injected);

        var r = Run(K(W, true, false), K(V, true, true), K(W, false, true), K(V, false, false));
        Check(r == "Pass,SwallowAndOpen,ReplaceWinUp,Swallow", $"Win+V, Win released first: Win-up is masked so the Start menu stays shut ({r})");
        r = Run(K(W, true, false), K(V, true, true), K(V, false, true), K(W, false, true));
        Check(r == "Pass,SwallowAndOpen,Swallow,ReplaceWinUp", $"Win+V, V released first: Win-up is masked ({r})");
        r = Run(K(W, true, false), K(W, false, true));
        Check(r == "Pass,Pass", $"a plain Win tap still opens the Start menu ({r})");
        r = Run(K(W, true, false), K(V, true, true), K(V, false, true), K(W, false, true), K(W, true, false), K(W, false, true));
        Check(r.EndsWith("Pass,Pass"), $"the Win press after a Win+V is not masked ({r})");
        r = Run(K(W, true, false), K(W, true, true), K(V, true, true), K(V, true, true), K(W, true, true), K(V, false, true), K(W, false, true));
        Check(r == "Pass,Pass,SwallowAndOpen,Swallow,Pass,Swallow,ReplaceWinUp", $"auto-repeat opens the panel once and still masks ({r})");
        r = Run(K(W, true, false), K(V, true, true), K(V, false, true), K(W, false, true, canInject: false));
        Check(r.EndsWith("Pass"), $"elevated window in front: the real Win-up goes through (never a stuck Win key) ({r})");
        r = Run(K(W, true, false), K(V, true, true), K(W, false, true), K(W, false, true, injected: true), K(V, false, false));
        Check(r == "Pass,SwallowAndOpen,ReplaceWinUp,Pass,Swallow", $"our injected Win-up passes untouched ({r})");
        r = Run(K(W, true, false), K(V, true, true, mods: true), K(V, false, true), K(W, false, true));
        Check(r == "Pass,Pass,Pass,Pass", $"Ctrl/Alt/Shift+Win+V is left to Windows ({r})");
        r = Run(K(V, true, false), K(V, false, false));
        Check(r == "Pass,Pass", $"plain V is untouched ({r})");
    }
}
