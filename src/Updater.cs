using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgosClip;

public sealed record UpdateInfo(Version Version, string Notes, string InstallerUrl, string ShaUrl, long Size, string PageUrl);

/// <summary>
/// Online upgrade from GitHub Releases. A release must carry AgosClip-Setup-x.y.z.exe and a matching
/// .sha256 file; the download is refused unless the hash matches. The installer then upgrades in place
/// (it keeps the previous install folder) and restarts the app.
/// </summary>
public static class Updater
{
    public const string Repo = "Amigosazul/AgosClip";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    // must be initialised before Http (CreateClient reads it)
    // AGOSCLIP_FAKE_VERSION lets the self-test pretend to be an older build
    public static Version Current { get; } =
        Version.TryParse(Environment.GetEnvironmentVariable("AGOSCLIP_FAKE_VERSION"), out var fake) ? fake
        : Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // per-call timeouts via CancellationToken
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"AgosClip/{Current.ToString(3)}");
        return c;
    }

    static Version? ParseTag(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>Returns the latest release if it is newer than this build, else null. Throws on network errors.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, cts.Token);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var root = doc.RootElement;
        var ver = ParseTag(root.GetProperty("tag_name").GetString() ?? "");
        if (ver == null || ver <= new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build))) return null;

        string? exe = null, sha = null;
        long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString();
            if (name.StartsWith("AgosClip-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            { exe = url; size = a.GetProperty("size").GetInt64(); }
            else if (name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (exe == null || sha == null) return null; // incomplete release: ignore
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        return new UpdateInfo(ver, notes.Trim(), exe, sha, size, page);
    }

    /// <summary>Downloads the installer to %TEMP% and verifies its SHA-256. Returns the local path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct = default)
    {
        string expected;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var text = await Http.GetStringAsync(u.ShaUrl, cts.Token);
            expected = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant() ?? "";
            if (expected.Length != 64) throw new InvalidDataException("校验文件格式不对");
        }

        var dir = Path.Combine(Path.GetTempPath(), "AgosClip-Update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"AgosClip-Setup-{u.Version.ToString(3)}.exe");
        var part = path + ".part";

        using (var resp = await Http.GetAsync(u.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? u.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            // stall guard: abort if no bytes arrive for 60 s
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                n = await src.ReadAsync(buf, stall.Token);
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (var fs = File.OpenRead(part))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(part);
            throw new InvalidDataException("下载的文件校验失败，已删除。请稍后重试。");
        }
        File.Move(part, path, true);
        return path;
    }

    /// <summary>Starts the silent in-place upgrade; the installer closes this process and restarts it afterwards.</summary>
    public static void RunInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTAPP") { UseShellExecute = true });
        App.Current.Quit();
    }

    public static void OpenReleasesPage(string? url = null) =>
        Process.Start(new ProcessStartInfo(url ?? ReleasesPage) { UseShellExecute = true });
}
