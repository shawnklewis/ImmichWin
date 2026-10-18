using ImmichWin.Api;
using ImmichWin.Cache;
using ImmichWin.Data;
using ImmichWin.Pipeline;
using ImmichWin.Settings;
using ImmichWin.Upload;

namespace ImmichWin.Core.Tests;

sealed class FakeRunner(Func<string, IReadOnlyList<string>, ProcessResult> f) : IProcessRunner
{
    public int Calls;
    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string? cwd, TimeSpan t, CancellationToken ct)
    { Calls++; return Task.FromResult(f(exe, args)); }
}

public class ResultParserTests
{
    [Fact]
    public void Fenced_json_parses()
    {
        var r = ResultParser.Parse("Sure!\n```json\n{\"tags\":[\"Dog\",\"beach\"],\"albums\":[\"Pets\"],\"develop\":true}\n```");
        Assert.Equal(["ai/dog", "ai/beach"], r.Tags);
        Assert.Equal(["Pets"], r.Albums);
        Assert.True(r.Develop);
    }

    [Fact]
    public void Fifty_tags_clamped_to_ten()
    {
        var tags = string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"t{i}\""));
        Assert.Equal(10, ResultParser.Parse($"{{\"tags\":[{tags}]}}").Tags.Count);
    }

    [Fact]
    public void Overlong_and_hostile_tags_are_dropped_or_sanitised()
    {
        var r = ResultParser.Parse("{\"tags\":[\"" + new string('x', 200) + "\",\"a; rm -rf /\\nb\",\"../../etc\"]}");
        Assert.DoesNotContain(r.Tags, t => t.Length > 43);
        Assert.All(r.Tags, t => Assert.Matches(@"^ai/[\p{L}\p{N} _\-]+$", t));
    }

    [Theory]
    [InlineData("no json here")]
    [InlineData("{ not valid json }")]
    public void Malformed_throws(string s) => Assert.Throws<ClassificationException>(() => ResultParser.Parse(s));
}

public class UploaderTests
{
    static string TempFile(string content = "abc")
    { var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg"); File.WriteAllText(p, content); return p; }

    [Fact]
    public async Task Uploads_new_then_skips_duplicate_and_keeps_local_file()
    {
        var api = new FakeApi(); using var db = new StateDb(":memory:"); var u = new Uploader(api, db);
        var f = TempFile();
        Assert.IsType<UploadOutcome.Uploaded>(await u.UploadFileAsync(f));
        Assert.IsType<UploadOutcome.Duplicate>(await u.UploadFileAsync(f));
        Assert.Equal(1, api.Uploads); Assert.True(File.Exists(f));
    }

    [Fact]
    public async Task Server_known_checksum_is_duplicate()
    {
        var api = new FakeApi(); using var db = new StateDb(":memory:"); var u = new Uploader(api, db);
        var f = TempFile("xyz");
        api.KnownSha.Add(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(f))).ToLowerInvariant());
        Assert.IsType<UploadOutcome.Duplicate>(await u.UploadFileAsync(f));
        Assert.Equal(0, api.Uploads);
    }

    [Fact]
    public async Task Zero_byte_and_missing_fail()
    {
        var u = new Uploader(new FakeApi(), new StateDb(":memory:"));
        var f = TempFile("");
        Assert.IsType<UploadOutcome.Failed>(await u.UploadFileAsync(f));
        Assert.IsType<UploadOutcome.Failed>(await u.UploadFileAsync(f + ".nope"));
    }

    [Fact]
    public void Watcher_ignores_pipeline_output_and_non_media()
    {
        var root = Path.Combine(Path.GetTempPath(), "developed");
        Assert.True(FolderWatcher.ShouldIgnore(Path.Combine(root, "a.jpg"), [root]));
        Assert.True(FolderWatcher.ShouldIgnore(@"C:\pics\notes.txt", [root]));
        Assert.False(FolderWatcher.ShouldIgnore(@"C:\pics\a.JPG", [root]));
    }

    [Fact]
    public async Task WaitStable_true_for_settled_file_false_for_zero_byte()
    {
        Assert.True(await FolderWatcher.WaitStableAsync(TempFile(), 5, 20));
        Assert.False(await FolderWatcher.WaitStableAsync(TempFile(""), 3, 20));
    }
}

public class PipelineWorkerTests
{
    const string GoodJson = "{\"tags\":[\"dog\"],\"albums\":[\"Pets\"],\"develop\":false}";

    sealed class Rig : IDisposable
    {
        public FakeApi Api = new(); public StateDb Db = new(":memory:"); public AppSettings S = new();
        public FakeRunner Claude; public PipelineWorker W; public Applier Ap; public AssetCache Cache;
        public Rig(string stdout = GoodJson, int exit = 0)
        {
            Claude = new FakeRunner((_, _) => new ProcessResult(exit, stdout, ""));
            Cache = new AssetCache(Db, Path.Combine(Path.GetTempPath(), "pw" + Guid.NewGuid()), () => long.MaxValue);
            Ap = new Applier(Api, Db);
            var up = new Uploader(Api, Db);
            var dev = new Developer(new FakeRunner((_, _) => new ProcessResult(1, "", "")), Cache, Api, up, Db, () => S.DevelopCommandTemplate, Path.Combine(Path.GetTempPath(), "dev"));
            W = new PipelineWorker(Api, Cache, new ClaudeRunner(Claude, () => "claude", () => S.ClassifyPrompt), Ap, dev, Db, () => S);
        }
        public void Dispose() => Db.Dispose();
    }

    static AssetDto Asset(string id, string name = "a.jpg", List<TagDto>? tags = null, bool trashed = false) =>
        new() { Id = id, OriginalFileName = name, Type = "IMAGE", Tags = tags, IsTrashed = trashed };

    [Fact]
    public async Task First_poll_only_sets_baseline_second_processes_new_asset_once()
    {
        using var r = new Rig();
        Assert.Equal(0, await r.W.PollOnceAsync());
        r.Api.Pages.Enqueue(new SearchPage([Asset("a1")], null));
        Assert.Equal(1, await r.W.PollOnceAsync());
        r.Api.Pages.Enqueue(new SearchPage([Asset("a1")], null)); // overlap window returns it again
        Assert.Equal(0, await r.W.PollOnceAsync());
        Assert.Equal(1, r.Claude.Calls);
        Assert.Contains(r.Api.Tagged, t => t.asset == "a1");
        Assert.Contains(r.Api.AlbumAdds, x => x.asset == "a1");
    }

    [Fact]
    public async Task Restart_with_same_db_does_not_reprocess()
    {
        using var r = new Rig();
        Assert.True(await r.W.ProcessAsync(Asset("a1"), default));
        Assert.False(await r.W.ProcessAsync(Asset("a1"), default));
    }

    [Fact]
    public async Task Developed_tagged_trashed_and_video_assets_are_skipped()
    {
        using var r = new Rig();
        Assert.False(await r.W.ProcessAsync(Asset("a", tags: [new("t", "ai/developed", "ai/developed")]), default));
        Assert.False(await r.W.ProcessAsync(Asset("b", trashed: true), default));
        Assert.False(await r.W.ProcessAsync(Asset("c") with { Type = "VIDEO" }, default));
        Assert.Equal(0, r.Claude.Calls);
    }

    [Fact]
    public async Task Malformed_claude_output_marks_failed_and_does_not_throw()
    {
        using var r = new Rig("garbage");
        Assert.False(await r.W.ProcessAsync(Asset("a1"), default));
        Assert.Equal("failed", r.Db.GetProcessedStatus("a1"));
    }

    [Fact]
    public async Task Unreachable_server_releases_claim_for_retry()
    {
        using var r = new Rig(); r.Api.Down = true;
        Assert.False(await r.W.ProcessAsync(Asset("a1"), default));
        Assert.Null(r.Db.GetProcessedStatus("a1"));
    }

    [Fact]
    public async Task Daily_cap_stops_automatic_polling()
    {
        using var r = new Rig(); r.S.DailyCap = 1;
        await r.W.PollOnceAsync();
        r.Api.Pages.Enqueue(new SearchPage([Asset("a1"), Asset("a2")], null));
        Assert.Equal(1, await r.W.PollOnceAsync());
    }

    [Fact]
    public async Task Undo_removes_only_recorded_links_not_preexisting_user_tag()
    {
        using var r = new Rig();
        // user already had "ai/dog"-valued tag on the asset
        var pre = new List<TagDto> { new("tag-ai/dog", "ai/dog", "ai/dog") };
        await r.W.ProcessAsync(Asset("a1", tags: pre), default);
        var changes = r.Db.ListChanges();
        Assert.DoesNotContain(changes, c => c.Type == "tag");        // not recorded => not undoable
        var album = changes.Single(c => c.Type == "album");
        await r.Ap.UndoAsync(album.Id);
        Assert.Contains(r.Api.AlbumRemoves, x => x.asset == "a1");
        Assert.Empty(r.Api.Untagged);
    }

    [Fact]
    public async Task New_albums_capped_per_run()
    {
        using var r = new Rig("{\"tags\":[],\"albums\":[\"A\",\"B\"]}");
        for (var i = 0; i < 3; i++) await r.Ap.ApplyAsync(Asset("x" + i), new ClassificationResult([], [$"n{i}a", $"n{i}b"], false, ""));
        Assert.Equal(Applier.MaxNewAlbumsPerRun, r.Api.Created.Count);
    }

    [Fact]
    public async Task Review_mode_queues_instead_of_applying()
    {
        using var r = new Rig(); r.S.ReviewBeforeApply = true;
        await r.W.ProcessAsync(Asset("a1"), default);
        Assert.Empty(r.Api.Tagged);
        Assert.Contains(r.Db.ListChanges(), c => c.Type == "pending");
    }

    [Fact]
    public async Task Unconfigured_develop_command_is_skipped_without_error()
    {
        using var r = new Rig("{\"tags\":[],\"albums\":[],\"develop\":true}");
        Assert.True(await r.W.ProcessAsync(Asset("r1", "photo.dng"), default));
        Assert.Equal("done", r.Db.GetProcessedStatus("r1"));
    }
}
