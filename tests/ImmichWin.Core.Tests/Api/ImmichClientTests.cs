using System.Net;
using System.Text;
using ImmichWin.Api;

namespace ImmichWin.Core.Tests.Api;

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
{
    public List<HttpRequestMessage> Seen { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    { Seen.Add(r); return Task.FromResult(f(r)); }
}

public class ImmichClientTests
{
    static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    static ImmichClient Make(FakeHandler h) => new(new HttpClient(h), "http://x:1", "FAKEKEY");

    [Fact]
    public async Task Validate_sends_api_key_header()
    {
        var h = new FakeHandler(_ => Ok("""{"id":"u","email":"a@b","name":"A"}"""));
        var u = await Make(h).ValidateAsync();
        Assert.Equal("a@b", u.Email);
        Assert.Equal("FAKEKEY", h.Seen[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal("http://x:1/api/users/me", h.Seen[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Validate_401_is_Unauthorized()
    {
        var c = Make(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var e = await Assert.ThrowsAsync<ImmichException>(() => c.ValidateAsync());
        Assert.Equal(ErrorKind.Unauthorized, e.Kind);
    }

    [Fact]
    public async Task Network_failure_is_Unreachable()
    {
        var c = Make(new FakeHandler(_ => throw new HttpRequestException("down")));
        var e = await Assert.ThrowsAsync<ImmichException>(() => c.ValidateAsync());
        Assert.Equal(ErrorKind.Unreachable, e.Kind);
    }

    [Fact]
    public async Task Search_parses_asset_with_null_dates_and_nextPage()
    {
        var json = """{"albums":{},"assets":{"items":[{"id":"a1","originalFileName":"x.jpg","type":"IMAGE","localDateTime":null,"fileCreatedAt":null}],"nextPage":"2"}}""";
        var page = await Make(new FakeHandler(_ => Ok(json))).SearchAsync(new SearchQuery());
        Assert.Single(page.Items);
        Assert.Equal("a1", page.Items[0].Id);
        Assert.Equal(DateTimeOffset.MinValue, page.Items[0].SortDate);
        Assert.Equal("2", page.NextPage);
    }

    [Fact]
    public async Task Search_nextPage_null_ends()
    {
        var json = """{"assets":{"items":[],"nextPage":null}}""";
        var page = await Make(new FakeHandler(_ => Ok(json))).SearchAsync(new SearchQuery());
        Assert.Null(page.NextPage);
    }

    [Fact]
    public async Task CheckDuplicates_maps_reject_to_true()
    {
        var json = """{"results":[{"id":"1","action":"reject","assetId":"z"},{"id":"2","action":"accept"}]}""";
        var d = await Make(new FakeHandler(_ => Ok(json))).CheckDuplicatesAsync([new("1", "aa"), new("2", "bb")]);
        Assert.True(d["1"]);
        Assert.False(d["2"]);
    }

    [Fact]
    public async Task Upload_sends_multipart_fields()
    {
        var tmp = Path.GetTempFileName(); File.WriteAllText(tmp, "hello");
        string? body = null;
        var h = new FakeHandler(r => { body = r.Content!.ReadAsStringAsync().Result; return Ok("""{"id":"new1","status":"created"}"""); });
        var res = await Make(h).UploadAsync(new UploadRequest(tmp, "dev-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Assert.Equal("new1", res.AssetId); Assert.False(res.Duplicate);
        foreach (var f in new[] { "assetData", "deviceAssetId", "deviceId", "fileCreatedAt", "fileModifiedAt" })
            Assert.Contains($"name={f}", body);
    }
}
