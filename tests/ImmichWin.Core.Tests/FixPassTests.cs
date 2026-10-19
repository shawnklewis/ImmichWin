using ImmichWin.Api;
using ImmichWin.Cache;
using ImmichWin.Data;
using ImmichWin.Pipeline;
using ImmichWin.Settings;
using ImmichWin.Upload;

namespace ImmichWin.Core.Tests;

/// <summary>Regression tests for the findings of the final branch review.</summary>
public class FixPassTests
{
    sealed class H : IDisposable
    {
        public FakeApi Api = new(); public StateDb Db = new(":memory:"); public AppSettings S = new();
        public AssetCache Cache; public Applier Ap; public PipelineWorker W; public Developer Dev;
        public FakeRunner Claude; public List<(string exe, IReadOnlyList<string> args)> DevCalls = [];
        public string Json = "{\"tags\":[\"dog\"],\"albums\":[]}";
        public Func<string>? NextJson; public Action? DuringClaude; public long CacheLimit = long.MaxValue;
        public H(string? developTemplate = null)
        {
            S.DevelopCommandTemplate = developTemplate;
            Cache = new AssetCache(Db, Path.Combine(Path.GetTempPath(), "fp" + Guid.NewGuid()), () => CacheLimit);
            Claude = new FakeRunner((_, _) => { DuringClaude?.Invoke(); return new ProcessResult(0, NextJson?.Invoke() ?? Json, ""); });
            Ap = new Applier(Api, Db);
            var devRunner = new FakeRunner((exe, args) =>
            {
                DevCalls.Add((exe, args));
                if (args.Count >= 2) File.WriteAllText(args[^1], "jpegdata");
                return new ProcessResult(0, "", "");
            });
            Dev = new Developer(devRunner, Cache, Api, new Uploader(Api, Db), Db, () => S.DevelopCommandTemplate, Path.Combine(Path.GetTempPath(), "fpdev" + Guid.NewGuid()));
            W = new PipelineWorker(Api, Cache, new ClaudeRunner(Claude, () => "claude", () => S.ClassifyPrompt), Ap, Dev, Db, () => S);
        }
        public void Dispose() => Db.Dispose();
    }

    static AssetDto Asset(string id, string name = "a.jpg") => new() { Id = id, OriginalFileName = name, Type = "IMAGE" };

    // 1. develop command: no shell, no injection, safe names
    [Fact]
    public void CommandTemplate_substitutes_after_tokenising_so_values_stay_single_args()
    {
        var (exe, args) = CommandTemplate.Build("\"C:\\Program Files\\dev tool.exe\" --in {input} -o {output}", "C:\\a b\\x\" & calc.arw", "C:\\o.jpg");
        Assert.Equal("C:\\Program Files\\dev tool.exe", exe);
        Assert.Equal(["--in", "C:\\a b\\x\" & calc.arw", "-o", "C:\\o.jpg"], args);
    }

    [Fact]
    public async Task Develop_runs_tool_directly_with_hostile_filename_and_id_based_output()
    {
        using var h = new H("devtool.exe {input} {output}");
        h.Claude = h.Claude; // (runner shared)
        var asset = Asset("raw-1", "x\" & echo INJECTED & \".ARW");
        var id = await h.Dev.DevelopAsync(asset);
        Assert.NotNull(id);
        var call = Assert.Single(h.DevCalls);
        Assert.Equal("devtool.exe", call.exe);
        Assert.Equal(2, call.args.Count);
        Assert.Equal("raw-1.jpg", Path.GetFileName(call.args[1]));
        Assert.DoesNotContain("INJECTED", call.args[0]);   // cache extension sanitised
    }

    // 2. developed JPEGs must never re-enter the pipeline, even though search results carry no tags
    [Fact]
    public async Task Pipeline_created_asset_is_never_classified()
    {
        using var h = new H("devtool.exe {input} {output}");
        h.Json = "{\"tags\":[],\"albums\":[],\"develop\":true}";
        Assert.True(await h.W.ProcessAsync(Asset("raw-1", "p.dng"), default));
        var createdId = h.Db.ListChanges().Single(c => c.Type == "asset_created").Ref;
        var before = h.Claude.Calls;
        Assert.False(await h.W.ProcessAsync(Asset(createdId, "p_raw-1.jpg"), default));
        Assert.Equal(before, h.Claude.Calls);
    }

    // 3. undo must not remove the user's own album membership / tags
    [Fact]
    public async Task Existing_album_membership_and_tag_are_not_recorded_for_undo()
    {
        using var h = new H();
        h.Api.Albums.Add(new AlbumDto("alb1", "Beach", 1, null));
        h.Api.AlreadyInAlbum.Add(("alb1", "p1"));
        h.Api.AlreadyTagged.Add(("tag-ai/sea", "p1"));
        await h.Ap.ApplyAsync(Asset("p1"), new ClassificationResult(["ai/sea", "ai/new"], ["Beach"], false, ""));
        var types = h.Db.ListChanges().Select(c => (c.Type, c.Extra)).ToList();
        Assert.DoesNotContain(types, t => t.Type == "album");
        Assert.DoesNotContain(types, t => t.Extra == "ai/sea");
        Assert.Contains(types, t => t.Extra == "ai/new");
    }

    // 4. new-album budget is per run, not per asset
    [Fact]
    public async Task New_album_budget_spans_the_whole_poll()
    {
        using var h = new H(); var n = 0;
        h.NextJson = () => { n++; return "{\"tags\":[],\"albums\":[\"A" + n + "\",\"B" + n + "\"]}"; };
        await h.W.PollOnceAsync();
        h.Api.Pages.Enqueue(new SearchPage([Asset("a1"), Asset("a2"), Asset("a3"), Asset("a4")], null));
        await h.W.PollOnceAsync();
        Assert.Equal(Applier.MaxNewAlbumsPerRun, h.Api.Created.Count);
    }

    // 5. idempotence / retries
    [Fact]
    public async Task Stale_claims_from_a_killed_run_are_recovered()
    {
        using var h = new H();
        Assert.True(h.Db.TryClaim("k1"));          // process died after claiming
        h.W.RecoverStaleClaims();
        Assert.True(await h.W.ProcessAsync(Asset("k1"), default));
    }

    [Fact]
    public async Task Unexpected_exception_does_not_leave_asset_claimed_forever()
    {
        using var h = new H();
        h.DuringClaude = () => throw new InvalidOperationException("boom");
        Assert.False(await h.W.ProcessAsync(Asset("e1"), default));
        Assert.NotEqual("claimed", h.Db.GetProcessedStatus("e1"));
    }

    [Fact]
    public async Task Auth_failure_is_retried_then_marked_failed_after_three_attempts()
    {
        using var h = new H(); h.Api.Unauthorized = true;
        await h.W.ProcessAsync(Asset("u1"), default); Assert.Equal("retry", h.Db.GetProcessedStatus("u1"));
        await h.W.ProcessAsync(Asset("u1"), default); Assert.Equal("retry", h.Db.GetProcessedStatus("u1"));
        await h.W.ProcessAsync(Asset("u1"), default); Assert.Equal("failed", h.Db.GetProcessedStatus("u1"));
        h.Api.Unauthorized = false;
        Assert.False(await h.W.ProcessAsync(Asset("u1"), default));   // permanently failed => not retried again
    }

    [Fact]
    public async Task Retryable_failures_keep_the_poll_window_open()
    {
        using var h = new H(); h.Api.Unauthorized = true;
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        h.Api.Pages.Enqueue(new SearchPage([Asset("b0") with { CreatedAt = t0 }], null));
        await h.W.PollOnceAsync();                                     // baseline = server createdAt of newest asset
        var a = Asset("u1") with { CreatedAt = t0.AddMinutes(5) };
        h.Api.Pages.Enqueue(new SearchPage([a], null));
        await h.W.PollOnceAsync();                                     // fails with 401 => retry
        h.Api.Unauthorized = false;
        h.Api.Pages.Enqueue(new SearchPage([a], null));
        Assert.Equal(1, await h.W.PollOnceAsync());                    // still inside the window, now succeeds
    }

    // 6. poll window uses the server's clock, not the PC's
    [Fact]
    public async Task Poll_window_is_based_on_server_createdAt_not_client_clock()
    {
        using var h = new H();
        var serverNow = DateTimeOffset.UtcNow.AddMinutes(-7);          // server clock is 7 min behind this PC
        h.Api.Pages.Enqueue(new SearchPage([Asset("n0") with { CreatedAt = serverNow }], null));
        await h.W.PollOnceAsync();                                     // baseline
        await h.W.PollOnceAsync();
        Assert.NotNull(h.Api.LastQuery!.CreatedAfter);
        Assert.True(h.Api.LastQuery.CreatedAfter <= serverNow, "window must start at/before the newest server createdAt");
    }

    // 7. cache races
    [Fact]
    public async Task Preview_used_by_pipeline_survives_eviction_triggered_by_ui_fetch()
    {
        using var h = new H(); h.CacheLimit = 15;                      // each item is 10 bytes
        string? previewPath = null;
        h.DuringClaude = () =>
        {
            h.Cache.TryGetCached("p1", "preview", out previewPath);
            h.Cache.GetThumbAsync("other", h.Api).GetAwaiter().GetResult();   // UI fetch forces eviction
        };
        Assert.True(await h.W.ProcessAsync(Asset("p1"), default));
        Assert.True(File.Exists(previewPath));
    }

    [Fact]
    public async Task Failed_delete_keeps_the_cache_row_so_size_accounting_stays_honest()
    {
        using var h = new H(); h.CacheLimit = 15;
        await h.Cache.GetThumbAsync("old", h.Api);
        h.Cache.TryGetCached("old", "thumb", out var oldPath);
        using var lockIt = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.None);
        await h.Cache.GetThumbAsync("new", h.Api);                     // would evict "old", but it is locked
        Assert.NotNull(h.Db.GetCache("old", "thumb"));
    }

    [Fact]
    public async Task Concurrent_fetches_of_same_item_download_once_and_both_succeed()
    {
        using var h = new H();
        var results = await Task.WhenAll(h.Cache.GetThumbAsync("same", h.Api), h.Cache.GetThumbAsync("same", h.Api));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, h.Api.ThumbCalls);
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Unsafe_asset_ids_are_rejected(string id)
    {
        using var h = new H();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Cache.GetThumbAsync(id, h.Api));
    }

    // 8. folder watcher
    [Fact]
    public async Task Watcher_uploads_files_inside_a_folder_moved_into_the_watched_dir()
    {
        var api = new FakeApi(); using var db = new StateDb(":memory:");
        var watched = Path.Combine(Path.GetTempPath(), "w" + Guid.NewGuid()); Directory.CreateDirectory(watched);
        var outside = Path.Combine(Path.GetTempPath(), "o" + Guid.NewGuid()); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "p.jpg"), "pixels");
        using var w = new FolderWatcher([watched], new Uploader(api, db), [], stableDelayMs: 50);
        var done = new TaskCompletionSource();
        w.Result += (_, o) => { if (o is UploadOutcome.Uploaded) done.TrySetResult(); };
        Directory.Move(outside, Path.Combine(watched, "moved"));
        await Task.WhenAny(done.Task, Task.Delay(8000));
        Assert.Equal(1, api.Uploads);
    }

    [Fact]
    public async Task WaitStable_waits_for_a_slow_writer_to_finish()
    {
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4");
        var fs = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read);
        fs.Write(new byte[100]);
        var closer = Task.Run(async () => { await Task.Delay(1200); fs.Dispose(); });
        Assert.True(await FolderWatcher.WaitStableAsync(p, tries: 40, delayMs: 100));
        await closer;
    }
}
