using System.Security.Cryptography;
using ImmichWin.Api;
using ImmichWin.Data;

namespace ImmichWin.Upload;

public abstract record UploadOutcome
{
    public sealed record Uploaded(string AssetId) : UploadOutcome;
    public sealed record Duplicate(string? AssetId) : UploadOutcome;
    public sealed record Failed(string Reason) : UploadOutcome;
}

public sealed class Uploader(IImmichApi api, StateDb db)
{
    /// <summary>Uploads one local file. Never modifies or deletes the local file.</summary>
    public async Task<UploadOutcome> UploadFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new UploadOutcome.Failed("File not found");
            if (fi.Length == 0) return new UploadOutcome.Failed("File is empty");

            string sha1;
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                sha1 = Convert.ToHexString(await SHA1.HashDataAsync(fs, ct)).ToLowerInvariant();

            if (db.WasUploaded(sha1)) return new UploadOutcome.Duplicate(null);
            var check = await api.CheckDuplicatesAsync([new DuplicateCheck("f", sha1)], ct);
            if (check.TryGetValue("f", out var dup) && dup)
            {
                db.RecordUpload(sha1, "existing");
                return new UploadOutcome.Duplicate(null);
            }

            var res = await api.UploadAsync(new UploadRequest(path, sha1, fi.CreationTimeUtc, fi.LastWriteTimeUtc), ct);
            db.RecordUpload(sha1, res.AssetId);
            return res.Duplicate ? new UploadOutcome.Duplicate(res.AssetId) : new UploadOutcome.Uploaded(res.AssetId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new UploadOutcome.Failed(e.Message); }
        catch (ImmichException e) { return new UploadOutcome.Failed(e.Message); }
    }
}

public sealed class FolderWatcher : IDisposable
{
    readonly List<FileSystemWatcher> _watchers = [];
    readonly Uploader _uploader;
    readonly string[] _ignoreRoots;
    readonly int _stableDelayMs;
    readonly List<string> _folders = [];
    public event Action<string, UploadOutcome>? Result;

    static readonly HashSet<string> Exts = [".jpg", ".jpeg", ".png", ".heic", ".webp", ".gif", ".mp4", ".mov",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2"];

    public FolderWatcher(IEnumerable<string> folders, Uploader uploader, IEnumerable<string> ignoreRoots, int stableDelayMs = 2000)
    {
        _uploader = uploader;
        _stableDelayMs = stableDelayMs;
        _ignoreRoots = ignoreRoots.Select(Path.GetFullPath).ToArray();
        foreach (var f in folders.Where(Directory.Exists))
        {
            var w = new FileSystemWatcher(f) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            _folders.Add(f);
            w.Created += (_, e) => _ = HandlePathAsync(e.FullPath);
            w.Renamed += (_, e) => _ = HandlePathAsync(e.FullPath);
            w.Error += (_, _) => _ = RescanAsync();      // buffer overflow etc.: look at everything again (uploads are de-duplicated)
            _watchers.Add(w);
        }
    }

    public static bool ShouldIgnore(string path, IEnumerable<string> ignoreRoots)
    {
        var full = Path.GetFullPath(path);
        if (!Exts.Contains(Path.GetExtension(full).ToLowerInvariant())) return true;
        return ignoreRoots.Any(r => full.StartsWith(Path.GetFullPath(r), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Waits until the file's size stops changing and it can be opened.</summary>
    public static async Task<bool> WaitStableAsync(string path, int tries = 900, int delayMs = 2000)
    {
        long last = -1;
        for (var i = 0; i < tries; i++)
        {
            try
            {
                var len = new FileInfo(path).Length;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (len == last && len > 0) return true;
                last = len;
            }
            catch (IOException) { last = -1; }
            catch (UnauthorizedAccessException) { last = -1; }
            await Task.Delay(delayMs);
        }
        return false;
    }

    async Task HandlePathAsync(string path)
    {
        try
        {
            if (Directory.Exists(path))                    // a populated folder was moved/copied in: no per-file events are raised for its contents
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) await HandleFileAsync(file);
                return;
            }
            await HandleFileAsync(path);
        }
        catch (Exception e) { Result?.Invoke(path, new UploadOutcome.Failed(e.Message)); }
    }

    async Task RescanAsync()
    {
        foreach (var f in _folders.ToList()) await HandlePathAsync(f);
    }

    async Task HandleFileAsync(string path)
    {
        if (ShouldIgnore(path, _ignoreRoots)) return;
        if (!await WaitStableAsync(path, delayMs: _stableDelayMs)) { Result?.Invoke(path, new UploadOutcome.Failed("File never became readable")); return; }
        Result?.Invoke(path, await _uploader.UploadFileAsync(path));
    }

    public void Dispose() { foreach (var w in _watchers) w.Dispose(); }
}
