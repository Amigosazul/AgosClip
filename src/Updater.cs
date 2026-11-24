using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgosClip;

/// <param name="Sha256">Expected hash from a trusted channel (GitHub API digest or the repo's latest.json via jsDelivr).</param>
public sealed record UpdateInfo(Version Version, string Notes, string FileName, string? Sha256, long Size,
                                string PageUrl, IReadOnlyList<string> Mirrors, string? Url = null)
{
    /// <summary>The GitHub download URL; plugins pass their own (they live under a separate release tag).</summary>
    public string DirectUrl => Url ?? $"https://github.com/{Updater.Repo}/releases/download/v{Version.ToString(3)}/{FileName}";
}

public sealed record DownloadProgress(double Fraction, string Source, double BytesPerSecond, string? Note = null);

/// <summary>
/// Online upgrade from GitHub Releases, built for networks where github.com downloads crawl or time out:
/// <list type="bullet">
/// <item>Version check: GitHub API, falling back to the repo's latest.json through the jsDelivr CDN.</item>
/// <item>Download: GitHub direct plus GitHub mirror proxies. All are speed-probed first; the fastest is used,
///   and if it stalls or slows to a crawl we switch to the next one and resume (HTTP Range).</item>
/// <item>Integrity: the SHA-256 comes from GitHub (API digest) or latest.json — never from a mirror — so a
///   mirror can't slip in a different file.</item>
/// </list>
/// </summary>
public static class Updater
{
    public const string Repo = "Amigosazul/AgosClip";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    /// <summary>Built-in mirror prefixes (verified reachable from mainland China, 2026-11). latest.json can override.</summary>
    internal static readonly string[] DefaultMirrors = { "https://ghfast.top/", "https://gh-proxy.com/", "https://ghproxy.net/" };

    static readonly string[] LatestJsonUrls =
    {
        $"https://cdn.jsdelivr.net/gh/{Repo}@main/latest.json",
        $"https://fastly.jsdelivr.net/gh/{Repo}@main/latest.json",
        $"https://gcore.jsdelivr.net/gh/{Repo}@main/latest.json",
    };

    // must be initialised before Http (CreateClient reads it)
    // AGOSCLIP_FAKE_VERSION lets the self-test pretend to be an older build
    public static Version Current { get; } =
        Version.TryParse(Environment.GetEnvironmentVariable("AGOSCLIP_FAKE_VERSION"), out var fake) ? fake
        : Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        // uses the Windows system proxy automatically (e.g. a Clash "system proxy")
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8), AutomaticDecompression = DecompressionMethods.All };
        // self-test: behave like a PC without a proxy (the case where github.com downloads time out)
        if (Environment.GetEnvironmentVariable("AGOSCLIP_NO_PROXY") == "1") handler.UseProxy = false;
        var c = new HttpClient(handler)
        { Timeout = Timeout.InfiniteTimeSpan }; // per-call timeouts via CancellationToken
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"AgosClip/{Current.ToString(3)}");
        return c;
    }

    static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static Version? ParseVersion(string? s)
    {
        var t = (s ?? "").Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out var v) ? Norm(v) : null;
    }

    static async Task<string> GetString(string url, TimeSpan timeout, CancellationToken ct, bool github = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (github) req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException(url);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    sealed record LatestJson(Version Version, string File, string? Sha256, long Size, string Notes, string[]? Mirrors);

    static async Task<LatestJson?> FetchLatestJson(CancellationToken ct)
    {
        foreach (var url in LatestJsonUrls)
        {
            try
            {
                using var doc = JsonDocument.Parse(await GetString(url, TimeSpan.FromSeconds(8), ct));
                var r = doc.RootElement;
                var v = ParseVersion(r.GetProperty("version").GetString());
                if (v == null) continue;
                string[]? mirrors = r.TryGetProperty("mirrors", out var m) && m.ValueKind == JsonValueKind.Array
                    ? m.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.StartsWith("https://")).ToArray() : null;
                return new LatestJson(v, r.GetProperty("file").GetString() ?? "", r.TryGetProperty("sha256", out var h) ? h.GetString() : null,
                    r.TryGetProperty("size", out var z) ? z.GetInt64() : 0, r.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "", mirrors);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { App.Log($"update: latest.json via {new Uri(url).Host} failed: {ex.Message}"); }
        }
        return null;
    }

    /// <summary>Returns the latest release if it is newer than this build, else null. Throws if no channel answers.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var jsonTask = FetchLatestJson(ct); // mirrors list (+ fallback version info), fetched in parallel
        UpdateInfo? fromApi = null;
        Exception? apiError = null;
        bool apiSaysNone = false;
        try
        {
            using var doc = JsonDocument.Parse(await GetString($"https://api.github.com/repos/{Repo}/releases/latest", TimeSpan.FromSeconds(10), ct, github: true));
            var root = doc.RootElement;
            var ver = ParseVersion(root.GetProperty("tag_name").GetString());
            string? file = null, sha = null;
            long size = 0;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("AgosClip-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    file = name;
                    size = a.GetProperty("size").GetInt64();
                    if (a.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:")) sha = dg[7..].ToLowerInvariant();
                }
            }
            if (ver != null && file != null)
                fromApi = new UpdateInfo(ver, (root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "").Trim(), file, sha, size,
                    root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage, DefaultMirrors);
            else apiSaysNone = true;
        }
        catch (FileNotFoundException) { apiSaysNone = true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { apiError = ex; App.Log("update: GitHub API failed: " + ex.Message); }

        LatestJson? lj = null;
        try { lj = await jsonTask; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { }

        var mirrors = lj?.Mirrors is { Length: > 0 } jm ? jm : DefaultMirrors;
        UpdateInfo? info = fromApi is { } api
            ? api with { Mirrors = mirrors, Sha256 = api.Sha256 ?? (lj != null && lj.Version == api.Version ? lj.Sha256 : null) }
            : lj != null && !string.IsNullOrEmpty(lj.File)
                ? new UpdateInfo(lj.Version, lj.Notes, lj.File, lj.Sha256, lj.Size, $"https://github.com/{Repo}/releases/tag/v{lj.Version.ToString(3)}", mirrors)
                : null;

        if (info == null)
        {
            if (apiError != null && !apiSaysNone) throw apiError;
            return null;
        }
        return info.Version > Norm(Current) ? info : null;
    }

    /// <summary>Download candidates: GitHub direct, then each mirror prefix.</summary>
    static List<(string Name, string Url)> Sources(UpdateInfo u)
    {
        var list = new List<(string, string)> { ("GitHub", u.DirectUrl) };
        int i = 1;
        foreach (var m in u.Mirrors) list.Add(($"镜像{i++}（{new Uri(m).Host}）", m + u.DirectUrl));
        return list;
    }

    /// <summary>Downloads the first 256 KB from every source at once and ranks them by speed.</summary>
    static async Task<List<(string Name, string Url, double Speed)>> Probe(UpdateInfo u, CancellationToken ct)
    {
        var tasks = Sources(u).Select(async s =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Get, s.Url);
                req.Headers.Range = new RangeHeaderValue(0, 262143);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                await using var st = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[65536];
                long n = 0;
                int r;
                while (n < 262144 && (r = await st.ReadAsync(buf, cts.Token)) > 0) n += r;
                if (n < 4096) throw new IOException("too little data");
                return (s.Name, s.Url, Speed: n / Math.Max(0.05, sw.Elapsed.TotalSeconds));
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) throw;
                App.Log($"update: probe {s.Name} failed: {ex.GetType().Name} {ex.Message}");
                return (s.Name, s.Url, Speed: -1.0);
            }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        var ranked = results.Where(r => r.Speed > 0).OrderByDescending(r => r.Speed).ToList();
        App.Log("update: probe → " + (ranked.Count == 0 ? "no source works" : string.Join(", ", ranked.Select(r => $"{r.Name} {r.Speed / 1024:0} KB/s"))));
        return ranked;
    }

    static (string Name, string Url)? _lastBest;

    /// <summary>URL for a manual browser download: the fastest source found, else GitHub.</summary>
    public static string BrowserUrl(UpdateInfo u) => _lastBest?.Url ?? u.DirectUrl;

    /// <summary>
    /// Downloads the installer to %TEMP% (resuming a previous partial download), switching sources when one
    /// stalls, then verifies its SHA-256. Returns the local path.
    /// </summary>
    /// <param name="dir">Where to put the file; default %TEMP%\AgosClip-Update.</param>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<DownloadProgress>? progress, CancellationToken ct = default, string? dir = null)
    {
        progress?.Report(new DownloadProgress(0, "", 0, "正在测试各下载线路的速度…"));
        var ranked = await Probe(u, ct);
        if (ranked.Count == 0) throw new HttpRequestException("所有下载线路都连不上");
        if (u.Url == null) _lastBest = (ranked[0].Name, ranked[0].Url); // only the installer's "用浏览器下载" uses it

        string? expected = u.Sha256;
        if (expected == null)
        {
            // last resort: the .sha256 asset (weaker, it comes through the same sources)
            foreach (var s in ranked)
            {
                try
                {
                    var txt = await GetString(s.Url + ".sha256", TimeSpan.FromSeconds(15), ct);
                    expected = txt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
                    if (expected?.Length == 64) break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
        }
        if (expected == null || expected.Length != 64) throw new InvalidDataException("拿不到安装包的校验值，为安全起见不下载。");

        dir ??= Path.Combine(Path.GetTempPath(), "AgosClip-Update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, u.FileName);
        var part = path + ".part";
        if (File.Exists(path) && await HashOf(path, ct) == expected) return path; // already downloaded earlier

        long total = u.Size;
        if (total > 0)
        {
            // pieces in parallel from every working source: on slow links the speeds add up
            await ParallelDownload(ranked, part, total, progress, ct);
        }
        else
        {
            // size unknown (shouldn't happen): one source at a time, switching + resuming on stalls
            int attempt = 0;
            var bad = new HashSet<string>();
            while (true)
            {
                var live = ranked.Where(r => !bad.Contains(r.Name)).ToList();
                if (live.Count == 0)
                {
                    if (++attempt >= 3) throw new HttpRequestException("各下载线路都中断了");
                    bad.Clear();
                    live = ranked;
                }
                var src = live[0];
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                var outcome = await DownloadFrom(src, part, have, total, live.Count > 1, progress, ct);
                if (outcome.Total > 0) total = outcome.Total;
                if (outcome.Done) break;
                App.Log($"update: {src.Name} {outcome.Reason}; switching");
                bad.Add(src.Name);
            }
        }

        var actual = await HashOf(part, ct);
        if (actual != expected)
        {
            File.Delete(part);
            try { File.Delete(part + ".chunks"); } catch { }
            throw new InvalidDataException("下载的文件校验不通过，已删除。请重试。");
        }
        File.Move(part, path, true);
        return path;
    }

    const int ChunkSize = 1 << 20; // 1 MB pieces

    /// <summary>
    /// Splits the file into 1 MB pieces and fetches them concurrently from all working sources (the fastest
    /// gets two connections). A failed or stalled piece goes back in the queue; a source that keeps failing is
    /// dropped. When the queue runs dry, idle connections re-fetch pieces still in flight on slow ones.
    /// Finished pieces are recorded in "&lt;part&gt;.chunks" so a later attempt resumes.
    /// </summary>
    static async Task ParallelDownload(List<(string Name, string Url, double Speed)> ranked, string part, long total,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        int count = (int)((total + ChunkSize - 1) / ChunkSize);
        var mapFile = part + ".chunks";
        var done = new bool[count];
        if (File.Exists(part) && new FileInfo(part).Length == total && File.Exists(mapFile))
        {
            foreach (var t in File.ReadAllText(mapFile).Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(t, out var i) && i >= 0 && i < count) done[i] = true;
        }
        else
        {
            try { File.Delete(mapFile); } catch { }
        }

        using var handle = File.OpenHandle(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (RandomAccess.GetLength(handle) != total) RandomAccess.SetLength(handle, total);

        var gate = new object();
        var queue = new Queue<int>(Enumerable.Range(0, count).Where(i => !done[i]));
        var inFlight = new Dictionary<int, int>(); // chunk → connections working on it
        long bytesDone = done.Count(d => d) * (long)ChunkSize;
        long liveBytes = 0;                         // bytes of unfinished pieces, for smooth progress
        int remaining = queue.Count;
        if (remaining == 0) return;
        App.Log($"update: parallel download, {remaining}/{count} pieces left, sources: {string.Join(", ", ranked.Select(r => r.Name))}");

        using var allDone = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, allDone.Token);

        int? Take()
        {
            lock (gate)
            {
                if (remaining == 0) return null;
                while (queue.Count > 0)
                {
                    int c = queue.Dequeue();
                    if (done[c]) continue;
                    inFlight[c] = inFlight.GetValueOrDefault(c) + 1;
                    return c;
                }
                // endgame: help with a piece someone else (maybe slow) is still fetching
                var slow = inFlight.Where(kv => kv.Value == 1 && !done[kv.Key]).Select(kv => (int?)kv.Key).FirstOrDefault();
                if (slow is int sc) inFlight[sc] = 2;
                return slow;
            }
        }

        void Release(int c, bool ok)
        {
            lock (gate)
            {
                inFlight[c] = inFlight.GetValueOrDefault(c) - 1;
                if (inFlight[c] <= 0) inFlight.Remove(c);
                if (ok && !done[c])
                {
                    done[c] = true;
                    remaining--;
                    bytesDone += Math.Min(ChunkSize, total - (long)c * ChunkSize);
                    try { File.AppendAllText(mapFile, c + ","); } catch { }
                    if (remaining == 0) allDone.Cancel();
                }
                else if (!ok && !done[c] && !inFlight.ContainsKey(c)) queue.Enqueue(c);
            }
        }

        async Task Worker((string Name, string Url, double Speed) src)
        {
            int failures = 0;
            var buf = new byte[1 << 16];
            while (!linked.IsCancellationRequested)
            {
                if (Take() is not int c) { await Task.Delay(300, linked.Token).ContinueWith(_ => { }); if (Volatile.Read(ref remaining) == 0) return; continue; }
                long from = (long)c * ChunkSize, to = Math.Min(total, from + ChunkSize) - 1;
                long got = 0;
                bool ok = false;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    cts.CancelAfter(TimeSpan.FromSeconds(60)); // a 1 MB piece slower than ~17 KB/s → give it to someone else
                    using var req = new HttpRequestMessage(HttpMethod.Get, src.Url);
                    req.Headers.Range = new RangeHeaderValue(from, to);
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (resp.StatusCode != HttpStatusCode.PartialContent) throw new IOException($"no range support ({(int)resp.StatusCode})");
                    await using var st = await resp.Content.ReadAsStreamAsync(cts.Token);
                    var piece = new byte[to - from + 1];
                    int n;
                    while (got < piece.Length && (n = await st.ReadAsync(piece.AsMemory((int)got), cts.Token)) > 0)
                    {
                        got += n;
                        Interlocked.Add(ref liveBytes, n);
                    }
                    if (got != piece.Length) throw new IOException("short piece");
                    lock (gate) { if (!done[c]) RandomAccess.Write(handle, piece, from); }
                    ok = true;
                    failures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (allDone.IsCancellationRequested) return;
                    failures++;
                    App.Log($"update: {src.Name} piece {c} failed ({ex.GetType().Name}: {ex.Message})");
                }
                finally
                {
                    Interlocked.Add(ref liveBytes, -got);
                    Release(c, ok);
                }
                if (failures >= 3) { App.Log($"update: dropping {src.Name}"); return; }
            }
        }

        // two connections on the fastest source, one on each of the others (max 5)
        var plan = new List<(string, string, double)> { ranked[0] };
        plan.AddRange(ranked);
        var workers = plan.Take(5).Select(src => Task.Run(() => Worker(src))).ToList();

        var sw = Stopwatch.StartNew();
        long lastBytes = bytesDone; double speed = 0;
        while (workers.Any(w => !w.IsCompleted))
        {
            await Task.WhenAny(Task.WhenAll(workers), Task.Delay(1000, ct));
            ct.ThrowIfCancellationRequested();
            long now;
            lock (gate) now = bytesDone;
            now += Interlocked.Read(ref liveBytes);
            speed = speed == 0 ? (now - lastBytes) / Math.Max(0.2, sw.Elapsed.TotalSeconds) : speed * 0.6 + 0.4 * (now - lastBytes) / Math.Max(0.2, sw.Elapsed.TotalSeconds);
            lastBytes = now;
            sw.Restart();
            int active = workers.Count(w => !w.IsCompleted);
            if (active == 0) break;
            progress?.Report(new DownloadProgress(Math.Min(1, (double)now / total), $"{active} 条线路", speed));
        }
        await Task.WhenAll(workers); // surface cancellation
        if (Volatile.Read(ref remaining) > 0) throw new HttpRequestException($"下载中断（还差 {remaining} 块），再点一次会接着下");
        try { File.Delete(mapFile); } catch { }
    }

    sealed record Outcome(bool Done, long Total, string Reason);

    static async Task<Outcome> DownloadFrom((string Name, string Url, double Speed) src, string part, long offset, long total,
        bool canSwitch, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, src.Url);
            if (offset > 0) req.Headers.Range = new RangeHeaderValue(offset, null);
            using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headCts.Token);
            resp.EnsureSuccessStatusCode();
            bool resumed = resp.StatusCode == HttpStatusCode.PartialContent && offset > 0;
            if (!resumed) offset = 0; // server ignored Range: start over
            if (resp.Content.Headers.ContentRange?.Length is long full) total = full;
            else if (!resumed && resp.Content.Headers.ContentLength is long len) total = len;

            await using var st = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[1 << 16];
            long done = offset;
            var window = Stopwatch.StartNew();
            var onSource = Stopwatch.StartNew();
            long windowStart = done;
            double speed = src.Speed;
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                stall.CancelAfter(TimeSpan.FromSeconds(20)); // no byte for 20 s → stalled
                int n = await st.ReadAsync(buf, stall.Token);
                if (n == 0) break;
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (window.Elapsed.TotalSeconds >= 1)
                {
                    speed = (done - windowStart) / window.Elapsed.TotalSeconds;
                    progress?.Report(new DownloadProgress(total > 0 ? (double)done / total : 0, src.Name, speed));
                    // crawling (<25 KB/s for a while) and another source exists → switch, keep what we have
                    if (canSwitch && onSource.Elapsed.TotalSeconds > 12 && speed < 25 * 1024)
                        return new Outcome(false, total, $"too slow ({speed / 1024:0} KB/s)");
                    window.Restart();
                    windowStart = done;
                }
            }
            return total > 0 && done < total ? new Outcome(false, total, "connection closed early") : new Outcome(true, total, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new Outcome(false, total, "stalled"); }
        catch (Exception ex) { return new Outcome(false, total, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    static async Task<string> HashOf(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
    }

    /// <summary>Starts the silent in-place upgrade; the installer closes this process and restarts it afterwards.</summary>
    public static void RunInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTAPP") { UseShellExecute = true });
        App.Current.Quit();
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void OpenReleasesPage(string? url = null) => OpenUrl(url ?? ReleasesPage);
}
