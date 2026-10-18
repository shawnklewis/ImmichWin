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
    public void ResetRunBudget() => _newAlbums = 0;

    /// <summary>Applies tags/albums and records each link so it can be undone.</summary>
    public async Task ApplyAsync(AssetDto asset, ClassificationResult r, CancellationToken ct = default)
    {
        var existingTags = asset.Tags?.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var name in r.Tags)
        {
            var tag = await api.UpsertTagAsync(name, ct);
            await api.TagAssetsAsync(tag.Id, [asset.Id], ct);
            if (!existingTags.Contains(name) && !existingTags.Contains(tag.Value))
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
            await api.AddToAlbumAsync(album.Id, [asset.Id], ct);
            db.RecordChange(asset.Id, "album", album.Id, album.AlbumName);
        }
    }

    /// <summary>Applies a queued ("pending") classification after the user approves it.</summary>
    public async Task ApprovePendingAsync(long changeId, CancellationToken ct = default)
    {
        var c = db.GetChange(changeId);
        if (c is not { Type: "pending", Undone: false }) return;
        var result = JsonSerializer.Deserialize<ClassificationResult>(c.Ref)!;
        await ApplyAsync(new AssetDto { Id = c.AssetId }, result, ct);
        db.MarkUndone(changeId);
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

    /// <summary>Runs the configured develop command (RAW in, JPEG out) and uploads the result. Returns new asset id, or null if skipped.</summary>
    public async Task<string?> DevelopAsync(AssetDto asset, CancellationToken ct = default)
    {
        var tpl = template();
        if (string.IsNullOrWhiteSpace(tpl)) return null;
        var input = await cache.GetOriginalAsync(asset.Id, asset.OriginalFileName, api, ct);
        Directory.CreateDirectory(outputDir);
        var output = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(asset.OriginalFileName) + "_" + asset.Id[..Math.Min(8, asset.Id.Length)] + ".jpg");
        var cmd = tpl.Replace("{input}", $"\"{input}\"").Replace("{output}", $"\"{output}\"");
        var res = await runner.RunAsync("cmd.exe", ["/c", cmd], outputDir, TimeSpan.FromMinutes(10), ct);
        if (res.ExitCode != 0) throw new ClassificationException($"develop command exited {res.ExitCode}");
        if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new ClassificationException("develop command produced no output");
        var up = await uploader.UploadFileAsync(output, ct);
        if (up is not UploadOutcome.Uploaded u) return null;
        var tag = await api.UpsertTagAsync("ai/developed", ct);
        await api.TagAssetsAsync(tag.Id, [u.AssetId], ct);
        db.RecordChange(asset.Id, "asset_created", u.AssetId, output);
        return u.AssetId;
    }
}

public sealed class PipelineWorker(
    IImmichApi api, AssetCache cache, ClaudeRunner claude, Applier applier, Developer developer, StateDb db, Func<AppSettings> settings)
{
    int _notReady;
    public const string DevelopedTag = "ai/developed";
    public event Action<string>? Log;
    CancellationTokenSource? _cts;
    Task? _loop;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

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

    /// <summary>One poll: find assets uploaded since the last poll and process each. First ever poll only records "now" (new assets only).</summary>
    public async Task<int> PollOnceAsync(CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow; _notReady = 0;
        var last = db.GetKv("lastSeen");
        if (last == null) { db.SetKv("lastSeen", started.ToString("o")); return 0; }
        var after = DateTimeOffset.Parse(last).AddMinutes(-1);
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
        foreach (var a in assets)
        {
            if (db.ProcessedToday() >= settings().DailyCap) { Log?.Invoke("Daily cap reached"); return done; }
            if (await ProcessAsync(a, ct)) done++;
        }
        // Keep the window open while previews are still being generated server-side (up to 6h).
        if (_notReady == 0 || started - after > TimeSpan.FromHours(6)) db.SetKv("lastSeen", started.ToString("o"));
        return done;
    }

    /// <summary>Manual / backfill entry point. Ignores the daily cap on purpose (user-initiated).</summary>
    public async Task<int> ProcessAssetsAsync(IEnumerable<AssetDto> assets, CancellationToken ct = default)
    {
        var n = 0;
        foreach (var a in assets) if (await ProcessAsync(a, ct)) n++;
        return n;
    }

    public async Task<bool> ProcessAsync(AssetDto a, CancellationToken ct)
    {
        if (a.IsTrashed || a.Type != "IMAGE") return false;
        if (a.Tags?.Any(t => t.Name == DevelopedTag || t.Value == DevelopedTag) == true) return false;
        if (!db.TryClaim(a.Id)) return false;
        try
        {
            applier.ResetRunBudget();
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
        catch (OperationCanceledException) { db.Release(a.Id); throw; }
        catch (Exception e) when (e is ClassificationException or ImmichException or CacheMissException or IOException)
        {
            // Unreachable server / transient problems: release so the next poll retries; hard failures are marked.
            if (e is ImmichException { Kind: ErrorKind.NotFound }) { _notReady++; db.Release(a.Id); }
            else if (e is ImmichException { Kind: ErrorKind.Unreachable } or CacheMissException) db.Release(a.Id);
            else db.SetProcessed(a.Id, "failed", e.Message);
            Log?.Invoke($"Failed {a.OriginalFileName}: {e.Message}");
            return false;
        }
    }
}
