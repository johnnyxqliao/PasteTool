using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Flow.Launcher.Plugin.PasteTool;

internal class ClipboardStore
{
    public class Record
    {
        public long Id { get; set; }
        public string Kind { get; set; } = "text";
        public string? Content { get; set; }
        public string? ImagePath { get; set; }
        public string? PreviewPath { get; set; }
        public List<string>? Files { get; set; }
        public List<string?>? CachedFiles { get; set; }
        public string? SourceApp { get; set; }
        public long CreatedAt { get; set; }
        public string CreatedDisplay => DateTimeOffset.FromUnixTimeSeconds(CreatedAt).LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss");

        public string Title()
        {
            switch (Kind)
            {
                case "text": return SingleLine(Content ?? "", 100);
                case "image": return "图片";
                case "files":
                    if (Files == null || Files.Count == 0) return "文件";
                    var name = Path.GetFileName(Files[0]);
                    return Files.Count > 1 ? $"文件：{name} 等 {Files.Count} 个文件" : $"文件：{name}";
                default: return Kind;
            }
        }

        public string Subtitle()
        {
            if (Kind == "files" && Files != null && Files.Count > 0) return Files[0];
            return $"{CreatedDisplay} · {(string.IsNullOrEmpty(SourceApp) ? "未知来源" : SourceApp)}";
        }

        private static string SingleLine(string text, int limit)
        {
            var cleaned = string.Join(" ", text.Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrEmpty(cleaned)) return "(空文本)";
            return cleaned.Length <= limit ? cleaned : cleaned[..(limit - 1)] + "...";
        }
    }

    private readonly string _pluginDir;
    private readonly string _dbPath;
    private readonly string _imagesDir;
    private readonly string _filesDir;

    public ClipboardStore(string pluginDir)
    {
        _pluginDir = pluginDir;
        var dataDir = Path.Combine(pluginDir, "Data");
        Directory.CreateDirectory(dataDir);
        _imagesDir = Path.Combine(dataDir, "images");
        Directory.CreateDirectory(_imagesDir);
        _filesDir = Path.Combine(dataDir, "files");
        Directory.CreateDirectory(_filesDir);
        _dbPath = Path.Combine(dataDir, "history.sqlite3");
        InitDb();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private void InitDb()
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS records (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind TEXT NOT NULL,
                    content TEXT,
                    image_path TEXT,
                    preview_path TEXT,
                    files_json TEXT,
                    cached_files_json TEXT,
                    source_app TEXT,
                    content_hash TEXT NOT NULL UNIQUE,
                    created_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_records_created_at ON records(created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_records_kind ON records(kind);";
            cmd.ExecuteNonQuery();
        }
        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE records ADD COLUMN cached_files_json TEXT";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException) { /* column exists */ }
    }

    public void AddText(string text, string sourceApp, string hash) =>
        Upsert("text", hash, sourceApp, content: text);

    public void AddImage(string imagePath, string previewPath, string sourceApp, string hash) =>
        Upsert("image", hash, sourceApp, imagePath: imagePath, previewPath: previewPath);

    public void AddFiles(List<string> paths, List<string?>? cachedPaths, string sourceApp, string hash) =>
        Upsert("files", hash, sourceApp,
            filesJson: JsonSerializer.Serialize(paths),
            cachedFilesJson: cachedPaths == null ? null : JsonSerializer.Serialize(cachedPaths));

    private void Upsert(string kind, string hash, string sourceApp,
        string? content = null, string? imagePath = null, string? previewPath = null,
        string? filesJson = null, string? cachedFilesJson = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var conn = Open();
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT id FROM records WHERE content_hash = $h";
            check.Parameters.AddWithValue("$h", hash);
            var existing = check.ExecuteScalar();
            if (existing != null && existing != DBNull.Value)
            {
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE records SET created_at = $t, source_app = $s WHERE id = $i";
                upd.Parameters.AddWithValue("$t", now);
                upd.Parameters.AddWithValue("$s", sourceApp ?? string.Empty);
                upd.Parameters.AddWithValue("$i", existing);
                upd.ExecuteNonQuery();
                return;
            }
        }
        using var ins = conn.CreateCommand();
        ins.CommandText = @"INSERT INTO records (kind, content, image_path, preview_path, files_json, cached_files_json, source_app, content_hash, created_at)
            VALUES ($k, $c, $ip, $pp, $fj, $cfj, $s, $h, $t)";
        ins.Parameters.AddWithValue("$k", kind);
        ins.Parameters.AddWithValue("$c", (object?)content ?? DBNull.Value);
        ins.Parameters.AddWithValue("$ip", (object?)imagePath ?? DBNull.Value);
        ins.Parameters.AddWithValue("$pp", (object?)previewPath ?? DBNull.Value);
        ins.Parameters.AddWithValue("$fj", (object?)filesJson ?? DBNull.Value);
        ins.Parameters.AddWithValue("$cfj", (object?)cachedFilesJson ?? DBNull.Value);
        ins.Parameters.AddWithValue("$s", sourceApp ?? string.Empty);
        ins.Parameters.AddWithValue("$h", hash);
        ins.Parameters.AddWithValue("$t", now);
        ins.ExecuteNonQuery();
    }

    public List<Record> Search(string query, int limit)
    {
        var results = new List<Record>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(query))
        {
            cmd.CommandText = "SELECT * FROM records ORDER BY created_at DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM records WHERE content LIKE $q OR files_json LIKE $q ORDER BY created_at DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$q", $"%{query}%");
            cmd.Parameters.AddWithValue("$l", limit);
        }
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(ReadRecord(reader));
        return results;
    }

    public Record? Get(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM records WHERE id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public (int total, string latest) Stats()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) AS total, MAX(created_at) AS latest FROM records";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, "");
        var total = reader.GetInt32(0);
        var latest = reader.IsDBNull(1) ? "" : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss");
        return (total, latest);
    }

    public void Cleanup(int keepDays)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)keepDays * 86400;
        DeleteWhere("created_at < $c", ("$c", cutoff));
    }

    public void Clear() => DeleteWhere("1=1");

    private void DeleteWhere(string condition, params (string, object)[] parameters)
    {
        var imagesToDelete = new List<string>();
        var cachedFileDirsToDelete = new HashSet<string>();
        using (var conn = Open())
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = $"SELECT image_path, preview_path, cached_files_json FROM records WHERE {condition}";
            foreach (var (k, v) in parameters) sel.Parameters.AddWithValue(k, v);
            using var reader = sel.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0)) imagesToDelete.Add(reader.GetString(0));
                if (!reader.IsDBNull(1)) imagesToDelete.Add(reader.GetString(1));
                if (!reader.IsDBNull(2))
                {
                    try
                    {
                        var cached = JsonSerializer.Deserialize<List<string?>>(reader.GetString(2));
                        if (cached != null)
                        {
                            foreach (var p in cached)
                            {
                                if (string.IsNullOrEmpty(p)) continue;
                                var dir = Path.GetDirectoryName(p);
                                if (!string.IsNullOrEmpty(dir)) cachedFileDirsToDelete.Add(dir!);
                            }
                        }
                    }
                    catch { }
                }
            }
            reader.Close();

            using var del = conn.CreateCommand();
            del.CommandText = $"DELETE FROM records WHERE {condition}";
            foreach (var (k, v) in parameters) del.Parameters.AddWithValue(k, v);
            del.ExecuteNonQuery();
        }

        foreach (var rel in imagesToDelete)
        {
            try
            {
                var path = Path.IsPathRooted(rel) ? rel : Path.Combine(_pluginDir, rel);
                if (File.Exists(path) && path.StartsWith(_imagesDir, StringComparison.OrdinalIgnoreCase))
                    File.Delete(path);
            }
            catch { }
        }

        foreach (var dir in cachedFileDirsToDelete)
        {
            try
            {
                if (Directory.Exists(dir) && dir.StartsWith(_filesDir, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(dir, recursive: true);
            }
            catch { }
        }
    }

    private static Record ReadRecord(SqliteDataReader r)
    {
        var rec = new Record
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            Kind = r.GetString(r.GetOrdinal("kind")),
            Content = r.IsDBNull(r.GetOrdinal("content")) ? null : r.GetString(r.GetOrdinal("content")),
            ImagePath = r.IsDBNull(r.GetOrdinal("image_path")) ? null : r.GetString(r.GetOrdinal("image_path")),
            PreviewPath = r.IsDBNull(r.GetOrdinal("preview_path")) ? null : r.GetString(r.GetOrdinal("preview_path")),
            SourceApp = r.IsDBNull(r.GetOrdinal("source_app")) ? null : r.GetString(r.GetOrdinal("source_app")),
            CreatedAt = r.GetInt64(r.GetOrdinal("created_at")),
        };
        var fjIdx = r.GetOrdinal("files_json");
        if (!r.IsDBNull(fjIdx))
        {
            try { rec.Files = JsonSerializer.Deserialize<List<string>>(r.GetString(fjIdx)); }
            catch { rec.Files = new List<string>(); }
        }
        try
        {
            var cfjIdx = r.GetOrdinal("cached_files_json");
            if (!r.IsDBNull(cfjIdx))
            {
                try { rec.CachedFiles = JsonSerializer.Deserialize<List<string?>>(r.GetString(cfjIdx)); }
                catch { rec.CachedFiles = null; }
            }
        }
        catch (IndexOutOfRangeException) { }
        return rec;
    }
}
