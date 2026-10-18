using ImmichWin.Api;

namespace ImmichWin.Timeline;

/// <summary>Pages through assets (whole library or one album), newest first. UI-agnostic so it can be tested.</summary>
public sealed class TimelineSource(IImmichApi api, string? albumId = null, int pageSize = 200)
{
    int _page = 1;
    public bool HasMore { get; private set; } = true;
    public bool Offline { get; private set; }
    public string? Error { get; private set; }

    public async Task<IReadOnlyList<AssetDto>> LoadMoreAsync(CancellationToken ct = default)
    {
        if (!HasMore) return [];
        try
        {
            var q = new SearchQuery { Page = _page, Size = pageSize, AlbumIds = albumId == null ? null : [albumId] };
            var p = await api.SearchAsync(q, ct);
            Offline = false; Error = null;
            if (p.NextPage == null || p.Items.Count == 0) HasMore = false; else _page++;
            return p.Items.Where(a => !a.IsTrashed).ToList();
        }
        catch (ImmichException e)
        {
            Offline = e.Kind == ErrorKind.Unreachable;
            Error = e.Message;
            return [];
        }
    }
}
