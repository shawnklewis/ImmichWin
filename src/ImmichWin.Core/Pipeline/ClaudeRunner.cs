using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImmichWin.Pipeline;

public sealed record ClassificationResult(IReadOnlyList<string> Tags, IReadOnlyList<string> Albums, bool Develop, string Notes);

public class ClassificationException(string message) : Exception(message);

/// <summary>Claude answered, but not with usable JSON. Deterministic, so not worth retrying.</summary>
public sealed class ClassificationFormatException(string message) : ClassificationException(message);

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string? cwd, TimeSpan timeout, CancellationToken ct);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string? cwd, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        if (cwd != null) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        { throw new ClassificationException($"Cannot start '{exe}': {e.Message}"); }
        p.StandardInput.Close();
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new ClassificationException("Process timed out");
        }
        return new ProcessResult(p.ExitCode, await so, await se);
    }
}

public static class ResultParser
{
    public const int MaxTags = 10, MaxTagLen = 40, MaxAlbums = 2;
    static readonly Regex Fence = new(@"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline);
    static readonly Regex BadChars = new(@"[^\p{L}\p{N} _\-]");

    public static ClassificationResult Parse(string stdout)
    {
        var json = ExtractJson(stdout) ?? throw new ClassificationFormatException("No JSON object in Claude output");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new ClassificationFormatException("Malformed JSON: " + e.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ClassificationFormatException("JSON is not an object");
            var tags = Strings(root, "tags").Select(CleanTag).Where(t => t != null).Select(t => "ai/" + t)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxTags).ToList();
            var albums = Strings(root, "albums").Select(a => Clean(a, 60)).Where(a => a.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxAlbums).ToList();
            var develop = root.TryGetProperty("develop", out var d) && d.ValueKind == JsonValueKind.True;
            var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? Clean(n.GetString()!, 500) : "";
            return new ClassificationResult(tags!, albums, develop, notes);
        }
    }

    static string? ExtractJson(string s)
    {
        var m = Fence.Match(s);
        if (m.Success) return m.Groups[1].Value;
        var a = s.IndexOf('{'); var b = s.LastIndexOf('}');
        return a >= 0 && b > a ? s[a..(b + 1)] : null;
    }

    static IEnumerable<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : [];

    static string Clean(string s, int max)
    {
        var c = Regex.Replace(BadChars.Replace(s, ""), @"\s+", " ").Trim();
        return c.Length > max ? c[..max].Trim() : c;
    }

    static string? CleanTag(string s)
    {
        var c = Clean(s, 1000).ToLowerInvariant();
        if (c.Length == 0 || c.Length > MaxTagLen) return null;
        return c;
    }
}

public sealed class ClaudeRunner(IProcessRunner runner, Func<string> command, Func<string> promptTemplate)
{
    public async Task<ClassificationResult> ClassifyAsync(string imagePath, CancellationToken ct = default)
    {
        var prompt = promptTemplate().Replace("{path}", imagePath);
        var args = new[] { "-p", prompt, "--allowedTools", "Read", "--output-format", "text" };
        var res = await runner.RunAsync(command(), args, Path.GetDirectoryName(imagePath), TimeSpan.FromMinutes(3), ct);
        if (res.ExitCode != 0) throw new ClassificationException($"claude exited {res.ExitCode}: {Truncate(string.IsNullOrWhiteSpace(res.Stderr) ? res.Stdout : res.Stderr)}");
        return ResultParser.Parse(res.Stdout);
    }

    static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
