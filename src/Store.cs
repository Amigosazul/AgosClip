using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace AgosClip;

/// <summary>Text may carry extra formats (HTML/RTF/spreadsheet) in item_formats. Files = paths, one per line, in Text.</summary>
public enum ClipKind { Text = 0, Image = 1, Files = 2 }

/// <summary>An extra clipboard format kept with an item so paste-back can offer it (rich text, tables…).</summary>
public sealed record ClipFormat(string Name, byte[] Data, bool IsText);

public sealed class ClipItem
{
    public long Id { get; init; }
    public ClipKind Kind { get; init; }
    public string? Text { get; init; }
    public string? ImagePath { get; init; }   // relative to DataDir
    public string? ThumbPath { get; init; }   // relative to DataDir
    public int Width { get; init; }
    public int Height { get; init; }
    public string? SourceApp { get; init; }
    public DateTime Time { get; init; }        // local time of the latest copy
    public int CopyCount { get; init; }
    public bool Pinned { get; set; }
    /// <summary>Names of the extra formats stored with this item (e.g. "HTML Format").</summary>
    public IReadOnlyList<string> Formats { get; init; } = Array.Empty<string>();
    public string[] Paths => Kind == ClipKind.Files && Text != null
        ? Text.Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
}

/// <summary>SQLite-backed history. One row per distinct content (unique hash); copying again only refreshes the time.</summary>
public sealed class Store : IDisposable
{
    SqliteConnection _db = null!;
    public static string ImagesDir => Path.Combine(Settings.DataDir, "images");

    public event Action? Changed;

    public Store() => Open();

    public static string DbFile => Path.Combine(Settings.DataDir, "history.db");

    /// <summary>(Re)opens the database in the current Settings.DataDir.</summary>
    public void Open()
    {
        Directory.CreateDirectory(Settings.DataDir);
        _db = new SqliteConnection($"Data Source={DbFile};Pooling=False");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Exec(@"CREATE TABLE IF NOT EXISTS items(
                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                 kind INTEGER NOT NULL,
                 text TEXT,
                 image_path TEXT,
                 thumb_path TEXT,
                 width INTEGER NOT NULL DEFAULT 0,
                 height INTEGER NOT NULL DEFAULT 0,
                 hash TEXT NOT NULL UNIQUE,
                 source_app TEXT,
                 first_at INTEGER NOT NULL,
                 created_at INTEGER NOT NULL,
                 copy_count INTEGER NOT NULL DEFAULT 1,
                 pinned INTEGER NOT NULL DEFAULT 0);
               CREATE INDEX IF NOT EXISTS ix_items_created ON items(created_at);
               CREATE TABLE IF NOT EXISTS item_formats(
                 item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
                 format TEXT NOT NULL,
                 data BLOB NOT NULL,
                 is_text INTEGER NOT NULL DEFAULT 0,
                 PRIMARY KEY(item_id, format));");
    }

    /// <summary>Folds the WAL into history.db and closes it, so the file can be copied or moved.</summary>
    public void Close()
    {
        try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch { }
        _db.Close();
        _db.Dispose();
    }

    public void NotifyChanged() => Changed?.Invoke();

    void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    /// <summary>Same files in the same order = same item.</summary>
    public static string FilesHash(string[] paths) =>
        "F" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", paths)))).ToLowerInvariant();

    public static string TextHash(string text) =>
        "T" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Thread-safe: uses its own short-lived connection so it can run off the UI thread.</summary>
    public bool Exists(string hash)
    {
        using var db = new SqliteConnection($"Data Source={DbFile};Pooling=False");
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT 1 FROM items WHERE hash=$h";
        c.Parameters.AddWithValue("$h", hash);
        return c.ExecuteScalar() != null;
    }

    /// <summary>Inserts new content, or moves an existing identical item to "now".</summary>
    public void Upsert(ClipKind kind, string hash, string? text, string? image, string? thumb, int w, int h, string? source,
                       long? atMs = null, IReadOnlyList<ClipFormat>? formats = null)
    {
        using var tx = _db.BeginTransaction();
        using var c = _db.CreateCommand();
        c.CommandText = @"INSERT INTO items(kind,text,image_path,thumb_path,width,height,hash,source_app,first_at,created_at)
                          VALUES($k,$t,$i,$th,$w,$hh,$h,$s,$n,$n)
                          ON CONFLICT(hash) DO UPDATE SET created_at=$n, copy_count=copy_count+1,
                              source_app=COALESCE($s, source_app)";
        c.Parameters.AddWithValue("$k", (int)kind);
        c.Parameters.AddWithValue("$t", (object?)text ?? DBNull.Value);
        c.Parameters.AddWithValue("$i", (object?)image ?? DBNull.Value);
        c.Parameters.AddWithValue("$th", (object?)thumb ?? DBNull.Value);
        c.Parameters.AddWithValue("$w", w);
        c.Parameters.AddWithValue("$hh", h);
        c.Parameters.AddWithValue("$h", hash);
        c.Parameters.AddWithValue("$s", (object?)source ?? DBNull.Value);
        c.Parameters.AddWithValue("$n", atMs ?? Now);
        c.ExecuteNonQuery();
        if (formats != null)
        {
            // the latest copy's formats replace the previous ones (same text, maybe different styling)
            using var id = _db.CreateCommand();
            id.CommandText = "SELECT id FROM items WHERE hash=$h";
            id.Parameters.AddWithValue("$h", hash);
            long itemId = Convert.ToInt64(id.ExecuteScalar());
            ReplaceFormats(itemId, formats);
        }
        tx.Commit();
        Changed?.Invoke();
    }

    void ReplaceFormats(long itemId, IReadOnlyList<ClipFormat> formats)
    {
        using (var d = _db.CreateCommand())
        {
            d.CommandText = "DELETE FROM item_formats WHERE item_id=$i";
            d.Parameters.AddWithValue("$i", itemId);
            d.ExecuteNonQuery();
        }
        foreach (var f in formats)
        {
            using var ins = _db.CreateCommand();
            ins.CommandText = "INSERT OR REPLACE INTO item_formats(item_id,format,data,is_text) VALUES($i,$f,$d,$t)";
            ins.Parameters.AddWithValue("$i", itemId);
            ins.Parameters.AddWithValue("$f", f.Name);
            ins.Parameters.AddWithValue("$d", f.Data);
            ins.Parameters.AddWithValue("$t", f.IsText ? 1 : 0);
            ins.ExecuteNonQuery();
        }
    }

    public List<ClipFormat> GetFormats(long itemId)
    {
        var list = new List<ClipFormat>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT format,data,is_text FROM item_formats WHERE item_id=$i";
        c.Parameters.AddWithValue("$i", itemId);
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(new ClipFormat(r.GetString(0), (byte[])r.GetValue(1), r.GetInt32(2) != 0));
        return list;
    }

    /// <summary>
    /// Replaces a text item's content (issue #6). The item keeps its id, time and pinned state; stored rich
    /// formats are dropped because they'd still hold the old text. If another item already has exactly
    /// the new text, that duplicate is merged into this one (keeping "pinned" if either was pinned).
    /// Returns false if the item no longer exists.
    /// </summary>
    public bool UpdateText(long id, string text)
    {
        var hash = TextHash(text);
        using var tx = _db.BeginTransaction();
        using (var other = _db.CreateCommand())
        {
            other.CommandText = "SELECT id, pinned FROM items WHERE hash=$h AND id<>$id";
            other.Parameters.AddWithValue("$h", hash);
            other.Parameters.AddWithValue("$id", id);
            using var r = other.ExecuteReader();
            if (r.Read())
            {
                long dupId = r.GetInt64(0);
                bool dupPinned = r.GetInt32(1) != 0;
                r.Close();
                using var del = _db.CreateCommand();
                del.CommandText = "DELETE FROM items WHERE id=$d";
                del.Parameters.AddWithValue("$d", dupId);
                del.ExecuteNonQuery();
                if (dupPinned)
                {
                    using var pin = _db.CreateCommand();
                    pin.CommandText = "UPDATE items SET pinned=1 WHERE id=$id";
                    pin.Parameters.AddWithValue("$id", id);
                    pin.ExecuteNonQuery();
                }
            }
        }
        int n;
        using (var u = _db.CreateCommand())
        {
            u.CommandText = "UPDATE items SET text=$t, hash=$h WHERE id=$id AND kind=0";
            u.Parameters.AddWithValue("$t", text);
            u.Parameters.AddWithValue("$h", hash);
            u.Parameters.AddWithValue("$id", id);
            n = u.ExecuteNonQuery();
        }
        if (n > 0) ReplaceFormats(id, Array.Empty<ClipFormat>());
        tx.Commit();
        if (n > 0) Changed?.Invoke();
        return n > 0;
    }

    const string Cols = "id,kind,text,image_path,thumb_path,width,height,source_app,created_at,copy_count,pinned," +
                        "(SELECT group_concat(format, char(31)) FROM item_formats f WHERE f.item_id = items.id)";

    static ClipItem Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Kind = (ClipKind)r.GetInt32(1),
        Text = r.IsDBNull(2) ? null : r.GetString(2),
        ImagePath = r.IsDBNull(3) ? null : r.GetString(3),
        ThumbPath = r.IsDBNull(4) ? null : r.GetString(4),
        Width = r.GetInt32(5),
        Height = r.GetInt32(6),
        SourceApp = r.IsDBNull(7) ? null : r.GetString(7),
        Time = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)).LocalDateTime,
        CopyCount = r.GetInt32(9),
        Pinned = r.GetInt32(10) != 0,
        Formats = r.IsDBNull(11) ? Array.Empty<string>() : r.GetString(11).Split('\u001f'),
    };

    List<ClipItem> Query(string where, Action<SqliteCommand>? bind = null, int limit = 2000)
    {
        using var c = _db.CreateCommand();
        c.CommandText = $"SELECT {Cols} FROM items WHERE {where} ORDER BY created_at DESC LIMIT {limit}";
        bind?.Invoke(c);
        var list = new List<ClipItem>();
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    static (long from, long to) DayRange(DateOnly day)
    {
        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        return (new DateTimeOffset(start).ToUnixTimeMilliseconds(),
                new DateTimeOffset(start.AddDays(1)).ToUnixTimeMilliseconds());
    }

    public List<ClipItem> ItemsOnDay(DateOnly day)
    {
        var (from, to) = DayRange(day);
        return Query("created_at >= $a AND created_at < $b", c =>
        {
            c.Parameters.AddWithValue("$a", from);
            c.Parameters.AddWithValue("$b", to);
        });
    }

    public List<ClipItem> PinnedItems() => Query("pinned = 1");

    public List<ClipItem> Search(string text)
    {
        var esc = text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        return Query(@"(text LIKE $q ESCAPE '\' OR source_app LIKE $q ESCAPE '\')", c =>
            c.Parameters.AddWithValue("$q", "%" + esc + "%"), 500);
    }

    /// <summary>Item count per local calendar day.</summary>
    public SortedDictionary<DateOnly, int> DayCounts()
    {
        var map = new SortedDictionary<DateOnly, int>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT date(created_at/1000,'unixepoch','localtime') d, count(*) FROM items GROUP BY d";
        using var r = c.ExecuteReader();
        while (r.Read())
            if (DateOnly.TryParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                map[d] = r.GetInt32(1);
        return map;
    }

    public int PinnedCount()
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT count(*) FROM items WHERE pinned=1";
        return Convert.ToInt32(c.ExecuteScalar());
    }

    public void SetPinned(long id, bool pinned)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "UPDATE items SET pinned=$p WHERE id=$id";
        c.Parameters.AddWithValue("$p", pinned ? 1 : 0);
        c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
        Changed?.Invoke();
    }

    public void Delete(long id) { DeleteWhere("id=$id", c => c.Parameters.AddWithValue("$id", id)); Changed?.Invoke(); }

    /// <summary>Deletes unpinned items older than the retention window. Returns the number removed.</summary>
    public int Cleanup(int retentionDays)
    {
        if (retentionDays <= 0) return 0;
        Watchdog.Step("cleanup old items");
        var cutoffDay = DateOnly.FromDateTime(DateTime.Now).AddDays(-(retentionDays - 1));
        var cutoff = DayRange(cutoffDay).from;
        int n = DeleteWhere("pinned=0 AND created_at < $c", c => c.Parameters.AddWithValue("$c", cutoff));
        if (n > 0) { RemoveEmptyImageDirs(); Changed?.Invoke(); }
        return n;
    }

    public void ClearAll(bool keepPinned)
    {
        DeleteWhere(keepPinned ? "pinned=0" : "1=1", null);
        RemoveEmptyImageDirs();
        Changed?.Invoke();
    }

    int DeleteWhere(string where, Action<SqliteCommand>? bind)
    {
        var files = new List<string>();
        using (var c = _db.CreateCommand())
        {
            c.CommandText = $"SELECT image_path, thumb_path FROM items WHERE {where}";
            bind?.Invoke(c);
            using var r = c.ExecuteReader();
            while (r.Read())
                for (int i = 0; i < 2; i++)
                    if (!r.IsDBNull(i)) files.Add(r.GetString(i));
        }
        int n;
        using (var c = _db.CreateCommand())
        {
            c.CommandText = $"DELETE FROM items WHERE {where}";
            bind?.Invoke(c);
            n = c.ExecuteNonQuery();
        }
        foreach (var f in files)
            try { File.Delete(Path.Combine(Settings.DataDir, f)); } catch { }
        return n;
    }

    static void RemoveEmptyImageDirs()
    {
        try
        {
            if (!Directory.Exists(ImagesDir)) return;
            foreach (var d in Directory.GetDirectories(ImagesDir))
                if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d);
        }
        catch { }
    }

    public void Dispose() => _db.Dispose();
}
