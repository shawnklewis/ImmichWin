using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ImmichWin.Api;
using ImmichWin.Data;

namespace ImmichWin.Cache;

public sealed class CacheMissException(string id) : Exception($"Asset {id} is not cached and the server is unreachable");

public sealed partial class AssetCache(StateDb db, string root, Func<long> limitBytes)
{
    readonly HashSet<string> _pinned = [];
    readonly object _lock = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")] private static partial Regex SafeIdRx();

    /// <summary>Asset ids become file names; anything but plain id characters is rejected (path traversal guard).</summary>
    public static string RequireSafeId(string id) =>
        SafeIdRx().IsMatch(id ?? "") ? id! : throw new ArgumentException("Unsafe asset id", nameof(id));

    /// <summary>Keeps only a short alphanumeric extension from a (server-supplied) file name.</summary>
    public static string SafeExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "");
        var clean = new string(ext.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
        return clean.Length == 0 ? ".bin" : "." + clean;
    }

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
        Fetch(id, "thumb", ".jpg", () => api.GetThumbnailAsync(id, ThumbSize.Thumbnail, ct), ct);

    public Task<string> GetPreviewAsync(string id, IImmichApi api, CancellationToken ct = default) =>
        Fetch(id, "preview", ".jpg", () => api.GetThumbnailAsync(id, ThumbSize.Preview, ct), ct);

    public Task<string> GetOriginalAsync(string id, string fileName, IImmichApi api, CancellationToken ct = default) =>
        Fetch(id, "original", SafeExtension(fileName), () => api.GetOriginalAsync(id, ct), ct);

    async Task<string> Fetch(string id, string kind, string ext, Func<Task<Stream>> download, CancellationToken ct)
    {
        RequireSafeId(id);
        if (TryGetCached(id, kind, out var hit)) return hit;
        var gate = _gates.GetOrAdd(id + "|" + kind, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryGetCached(id, kind, out hit)) return hit;   // another caller finished while we waited
            Stream s;
            try { s = await download(); }
            catch (ImmichException e) when (e.Kind == ErrorKind.Unreachable) { throw new CacheMissException(id); }
            await using var _ = s;
            var dir = Path.Combine(root, kind);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, id + ext);
            var tmp = path + ".part";
            await using (var f = File.Create(tmp)) await s.CopyToAsync(f, ct);
            File.Move(tmp, path, overwrite: true);
            db.PutCache(id, kind, path, new FileInfo(path).Length);
            Evict(id);
            return path;
        }
        finally { gate.Release(); }
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
            if (File.Exists(item.Path)) continue;   // still there (in use): keep the row so size accounting stays honest
            db.RemoveCache(item.Id, item.Kind);
        }
    }
}
