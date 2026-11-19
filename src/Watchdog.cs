using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace AgosClip;

/// <summary>
/// Pings the UI thread once a second from a background thread. If a ping isn't answered for 3 s,
/// logs that the UI is stuck and what it was last doing (see <see cref="Step"/>), and logs again
/// when it recovers. Makes "未响应" diagnosable from agosclip.log.
/// </summary>
public static class Watchdog
{
    static volatile string _op = "idle";
    static long _opAt;

    /// <summary>Breadcrumb: call before anything on the UI thread that could take a while.</summary>
    public static void Step(string op)
    {
        _op = op;
        Interlocked.Exchange(ref _opAt, Stopwatch.GetTimestamp());
    }

    public static void Start(Dispatcher ui)
    {
        new Thread(() =>
        {
            int pending = 0;
            long sentAt = 0;
            bool reported = false;
            while (true)
            {
                Thread.Sleep(1000);
                if (Volatile.Read(ref pending) == 0)
                {
                    if (reported)
                    {
                        App.Log($"UI recovered after {Stopwatch.GetElapsedTime(sentAt).TotalSeconds:0.0}s");
                        reported = false;
                    }
                    Volatile.Write(ref pending, 1);
                    sentAt = Stopwatch.GetTimestamp();
                    ui.BeginInvoke(() => Volatile.Write(ref pending, 0), DispatcherPriority.Send);
                }
                else if (!reported && Stopwatch.GetElapsedTime(sentAt).TotalSeconds > 3)
                {
                    var opAge = Stopwatch.GetElapsedTime(Interlocked.Read(ref _opAt)).TotalSeconds;
                    App.Log($"UI NOT RESPONDING >3s; last step: \"{_op}\" ({opAge:0.0}s ago)");
                    reported = true;
                }
            }
        }) { IsBackground = true, Name = "ui-watchdog", Priority = ThreadPriority.BelowNormal }.Start();
    }
}
