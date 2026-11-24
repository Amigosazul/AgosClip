using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgosClip;

public sealed record TranslateLang(string Code, string Name);

/// <summary>
/// Free machine translation without an API key: Google's public "gtx" endpoint first (best quality, but
/// mainland China needs a proxy — the system proxy is used automatically), then MyMemory's free API
/// (reachable without a proxy; anonymous use is limited to roughly 5000 characters a day).
/// Neither is a paid service, so either can stop working; the caller shows the error.
/// </summary>
public static class Translator
{
    public const string Auto = "auto";

    /// <summary>Targets offered in the UI. "auto" = Chinese text → English, anything else → Simplified Chinese.</summary>
    public static readonly TranslateLang[] Targets =
    {
        new(Auto, "自动（中⇄英）"),
        new("zh-Hans", "简体中文"),
        new("en", "英语"),
        new("ja", "日语"),
        new("ko", "韩语"),
        new("zh-Hant", "繁体中文"),
        new("fr", "法语"),
        new("de", "德语"),
        new("es", "西班牙语"),
        new("ru", "俄语"),
    };

    public static string NameOf(string code) => Targets.FirstOrDefault(t => t.Code == code)?.Name ?? code;

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8), AutomaticDecompression = DecompressionMethods.All };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        // the Edge endpoints expect a browser
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0");
        return c;
    }

    /// <summary>Picks the real target for "auto" from the text itself.</summary>
    public static string Resolve(string target, IEnumerable<string> texts)
    {
        if (target != Auto) return target;
        int han = 0, letters = 0;
        foreach (var s in texts)
            foreach (var ch in s)
            {
                if (ch >= 0x4E00 && ch <= 0x9FFF || ch >= 0x3400 && ch <= 0x4DBF) han++;
                else if (char.IsLetter(ch)) letters++;
            }
        // a Chinese text with some English words still counts as Chinese
        return han > 0 && han * 3 >= letters ? "en" : "zh-Hans";
    }

    /// <summary>Translates each text (empty ones stay empty). Returns the translations and the engine used.</summary>
    public static async Task<(string[] Texts, string Engine)> TranslateAsync(IReadOnlyList<string> texts, string target, CancellationToken ct = default)
    {
        var to = Resolve(target, texts);
        var errors = new List<string>();
        try { return (await Google(texts, to, ct), "谷歌翻译"); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { errors.Add("谷歌：" + Short(ex)); App.Log("translate: google failed: " + ex.Message); }
        try { return (await MyMemory(texts, to, ct), "MyMemory"); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { errors.Add("MyMemory：" + Short(ex)); App.Log("translate: mymemory failed: " + ex.Message); }
        throw new HttpRequestException("翻译服务都连不上（" + string.Join("；", errors) + "）");
    }

    static string Short(Exception ex) => ex is TaskCanceledException ? "超时" : ex is HttpRequestException h && h.StatusCode is { } c ? $"HTTP {(int)c}" : ex.Message;

    // ───────────── Google (gtx) ─────────────

    static async Task<string[]> Google(IReadOnlyList<string> texts, string to, CancellationToken ct)
    {
        var tl = to switch { "zh-Hans" => "zh-CN", "zh-Hant" => "zh-TW", _ => to };
        var result = new string[texts.Count];
        using var gate = new SemaphoreSlim(4); // image translation sends one request per block: a few at a time
        await Task.WhenAll(Enumerable.Range(0, texts.Count).Select(async i =>
        {
            if (string.IsNullOrWhiteSpace(texts[i])) { result[i] = texts[i]; return; }
            await gate.WaitAsync(ct);
            try
            {
                var sb = new StringBuilder();
                foreach (var piece in SplitLong(texts[i], 1800)) // GET URL length
                {
                    var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={tl}&dt=t&q={Uri.EscapeDataString(piece)}";
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
                    foreach (var seg in doc.RootElement[0].EnumerateArray())
                        if (seg.ValueKind == JsonValueKind.Array && seg[0].ValueKind == JsonValueKind.String) sb.Append(seg[0].GetString());
                }
                result[i] = sb.ToString();
            }
            finally { gate.Release(); }
        }));
        return result;
    }

    // ───────────── MyMemory ─────────────

    static async Task<string[]> MyMemory(IReadOnlyList<string> texts, string to, CancellationToken ct)
    {
        var tl = to switch { "zh-Hans" => "zh-CN", "zh-Hant" => "zh-TW", _ => to };
        var result = new string[texts.Count];
        for (int i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i])) { result[i] = texts[i]; continue; }
            var sl = GuessSource(texts[i]);
            if (sl == tl) { result[i] = texts[i]; continue; }
            var sb = new StringBuilder();
            foreach (var piece in SplitLong(texts[i], 450)) // the API takes at most 500 bytes-ish per query
            {
                if (string.IsNullOrWhiteSpace(piece)) { sb.Append(piece); continue; }
                var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(piece)}&langpair={sl}|{tl}";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
                var root = doc.RootElement;
                if (root.TryGetProperty("quotaFinished", out var q) && q.ValueKind == JsonValueKind.True)
                    throw new HttpRequestException("今天的免费额度用完了");
                int status = root.TryGetProperty("responseStatus", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 200;
                if (status != 200) throw new HttpRequestException(root.TryGetProperty("responseDetails", out var d) ? d.ToString() : $"status {status}");
                sb.Append(WebUtility.HtmlDecode(root.GetProperty("responseData").GetProperty("translatedText").GetString() ?? ""));
                if (piece.EndsWith('\n')) sb.Append('\n');
            }
            result[i] = sb.ToString().TrimEnd('\n');
        }
        return result;
    }

    /// <summary>MyMemory has no auto-detect: guess the source language from the script.</summary>
    static string GuessSource(string s)
    {
        int han = 0, kana = 0, hangul = 0, cyr = 0, latin = 0;
        foreach (var ch in s)
        {
            if (ch >= 0x3040 && ch <= 0x30FF) kana++;
            else if (ch >= 0x4E00 && ch <= 0x9FFF) han++;
            else if (ch >= 0xAC00 && ch <= 0xD7AF) hangul++;
            else if (ch >= 0x0400 && ch <= 0x04FF) cyr++;
            else if (ch < 0x250 && char.IsLetter(ch)) latin++;
        }
        if (kana > 0 && kana * 5 >= han) return "ja";
        if (hangul > Math.Max(han, latin)) return "ko";
        if (cyr > Math.Max(han, latin)) return "ru";
        if (han > 0 && han * 3 >= latin) return "zh-CN";
        return "en";
    }

    // ───────────── helpers ─────────────

    /// <summary>Splits at line breaks (or hard, as a last resort) into pieces of at most <paramref name="max"/> characters.</summary>
    internal static IEnumerable<string> SplitLong(string s, int max)
    {
        if (s.Length <= max) { yield return s; yield break; }
        var sb = new StringBuilder();
        foreach (var line in s.Split('\n'))
        {
            var l = line + "\n";
            if (sb.Length + l.Length > max && sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            while (l.Length > max) { yield return l[..max]; l = l[max..]; }
            sb.Append(l);
        }
        if (sb.Length > 0) yield return sb.ToString().TrimEnd('\n');
    }
}
