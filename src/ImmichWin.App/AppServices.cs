using ImmichWin.Api;
using ImmichWin.Cache;
using ImmichWin.Data;
using ImmichWin.Pipeline;
using ImmichWin.Settings;
using ImmichWin.Upload;
using Windows.Security.Credentials;

namespace ImmichWin.App;

public sealed class PasswordVaultSecretStore : ISecretStore
{
    const string Resource = "ImmichWin", User = "api-key";
    public string? Get()
    {
        try { var c = new PasswordVault().Retrieve(Resource, User); c.RetrievePassword(); return c.Password; }
        catch { return null; }
    }
    public void Set(string value) { Clear(); new PasswordVault().Add(new PasswordCredential(Resource, User, value)); }
    public void Clear()
    {
        try { var v = new PasswordVault(); v.Remove(v.Retrieve(Resource, User)); } catch { }
    }
}

/// <summary>Single composition root for the app (no DI container needed at this size).</summary>
public sealed class AppServices
{
    public static AppServices I { get; } = new();

    public string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmichWin");
    public JsonSettingsStore SettingsStore { get; }
    public AppSettings Settings { get; private set; }
    public ISecretStore Secrets { get; } = new PasswordVaultSecretStore();
    public StateDb Db { get; }
    public AssetCache Cache { get; }
    public string DevelopedDir => Path.Combine(DataDir, "developed");

    public IImmichApi? Api { get; private set; }
    public PipelineWorker? Worker { get; private set; }
    public Applier? Applier { get; private set; }
    public Uploader? Uploader { get; private set; }
    FolderWatcher? _watcher;
    public event Action<string>? Log;
    public event Action<string, UploadOutcome>? WatchResult;
    public bool IsSignedIn => Api != null;

    AppServices()
    {
        Directory.CreateDirectory(DataDir);
        SettingsStore = new JsonSettingsStore(Path.Combine(DataDir, "settings.json"));
        Settings = SettingsStore.Load();
        Db = new StateDb(Path.Combine(DataDir, "state.db"));
        Cache = new AssetCache(Db, Path.Combine(DataDir, "cache"), () => (long)Settings.CacheLimitMb * 1024 * 1024);
    }

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
        RestartWatcher();
    }

    static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Validates url+key (trying the fallback URL if the primary is unreachable). Persists the key only on success.</summary>
    public async Task<UserInfo> SignInAsync(string url, string key, CancellationToken ct = default)
    {
        var urls = new[] { url, Settings.FallbackUrl }.Where(u => !string.IsNullOrWhiteSpace(u)).Cast<string>().Distinct().ToList();
        ImmichException? last = null;
        foreach (var u in urls)
        {
            var client = new ImmichClient(NewHttp(), u, key);
            try
            {
                var me = await client.ValidateAsync(ct);
                Settings.ServerUrl = u; SettingsStore.Save(Settings);
                Secrets.Set(key);
                Attach(client);
                return me;
            }
            catch (ImmichException e) when (e.Kind == ErrorKind.Unreachable) { last = e; }
        }
        throw last ?? new ImmichException(ErrorKind.Unreachable, "No server URL given");
    }

    /// <summary>Uses the stored key without blocking on the network. Returns false if there is no key.</summary>
    public bool TryRestoreSession()
    {
        var key = Secrets.Get();
        if (string.IsNullOrEmpty(key)) return false;
        Attach(new ImmichClient(NewHttp(), Settings.ServerUrl, key));
        return true;
    }

    void Attach(ImmichClient client)
    {
        _ = Worker?.StopAsync();
        Api = client;
        Applier = new Applier(client, Db);
        Uploader = new Uploader(client, Db);
        var runner = new SystemProcessRunner();
        var claude = new ClaudeRunner(runner, () => Settings.ClaudeCommand, () => Settings.ClassifyPrompt);
        var dev = new Developer(runner, Cache, client, Uploader, Db, () => Settings.DevelopCommandTemplate, DevelopedDir);
        Worker = new PipelineWorker(client, Cache, claude, Applier, dev, Db, () => Settings);
        Worker.Log += m => Log?.Invoke(m);
        Worker.Start();
        RestartWatcher();
    }

    void RestartWatcher()
    {
        _watcher?.Dispose(); _watcher = null;
        if (Uploader == null || Settings.WatchFolders.Count == 0) return;
        _watcher = new FolderWatcher(Settings.WatchFolders, Uploader, [DevelopedDir]);
        _watcher.Result += (p, o) => WatchResult?.Invoke(p, o);
    }

    public void SignOut()
    {
        _ = Worker?.StopAsync();
        _watcher?.Dispose(); _watcher = null;
        Worker = null; Api = null; Applier = null; Uploader = null;
        Secrets.Clear();
    }
}
