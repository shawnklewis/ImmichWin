using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ImmichWin.Api;

public interface IImmichApi
{
    Task<UserInfo> ValidateAsync(CancellationToken ct = default);
    Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct = default);
    Task<Stream> GetThumbnailAsync(string id, ThumbSize size, CancellationToken ct = default);
    Task<Stream> GetOriginalAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<AlbumDto>> GetAlbumsAsync(CancellationToken ct = default);
    Task<AlbumDto> CreateAlbumAsync(string name, CancellationToken ct = default);
    Task<bool> AddToAlbumAsync(string albumId, IEnumerable<string> assetIds, CancellationToken ct = default);
    Task RemoveFromAlbumAsync(string albumId, IEnumerable<string> assetIds, CancellationToken ct = default);
    Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken ct = default);
    Task<TagDto> UpsertTagAsync(string name, CancellationToken ct = default);
    Task<bool> TagAssetsAsync(string tagId, IEnumerable<string> assetIds, CancellationToken ct = default);
    Task UntagAssetsAsync(string tagId, IEnumerable<string> assetIds, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, bool>> CheckDuplicatesAsync(IEnumerable<DuplicateCheck> items, CancellationToken ct = default);
    Task<UploadResult> UploadAsync(UploadRequest r, CancellationToken ct = default);
    Task DeleteAssetsAsync(IEnumerable<string> ids, CancellationToken ct = default);
}

public sealed class ImmichClient : IImmichApi
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly HttpClient _http;

    public ImmichClient(HttpClient http, string baseUrl, string apiKey)
    {
        _http = http;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/");
        _http.DefaultRequestHeaders.Remove("x-api-key");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new ImmichException(ErrorKind.Unreachable, e.Message, e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new ImmichException(ErrorKind.Unreachable, "Timed out", e); }
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            res.Dispose();
            throw new ImmichException(ErrorKind.Unauthorized, "API key rejected");
        }
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            res.Dispose();
            throw new ImmichException(ErrorKind.NotFound, "Not found");
        }
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            throw new ImmichException(ErrorKind.Server, $"{(int)res.StatusCode}: {body}");
        }
        return res;
    }

    async Task<T> GetJson<T>(string url, CancellationToken ct)
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    Task<HttpResponseMessage> Send(HttpMethod m, string url, object? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(m, url);
        if (body != null) req.Content = JsonContent.Create(body, options: Json);
        return SendAsync(req, ct);
    }

    public Task<UserInfo> ValidateAsync(CancellationToken ct = default) => GetJson<UserInfo>("users/me", ct);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["page"] = q.Page, ["size"] = q.Size, ["order"] = q.Order, ["withExif"] = q.WithExif,
            ["createdAfter"] = q.CreatedAfter?.ToString("o"), ["albumIds"] = q.AlbumIds, ["tagIds"] = q.TagIds,
        };
        foreach (var k in body.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) body.Remove(k);
        using var res = await Send(HttpMethod.Post, "search/metadata", body, ct);
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var assets = doc.RootElement.GetProperty("assets");
        var items = assets.GetProperty("items").Deserialize<List<AssetDto>>(Json) ?? [];
        var next = assets.TryGetProperty("nextPage", out var np) && np.ValueKind == JsonValueKind.String ? np.GetString() : null;
        return new SearchPage(items, next);
    }

    public Task<Stream> GetThumbnailAsync(string id, ThumbSize size, CancellationToken ct = default) =>
        Download($"assets/{id}/thumbnail?size={(size == ThumbSize.Preview ? "preview" : "thumbnail")}", ct);

    public Task<Stream> GetOriginalAsync(string id, CancellationToken ct = default) =>
        Download($"assets/{id}/original", ct);

    async Task<Stream> Download(string url, CancellationToken ct)
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        return new MemoryStream(await res.Content.ReadAsByteArrayAsync(ct));
    }

    public async Task<IReadOnlyList<AlbumDto>> GetAlbumsAsync(CancellationToken ct = default) =>
        await GetJson<List<AlbumDto>>("albums", ct);

    public async Task<AlbumDto> CreateAlbumAsync(string name, CancellationToken ct = default)
    {
        using var res = await Send(HttpMethod.Post, "albums", new { albumName = name }, ct);
        return (await res.Content.ReadFromJsonAsync<AlbumDto>(Json, ct))!;
    }

    /// <summary>True if at least one asset was newly added (false when all were already members).</summary>
    public async Task<bool> AddToAlbumAsync(string albumId, IEnumerable<string> assetIds, CancellationToken ct = default)
    { using var res = await Send(HttpMethod.Put, $"albums/{albumId}/assets", new { ids = assetIds }, ct); return await AnySuccess(res, ct); }

    public async Task RemoveFromAlbumAsync(string albumId, IEnumerable<string> assetIds, CancellationToken ct = default)
    { using var _ = await Send(HttpMethod.Delete, $"albums/{albumId}/assets", new { ids = assetIds }, ct); }

    public async Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken ct = default) =>
        await GetJson<List<TagDto>>("tags", ct);

    public async Task<TagDto> UpsertTagAsync(string name, CancellationToken ct = default)
    {
        using var res = await Send(HttpMethod.Put, "tags", new { tags = new[] { name } }, ct);
        var list = await res.Content.ReadFromJsonAsync<List<TagDto>>(Json, ct);
        return list![0];
    }

    public async Task<bool> TagAssetsAsync(string tagId, IEnumerable<string> assetIds, CancellationToken ct = default)
    { using var res = await Send(HttpMethod.Put, $"tags/{tagId}/assets", new { ids = assetIds }, ct); return await AnySuccess(res, ct); }

    static async Task<bool> AnySuccess(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return true;
            return doc.RootElement.EnumerateArray().Any(e => e.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True);
        }
        catch (JsonException) { return true; }
    }

    public async Task UntagAssetsAsync(string tagId, IEnumerable<string> assetIds, CancellationToken ct = default)
    { using var _ = await Send(HttpMethod.Delete, $"tags/{tagId}/assets", new { ids = assetIds }, ct); }

    public async Task<IReadOnlyDictionary<string, bool>> CheckDuplicatesAsync(IEnumerable<DuplicateCheck> items, CancellationToken ct = default)
    {
        var body = new { assets = items.Select(i => new { id = i.ClientId, checksum = i.Sha1 }) };
        using var res = await Send(HttpMethod.Post, "assets/bulk-upload-check", body, ct);
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var d = new Dictionary<string, bool>();
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            d[r.GetProperty("id").GetString()!] = r.GetProperty("action").GetString() == "reject";
        return d;
    }

    public async Task<UploadResult> UploadAsync(UploadRequest r, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(r.FilePath);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(fs);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "assetData", Path.GetFileName(r.FilePath));
        form.Add(new StringContent(r.DeviceAssetId), "deviceAssetId");
        form.Add(new StringContent("ImmichWin"), "deviceId");
        form.Add(new StringContent(r.FileCreatedAt.ToString("o")), "fileCreatedAt");
        form.Add(new StringContent(r.FileModifiedAt.ToString("o")), "fileModifiedAt");
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "assets") { Content = form }, ct);
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return new UploadResult(doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.GetProperty("status").GetString() == "duplicate");
    }

    public async Task DeleteAssetsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    { using var _ = await Send(HttpMethod.Delete, "assets", new { ids, force = false }, ct); }
}
