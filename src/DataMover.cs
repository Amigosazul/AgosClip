using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AgosClip;

/// <summary>
/// Moves history.db + images\ to another folder. Copies first, verifies the copy, switches over,
/// and only then deletes the old files, so a failure part-way never loses data.
/// </summary>
public static class DataMover
{
    static readonly string[] DbFiles = { "history.db", "history.db-wal", "history.db-shm" };

    public static bool HasData(string dir) => File.Exists(Path.Combine(dir, "history.db"));

    /// <summary>
    /// Folder that will actually be used for a picked folder: the folder itself if it is empty or already
    /// holds AgosClip data, otherwise an "AgosClip" subfolder (so we never mix with other files).
    /// </summary>
    public static string ResolveTarget(string picked)
    {
        picked = Path.GetFullPath(picked);
        if (HasData(picked) || !Directory.Exists(picked) || !Directory.EnumerateFileSystemEntries(picked).Any()) return picked;
        return Path.Combine(picked, "AgosClip");
    }

    static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static bool Inside(string child, string parent) =>
        Path.GetFullPath(child).TrimEnd('\\').StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static long CurrentSize()
    {
        long n = 0;
        foreach (var f in DbFiles) { var p = Path.Combine(Settings.DataDir, f); if (File.Exists(p)) n += new FileInfo(p).Length; }
        if (Directory.Exists(Store.ImagesDir))
            n += new DirectoryInfo(Store.ImagesDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        return n;
    }

    /// <summary>Returns a user-facing reason the target can't be used, or null if it's fine.</summary>
    public static string? Validate(string target)
    {
        var cur = Settings.DataDir;
        if (Same(target, cur)) return "数据已经在这个文件夹里了。";
        if (Inside(target, cur)) return "不能选当前数据文件夹里面的位置。";
        if (Inside(cur, target) && !HasData(target)) return "不能选当前数据文件夹的上级文件夹，请选一个单独的文件夹。";
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".agosclip-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) { return "这个位置没法写入：" + ex.Message; }
        if (!HasData(target))
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(target));
                if (root != null && !root.StartsWith(@"\\"))
                {
                    long free = new DriveInfo(root).AvailableFreeSpace;
                    long need = CurrentSize() + 50L * 1024 * 1024;
                    if (free < need) return $"磁盘空间不够：需要约 {need / 1048576.0:0} MB，只剩 {free / 1048576.0:0} MB。";
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Copies the data to <paramref name="target"/>, switches to it and deletes the old copy.
    /// If <paramref name="useExisting"/> the target already has data: just switch, leave the old folder as it is.
    /// </summary>
    public static async Task MoveAsync(Store store, Settings settings, string target, bool useExisting, IProgress<string>? progress)
    {
        var oldDir = Settings.DataDir;
        App.Moving = true;
        await Task.Delay(600); // let an in-flight image save finish writing into the old folder
        store.Close();
        var created = new List<string>();
        try
        {
            if (!useExisting)
            {
                await Task.Run(() => CopyAll(oldDir, target, created, progress));
                Verify(oldDir, target);
            }
            settings.SetDataLocation(target);
        }
        catch
        {
            // roll back: remove what we copied, keep using the old folder
            foreach (var f in created.AsEnumerable().Reverse())
                try { if (File.Exists(f)) File.Delete(f); else if (Directory.Exists(f)) Directory.Delete(f, false); } catch { }
            store.Open();
            App.Moving = false;
            throw;
        }

        store.Open();
        App.Moving = false;
        store.NotifyChanged();

        if (!useExisting)
        {
            progress?.Report("正在清理原来的位置…");
            await Task.Run(() => DeleteData(oldDir));
        }
        App.Log($"data moved: {oldDir} -> {target} (existing={useExisting})");
    }

    static void CopyAll(string from, string to, List<string> created, IProgress<string>? progress)
    {
        Directory.CreateDirectory(to);
        foreach (var f in DbFiles)
        {
            var src = Path.Combine(from, f);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(to, f);
            File.Copy(src, dst, true);
            created.Add(dst);
        }
        var imgFrom = Path.Combine(from, "images");
        if (!Directory.Exists(imgFrom)) return;
        var files = Directory.GetFiles(imgFrom, "*", SearchOption.AllDirectories);
        for (int i = 0; i < files.Length; i++)
        {
            var rel = Path.GetRelativePath(from, files[i]);
            var dst = Path.Combine(to, rel);
            var dir = Path.GetDirectoryName(dst)!;
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); created.Add(dir); }
            File.Copy(files[i], dst, true);
            created.Add(dst);
            if (i % 20 == 0) progress?.Report($"正在复制图片 {i + 1}/{files.Length}…");
        }
    }

    static void Verify(string from, string to)
    {
        foreach (var src in Directory.EnumerateFiles(from, "history.db*")
                     .Concat(Directory.Exists(Path.Combine(from, "images"))
                         ? Directory.EnumerateFiles(Path.Combine(from, "images"), "*", SearchOption.AllDirectories)
                         : Enumerable.Empty<string>()))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, src));
            if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length)
                throw new IOException("复制后校验不一致：" + Path.GetRelativePath(from, src));
        }
    }

    /// <summary>Deletes only AgosClip's own files (never settings.json/log, never unrelated files).</summary>
    static void DeleteData(string dir)
    {
        foreach (var f in DbFiles) try { File.Delete(Path.Combine(dir, f)); } catch { }
        try { var img = Path.Combine(dir, "images"); if (Directory.Exists(img)) Directory.Delete(img, true); } catch { }
        try
        {
            if (!Settings.IsDefaultLocation(dir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }
}
