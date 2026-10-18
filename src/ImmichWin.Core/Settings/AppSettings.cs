using System.Text.Json;

namespace ImmichWin.Settings;

public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "http://localhost:2283";
    public string? FallbackUrl { get; set; }
    public int CacheLimitMb { get; set; } = 2048;
    public string ClaudeCommand { get; set; } = "claude";
    public string ClassifyPrompt { get; set; } =
        "Look at the image file at {path}. Reply with ONLY a JSON object: " +
        "{\"tags\":[short descriptive tags],\"albums\":[0-2 album names this photo belongs in],\"develop\":false,\"notes\":\"\"}. " +
        "Set develop to true only if this is an unprocessed RAW-style photo that needs developing.";
    public string? DevelopCommandTemplate { get; set; }
    public bool ReviewBeforeApply { get; set; }
    public bool AutoClassify { get; set; } = true;
    public int PollMinutes { get; set; } = 3;
    public int DailyCap { get; set; } = 200;
    public List<string> WatchFolders { get; set; } = [];
}

public interface ISecretStore
{
    string? Get();
    void Set(string value);
    void Clear();
}

public sealed class JsonSettingsStore(string path)
{
    static readonly JsonSerializerOptions Opt = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opt) ?? new(); }
        catch { return new(); }
    }

    public void Save(AppSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(s, Opt));
    }
}
