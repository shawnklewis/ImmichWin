using ImmichWin.Api;
using ImmichWin.Cache;
using ImmichWin.Data;
using ImmichWin.Settings;

namespace ImmichWin.Core.Tests;

sealed class FakeApi : IImmichApi
{
    public int ThumbCalls; public bool Down; public int Size = 10;
    public Task<UserInfo> ValidateAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct = default) => Down ? throw new ImmichException(ErrorKind.Unreachable, "down") : Task.FromResult(Pages.Count > 0 ? Pages.Dequeue() : new SearchPage([], null));
    public Queue<SearchPage> Pages = new();
    public Task<Stream> GetThumbnailAsync(string id, ThumbSize size, CancellationToken ct = default)
    {
        ThumbCalls++;
        if (Down) throw new ImmichException(ErrorKind.Unreachable, "down");
        return Task.FromResult<Stream>(new MemoryStream(new byte[Size]));
    }
    public Task<Stream> GetOriginalAsync(string id, CancellationToken ct = default) => GetThumbnailAsync(id, ThumbSize.Preview, ct);
    public List<AlbumDto> Albums = [];
    public Task<IReadOnlyList<AlbumDto>> GetAlbumsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AlbumDto>>(Albums);
    public Task<AlbumDto> CreateAlbumAsync(string name, CancellationToken ct = default) { var a = new AlbumDto("alb-" + name, name, 0, null); Albums.Add(a); Created.Add(name); return Task.FromResult(a); }
    public List<string> Created = [];
    public List<(string album, string asset)> AlbumAdds = [], AlbumRemoves = [];
    public Task AddToAlbumAsync(string albumId, IEnumerable<string> ids, CancellationToken ct = default) { AlbumAdds.AddRange(ids.Select(i => (albumId, i))); return Task.CompletedTask; }
    public Task RemoveFromAlbumAsync(string albumId, IEnumerable<string> ids, CancellationToken ct = default) { AlbumRemoves.AddRange(ids.Select(i => (albumId, i))); return Task.CompletedTask; }
    public List<TagDto> Tags = [];
    public Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TagDto>>(Tags);
    public Task<TagDto> UpsertTagAsync(string name, CancellationToken ct = default) { var t = Tags.FirstOrDefault(x => x.Name == name) ?? new TagDto("tag-" + name, name, name); if (!Tags.Contains(t)) Tags.Add(t); return Task.FromResult(t); }
    public List<(string tag, string asset)> Tagged = [], Untagged = [];
    public Task TagAssetsAsync(string tagId, IEnumerable<string> ids, CancellationToken ct = default) { Tagged.AddRange(ids.Select(i => (tagId, i))); return Task.CompletedTask; }
    public Task UntagAssetsAsync(string tagId, IEnumerable<string> ids, CancellationToken ct = default) { Untagged.AddRange(ids.Select(i => (tagId, i))); return Task.CompletedTask; }
    public HashSet<string> KnownSha = [];
    public Task<IReadOnlyDictionary<string, bool>> CheckDuplicatesAsync(IEnumerable<DuplicateCheck> items, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, bool>>(items.ToDictionary(i => i.ClientId, i => KnownSha.Contains(i.Sha1)));
    public int Uploads;
    public Task<UploadResult> UploadAsync(UploadRequest r, CancellationToken ct = default) { Uploads++; return Task.FromResult(new UploadResult("up" + Uploads, false)); }
    public List<string> Deleted = [];
    public Task DeleteAssetsAsync(IEnumerable<string> ids, CancellationToken ct = default) { Deleted.AddRange(ids); return Task.CompletedTask; }
}

public class SettingsTests
{
    [Fact]
    public void Roundtrip_and_corrupt_file_gives_defaults()
    {
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var store = new JsonSettingsStore(p);
        store.Save(new AppSettings { PollMinutes = 7, WatchFolders = ["C:\\x"] });
        Assert.Equal(7, store.Load().PollMinutes);
        Assert.DoesNotContain("apiKey", File.ReadAllText(p), StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(p, "{{{ not json");
        Assert.Equal(3, store.Load().PollMinutes);
    }
}

public class StateDbTests
{
    [Fact]
    public void TryClaim_is_idempotent_and_persists_across_reopen()
    {
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        using (var db = new StateDb(p)) { Assert.True(db.TryClaim("a1")); Assert.False(db.TryClaim("a1")); db.RecordChange("a1", "tag", "t1"); }
        using var db2 = new StateDb(p);
        Assert.False(db2.TryClaim("a1"));
        Assert.Single(db2.ListChanges());
    }
}

public class AssetCacheTests
{
    static (AssetCache c, StateDb db, string root) Make(long limit)
    {
        var root = Path.Combine(Path.GetTempPath(), "ac" + Guid.NewGuid());
        var db = new StateDb(":memory:");
        return (new AssetCache(db, root, () => limit), db, root);
    }

    [Fact]
    public async Task Second_call_is_served_from_disk()
    {
        var (c, _, _) = Make(1000); var api = new FakeApi();
        var p1 = await c.GetThumbAsync("a", api); var p2 = await c.GetThumbAsync("a", api);
        Assert.Equal(p1, p2); Assert.Equal(1, api.ThumbCalls);
    }

    [Fact]
    public async Task Unreachable_cached_ok_uncached_throws()
    {
        var (c, _, _) = Make(1000); var api = new FakeApi();
        await c.GetThumbAsync("a", api); api.Down = true;
        Assert.True(File.Exists(await c.GetThumbAsync("a", api)));
        await Assert.ThrowsAsync<CacheMissException>(() => c.GetThumbAsync("b", api));
    }

    [Fact]
    public async Task Eviction_removes_oldest_but_never_pinned_or_new()
    {
        var (c, db, _) = Make(25); var api = new FakeApi(); // each item 10 bytes
        await c.GetThumbAsync("a", api); c.Pin("a");
        await c.GetThumbAsync("b", api);
        await c.GetThumbAsync("c", api); // 30 > 25 -> evict b (a pinned, c new)
        Assert.NotNull(db.GetCache("a", "thumb"));
        Assert.Null(db.GetCache("b", "thumb"));
        Assert.NotNull(db.GetCache("c", "thumb"));
    }
}
