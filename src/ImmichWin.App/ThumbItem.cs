using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using ImmichWin.Api;
using ImmichWin.Cache;
using ImmichWin.Timeline;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace ImmichWin.App;

public sealed partial class ThumbItem(AssetDto asset) : ObservableObject
{
    public AssetDto Asset { get; } = asset;
    [ObservableProperty] ImageSource? _thumb;
    bool _loading;

    public async Task EnsureThumbAsync()
    {
        if (Thumb != null || _loading) return;
        _loading = true;
        try
        {
            var s = AppServices.I;
            var path = await s.Cache.GetThumbAsync(Asset.Id, s.Api!);
            Thumb = new BitmapImage(new Uri(path)) { DecodePixelWidth = 256 };
        }
        catch { _loading = false; }
    }
}

/// <summary>Incrementally-loaded asset list. When the server is unreachable it falls back to the locally cached index.</summary>
public sealed class AssetCollection : ObservableCollection<ThumbItem>, ISupportIncrementalLoading
{
    const int IndexCap = 3000;
    readonly TimelineSource _src;
    readonly bool _persistIndex;
    readonly List<AssetDto> _seen = [];
    readonly string _indexPath = Path.Combine(AppServices.I.DataDir, "timeline-index.json");
    bool _offlineFilled;

    public event Action<bool>? OfflineChanged;

    public AssetCollection(string? albumId)
    {
        _src = new TimelineSource(AppServices.I.Api!, albumId);
        _persistIndex = albumId == null;
    }

    public bool HasMoreItems => _src.HasMore && !_offlineFilled;

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) =>
        System.Runtime.InteropServices.WindowsRuntime.AsyncInfo.Run(async ct =>
        {
            var items = await _src.LoadMoreAsync(ct);
            OfflineChanged?.Invoke(_src.Offline);
            if (_src.Offline && _persistIndex && !_offlineFilled)
            {
                _offlineFilled = true;
                items = LoadCachedIndex();
            }
            else if (_persistIndex && items.Count > 0 && _seen.Count < IndexCap)
            {
                _seen.AddRange(items);
                try { File.WriteAllText(_indexPath, JsonSerializer.Serialize(_seen)); } catch { }
            }
            foreach (var a in items) Add(new ThumbItem(a));
            return new LoadMoreItemsResult { Count = (uint)items.Count };
        });

    IReadOnlyList<AssetDto> LoadCachedIndex()
    {
        try
        {
            var all = JsonSerializer.Deserialize<List<AssetDto>>(File.ReadAllText(_indexPath)) ?? [];
            return all.Where(a => AppServices.I.Cache.TryGetCached(a.Id, "thumb", out _)).ToList();
        }
        catch { return []; }
    }
}
