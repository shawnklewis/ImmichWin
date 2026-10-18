using Microsoft.Data.Sqlite;

namespace ImmichWin.Data;

public sealed record CacheItem(string Id, string Kind, string Path, long Bytes);

public sealed record ChangeRecord(long Id, string AssetId, string Type, string Ref, string? Extra, bool Undone, DateTimeOffset At);

public sealed class StateDb : IDisposable
{
    readonly SqliteConnection _c;
    readonly object _gate = new();

    public StateDb(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _c = new SqliteConnection($"Data Source={path}");
        _c.Open();
        Exec("""
            CREATE TABLE IF NOT EXISTS cache_items(id TEXT, kind TEXT, path TEXT, bytes INTEGER, last_access INTEGER, PRIMARY KEY(id,kind));
            CREATE TABLE IF NOT EXISTS processed(asset_id TEXT PRIMARY KEY, status TEXT, at INTEGER, error TEXT, attempts INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS changes(id INTEGER PRIMARY KEY AUTOINCREMENT, asset_id TEXT, type TEXT, ref TEXT, extra TEXT, undone INTEGER DEFAULT 0, at INTEGER);
            CREATE TABLE IF NOT EXISTS uploaded(sha1 TEXT PRIMARY KEY, asset_id TEXT, at INTEGER);
            CREATE TABLE IF NOT EXISTS kv(k TEXT PRIMARY KEY, v TEXT);
            """);
    }

    void Exec(string sql, params (string, object?)[] p)
    {
        lock (_gate)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] p)
    {
        lock (_gate)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var l = new List<T>();
            while (r.Read()) l.Add(map(r));
            return l;
        }
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---- cache
    public void PutCache(string id, string kind, string path, long bytes) =>
        Exec("INSERT OR REPLACE INTO cache_items VALUES($i,$k,$p,$b,$t)", ("$i", id), ("$k", kind), ("$p", path), ("$b", bytes), ("$t", Now()));

    public CacheItem? GetCache(string id, string kind) =>
        Query("SELECT id,kind,path,bytes FROM cache_items WHERE id=$i AND kind=$k",
            r => new CacheItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)), ("$i", id), ("$k", kind)).FirstOrDefault();

    public void TouchCache(string id, string kind) =>
        Exec("UPDATE cache_items SET last_access=$t WHERE id=$i AND kind=$k", ("$t", Now()), ("$i", id), ("$k", kind));

    public long CacheBytes() => Query("SELECT COALESCE(SUM(bytes),0) FROM cache_items", r => r.GetInt64(0)).First();

    public IReadOnlyList<CacheItem> CacheOldestFirst() =>
        Query("SELECT id,kind,path,bytes FROM cache_items ORDER BY last_access ASC, rowid ASC",
            r => new CacheItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));

    public void RemoveCache(string id, string kind) =>
        Exec("DELETE FROM cache_items WHERE id=$i AND kind=$k", ("$i", id), ("$k", kind));

    // ---- processed
    public bool TryClaim(string assetId)
    {
        lock (_gate)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO processed(asset_id,status,at) VALUES($a,'claimed',$t)";
            cmd.Parameters.AddWithValue("$a", assetId);
            cmd.Parameters.AddWithValue("$t", Now());
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public void SetProcessed(string assetId, string status, string? error = null) =>
        Exec("UPDATE processed SET status=$s, error=$e, at=$t, attempts=attempts+1 WHERE asset_id=$a", ("$s", status), ("$e", error), ("$t", Now()), ("$a", assetId));

    public void Release(string assetId) => Exec("DELETE FROM processed WHERE asset_id=$a", ("$a", assetId));

    public string? GetProcessedStatus(string assetId) =>
        Query("SELECT status FROM processed WHERE asset_id=$a", r => r.GetString(0), ("$a", assetId)).FirstOrDefault();

    public int ProcessedToday() =>
        Query("SELECT COUNT(*) FROM processed WHERE status='done' AND at>=$t", r => r.GetInt32(0),
            ("$t", new DateTimeOffset(DateTime.Now.Date).ToUnixTimeSeconds())).First();

    // ---- changes
    public long RecordChange(string assetId, string type, string reference, string? extra = null)
    {
        Exec("INSERT INTO changes(asset_id,type,ref,extra,at) VALUES($a,$t,$r,$x,$n)", ("$a", assetId), ("$t", type), ("$r", reference), ("$x", extra), ("$n", Now()));
        return Query("SELECT last_insert_rowid()", r => r.GetInt64(0)).First();
    }

    public IReadOnlyList<ChangeRecord> ListChanges(int limit = 500) =>
        Query("SELECT id,asset_id,type,ref,extra,undone,at FROM changes ORDER BY id DESC LIMIT $l",
            r => new ChangeRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5) == 1, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6))), ("$l", limit));

    public ChangeRecord? GetChange(long id) =>
        Query("SELECT id,asset_id,type,ref,extra,undone,at FROM changes WHERE id=$i",
            r => new ChangeRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5) == 1, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6))), ("$i", id)).FirstOrDefault();

    public void MarkUndone(long id) => Exec("UPDATE changes SET undone=1 WHERE id=$i", ("$i", id));

    // ---- uploaded
    public void RecordUpload(string sha1, string assetId) =>
        Exec("INSERT OR REPLACE INTO uploaded VALUES($s,$a,$t)", ("$s", sha1), ("$a", assetId), ("$t", Now()));

    public bool WasUploaded(string sha1) =>
        Query("SELECT 1 FROM uploaded WHERE sha1=$s", r => 1, ("$s", sha1)).Count > 0;

    // ---- kv
    public string? GetKv(string k) => Query("SELECT v FROM kv WHERE k=$k", r => r.GetString(0), ("$k", k)).FirstOrDefault();
    public void SetKv(string k, string v) => Exec("INSERT OR REPLACE INTO kv VALUES($k,$v)", ("$k", k), ("$v", v));

    public void Dispose() => _c.Dispose();
}
