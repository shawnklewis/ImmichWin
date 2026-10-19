using System.Text.Json;
using ImmichWin.Api;
using ImmichWin.Cache;
using ImmichWin.Data;
using ImmichWin.Settings;
using ImmichWin.Upload;

namespace ImmichWin.Pipeline;

public sealed class Applier(IImmichApi api, StateDb db)
{
    public const int MaxNewAlbumsPerRun = 3;
    int _newAlbums;

    /// <summary>Call at the start of each poll / manual run / approval: the new-album limit is per run.</summary>
    public void ResetRunBudget() => _newAlbums = 0;

    /// <summary>Applies tags/albums. Only links the pipeline actually created are recorded, so Undo never touches the user's own.</summary>
    public async Task ApplyAsync(AssetDto asset, ClassificationResult r, CancellationToken ct = default)
    {
        var existingTags = asset.Tags?.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var name in r.Tags)
        {
            var tag = await api.UpsertTagAsync(name, ct);
            var added = await api.TagAssetsAsync(tag.Id, [asset.Id], ct);
            if (added && !existingTags.Contains(name) && !existingTags.Contains(tag.Value))
                db.RecordChange(asset.Id, "tag", tag.Id, name);
        }
        if (r.Albums.Count == 0) return;
        var albums = await api.GetAlbumsAsync(ct);
        foreach (var name in r.Albums)
        {
            var album = albums.FirstOrDefault(a => string.Equals(a.AlbumName, name, StringComparison.OrdinalIgnoreCase));
            if (album == null)
            {
                if (_newAlbums >= MaxNewAlbumsPerRun) continue;
                album = await api.CreateAlbumAsync(name, ct);
                _newAlbums++;
            }
            if (await api.AddToAlbumAsync(album.Id, [asset.Id], ct))
                db.RecordChange(asset.Id, "album", album.Id, album.AlbumName);
        }
    }

    /// <summary>Applies a queued ("pending") classification after the user approves it.</summary>
    public async Task ApprovePendingAsync(long changeId, CancellationToken ct = default)
    {
        var c = db.GetChange(changeId);
        if (c is not { Type: "pending", Undone: false }) return;
        db.MarkUndone(changeId);                       // resolve first: a double-click must not apply twice
        ResetRunBudget();
        var result = JsonSerializer.Deserialize<ClassificationResult>(c.Ref)!;
        await ApplyAsync(new AssetDto { Id = c.AssetId }, result, ct);
    }

    public void RejectPending(long changeId) => db.MarkUndone(changeId);

    public async Task UndoAsync(long changeId, CancellationToken ct = default)
    {
        var c = db.GetChange(changeId);
        if (c == null || c.Undone) return;
        switch (c.Type)
        {
            case "tag": await api.UntagAssetsAsync(c.Ref, [c.AssetId], ct); break;
            case "album": await api.RemoveFromAlbumAsync(c.Ref, [c.AssetId], ct); break;
            case "asset_created": await api.DeleteAssetsAsync([c.Ref], ct); break;
        }
        db.MarkUndone(changeId);
    }
}

public sealed class Developer(IProcessRunner runner, AssetCache cache, IImmichApi api, Uploader uploader, StateDb db, Func<string?> template, string outputDir)
{
    public string OutputDir => outputDir;

    /// <summary>Runs the configured develop command (RAW in, JPEG out; no shell) and uploads the result. Returns the new asset id, or null if skipped.</summary>
    public async Task<string?> DevelopAsync(AssetDto asset, CancellationToken ct = default)
    {
        var tpl = template();
        if (string.IsNullOrWhiteSpace(tpl)) return null;
        AssetCache.RequireSafeId(asset.Id);
        var input = await cache.GetOriginalAsync(asset.Id, asset.OriginalFileName, api, ct);
        Directory.CreateDirectory(outputDir);
        var output = Path.Combine(outputDir, asset.Id + ".jpg");     // never derived from the server-supplied file name
        var (exe, args) = CommandTemplate.Build(tpl, input, output);
        var res = await runner.RunAsync(exe, args, outputDir, TimeSpan.FromMinutes(10), ct);
        if (res.ExitCode != 0) throw new ClassificationException($"develop command exited {res.ExitCode}");
        if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new ClassificationException("develop command produced no output");
        var up = await uploader.UploadFileAsync(output, ct);
        if (up is not UploadOutcome.Uploaded u) return null;
        db.RecordChange(asset.Id, "asset_created", u.AssetId, output);   // record first: this asset must never re-enter the pipeline
        var tag = await api.UpsertTagAsync(PipelineWorker.DevelopedTag, ct);
        await api.TagAssetsAsync(tag.Id, [u.AssetId], ct);
        return u.AssetId;
    }
}

public sealed class PipelineWorker(
    IImmichApi api, AssetCache cache, ClaudeRunner claude, Applier applier, Developer developer, StateDb db, Func<AppSettings> settings)
{
    public const string DevelopedTag = "ai/developed";
    const int MaxAttempts = 3;
    static readonly TimeSpan MaxWindowHold = TimeSpan.FromHours(6);
    public event Action<string>? Log;
    CancellationTokenSource? _cts;
    Task? _loop;
    int _pending;

    public void Start()
    {
        RecoverStaleClaims();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Assets left "claimed" by a process that was killed mid-work become retryable.</summary>
    public void RecoverStaleClaims() => db.RecoverStaleClaims();

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop != null) try { await _loop; } catch (OperationCanceledException) { }
    }

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (settings().AutoClassify) await PollOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log?.Invoke("Poll failed: " + e.Message); }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, settings().PollMinutes)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One poll: find assets the server created since the last poll and process each.
    /// All times are the server's own (asset createdAt), so a skewed PC clock cannot hide new photos.
    /// The first ever poll only records a baseline (new assets only).</summary>
    public async Task<int> PollOnceAsync(CancellationToken ct = default)
    {
        _pending = 0;
        applier.ResetRunBudget();
        var last = db.GetKv("lastSeen");
        if (last == null)
        {
            var latest = await api.SearchAsync(new SearchQuery { Size = 1, Order = "desc" }, ct);
            var baseline = latest.Items.FirstOrDefault()?.CreatedAt ?? DateTimeOffset.UtcNow;
            db.SetKv("lastSeen", baseline.ToString("o"));
            return 0;
        }
        var lastSeen = DateTimeOffset.Parse(last);
        var after = lastSeen.AddMinutes(-1);
        var assets = new List<AssetDto>();
        var page = 1;
        while (true)
        {
            var p = await api.SearchAsync(new SearchQuery { Page = page, Size = 100, CreatedAfter = after, Order = "asc", WithExif = false }, ct);
            assets.AddRange(p.Items);
            if (p.NextPage == null || p.Items.Count == 0) break;
            page++;
        }
        var done = 0;
        var capped = false;
        foreach (var a in assets)
        {
            if (db.ProcessedToday() >= settings().DailyCap) { Log?.Invoke("Daily cap reached"); capped = true; break; }
            if (await ProcessAsync(a, ct)) done++;
        }
        if (capped) return done;                      // window stays where it was; resume tomorrow
        var newest = assets.Select(a => a.CreatedAt).Where(d => d != null).Select(d => d!.Value).DefaultIfEmpty(lastSeen).Max();
        if (_pending == 0) { db.SetKv("lastSeen", Max(lastSeen, newest).ToString("o")); db.SetKv("pendingSince", ""); }
        else
        {
            // keep the window open while some assets still need a retry, but not forever
            var since = db.GetKv("pendingSince");
            if (string.IsNullOrEmpty(since)) db.SetKv("pendingSince", DateTimeOffset.UtcNow.ToString("o"));
            else if (DateTimeOffset.UtcNow - DateTimeOffset.Parse(since) > MaxWindowHold)
            { db.SetKv("lastSeen", Max(lastSeen, newest).ToString("o")); db.SetKv("pendingSince", ""); }
        }
        return done;
    }

    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>Manual / backfill entry point. Ignores the daily cap on purpose (user-initiated).</summary>
    public async Task<int> ProcessAssetsAsync(IEnumerable<AssetDto> assets, CancellationToken ct = default)
    {
        applier.ResetRunBudget();
        var n = 0;
        foreach (var a in assets) if (await ProcessAsync(a, ct)) n++;
        return n;
    }

    public async Task<bool> ProcessAsync(AssetDto a, CancellationToken ct)
    {
        if (a.IsTrashed || a.Type != "IMAGE") return false;
        if (a.Tags?.Any(t => t.Name == DevelopedTag || t.Value == DevelopedTag) == true) return false;
        if (db.IsPipelineCreated(a.Id)) return false;          // search results carry no tags, so also check our own records
        if (!db.TryClaim(a.Id)) return false;
        cache.Pin(a.Id);                                        // keep its files out of cache eviction while we use them
        try
        {
            var preview = await cache.GetPreviewAsync(a.Id, api, ct);
            var result = await claude.ClassifyAsync(preview, ct);
            if (settings().ReviewBeforeApply)
                db.RecordChange(a.Id, "pending", JsonSerializer.Serialize(result), null);
            else
                await applier.ApplyAsync(a, result, ct);
            if (result.Develop && a.IsRaw && !settings().ReviewBeforeApply)
                await developer.DevelopAsync(a, ct);
            db.SetProcessed(a.Id, "done");
            Log?.Invoke($"Classified {a.OriginalFileName}: {string.Join(", ", result.Tags)}");
            return true;
        }
        catch (OperationCanceledException) { db.SetRetry(a.Id, null, countAttempt: false); throw; }
        catch (Exception e) when (e is ClassificationFormatException or ArgumentException)
        {
            db.SetProcessed(a.Id, "failed", e.Message);        // deterministic: retrying would give the same answer
            Log?.Invoke($"Failed {a.OriginalFileName}: {e.Message}");
            return false;
        }
        catch (Exception e)
        {
            // Outages (unreachable / preview not generated yet) are not the asset's fault: retry without counting.
            var free = e is CacheMissException or ImmichException { Kind: ErrorKind.Unreachable or ErrorKind.NotFound };
            if (!free && db.Attempts(a.Id) + 1 >= MaxAttempts) db.SetProcessed(a.Id, "failed", e.Message);
            else { db.SetRetry(a.Id, e.Message, countAttempt: !free); _pending++; }
            Log?.Invoke($"{(free ? "Waiting" : "Failed")} {a.OriginalFileName}: {e.Message}");
            return false;
        }
        finally { cache.Unpin(a.Id); }
    }
}
