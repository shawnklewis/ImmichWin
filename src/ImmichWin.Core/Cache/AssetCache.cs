using ImmichWin.Api;
using ImmichWin.Data;

namespace ImmichWin.Cache;

public sealed class CacheMissException(string id) : Exception($"Asset {id} is not cached and the server is unreachable");

public sealed class AssetCache(StateDb db, string root, Func<long> limitBytes)
{
    readonly HashSet<string> _pinned = [];
    readonly object _lock = new();

    public void Pin(string id) { lock (_lock) _pinned.Add(id); }
    public void Unpin(string id) { lock (_lock) _pinned.Remove(id); }

    public bool TryGetCached(string id, string kind, out string path)
    {
        var c = db.GetCache(id, kind);
        if (c != null && File.Exists(c.Path)) { db.TouchCache(id, kind); path = c.Path; return true; }
        path = "";
        return false;
    }

    public Task<string> GetThumbAsync(string id, IImmichApi api, CancellationToken ct = default) =>
        Fetch(id, "thumb", ".jpg", () => api.GetThumbnailAsync(id, ThumbSize.Thumbnail, ct));

    public Task<string> GetPreviewAsync(string id, IImmichApi api, CancellationToken ct = default) =>
        Fetch(id, "preview", ".jpg", () => api.GetThumbnailAsync(id, ThumbSize.Preview, ct));

    public Task<string> GetOriginalAsync(string id, string fileName, IImmichApi api, CancellationToken ct = default) =>
        Fetch(id, "original", Path.GetExtension(fileName), () => api.GetOriginalAsync(id, ct));

    async Task<string> Fetch(string id, string kind, string ext, Func<Task<Stream>> download)
    {
        if (TryGetCached(id, kind, out var hit)) return hit;
        Stream s;
        try { s = await download(); }
        catch (ImmichException e) when (e.Kind == ErrorKind.Unreachable) { throw new CacheMissException(id); }
        await using var _ = s;
        var dir = Path.Combine(root, kind);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id + (string.IsNullOrEmpty(ext) ? ".bin" : ext));
        await using (var f = File.Create(path)) await s.CopyToAsync(f);
        db.PutCache(id, kind, path, new FileInfo(path).Length);
        Evict(id);
        return path;
    }

    void Evict(string justAdded)
    {
        var limit = limitBytes();
        if (db.CacheBytes() <= limit) return;
        HashSet<string> pinned;
        lock (_lock) pinned = [.._pinned, justAdded];
        foreach (var item in db.CacheOldestFirst())
        {
            if (db.CacheBytes() <= limit) break;
            if (pinned.Contains(item.Id)) continue;
            try { File.Delete(item.Path); } catch { }
            db.RemoveCache(item.Id, item.Kind);
        }
    }
}
