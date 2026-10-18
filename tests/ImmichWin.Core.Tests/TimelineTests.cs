using ImmichWin.Api;
using ImmichWin.Data;
using ImmichWin.Pipeline;
using ImmichWin.Timeline;

namespace ImmichWin.Core.Tests;

public class TimelineTests
{
    static AssetDto A(string id) => new() { Id = id, OriginalFileName = id + ".jpg" };

    [Fact]
    public async Task Pages_append_until_nextPage_null()
    {
        var api = new FakeApi();
        api.Pages.Enqueue(new SearchPage([A("1"), A("2")], "2"));
        api.Pages.Enqueue(new SearchPage([A("3")], null));
        var t = new TimelineSource(api);
        Assert.Equal(2, (await t.LoadMoreAsync()).Count); Assert.True(t.HasMore);
        Assert.Single(await t.LoadMoreAsync()); Assert.False(t.HasMore);
        Assert.Empty(await t.LoadMoreAsync());
    }

    [Fact]
    public async Task Unreachable_sets_offline_without_throwing()
    {
        var t = new TimelineSource(new FakeApi { Down = true });
        Assert.Empty(await t.LoadMoreAsync());
        Assert.True(t.Offline); Assert.True(t.HasMore);
    }


    [Fact]
    public async Task Approving_pending_applies_and_resolves_it()
    {
        var api = new FakeApi(); using var db = new StateDb(":memory:"); var ap = new Applier(api, db);
        var id = db.RecordChange("a1", "pending", System.Text.Json.JsonSerializer.Serialize(new ClassificationResult(["ai/x"], [], false, "")));
        await ap.ApprovePendingAsync(id);
        Assert.Contains(api.Tagged, t => t.asset == "a1");
        Assert.True(db.GetChange(id)!.Undone);
    }
}

