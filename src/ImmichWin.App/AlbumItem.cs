using CommunityToolkit.Mvvm.ComponentModel;
using ImmichWin.Api;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ImmichWin.App;

public sealed partial class AlbumItem(AlbumDto album) : ObservableObject
{
    public AlbumDto Album { get; } = album;
    public string CountText => $"{Album.AssetCount} items";
    [ObservableProperty] ImageSource? _thumb;

    public async Task LoadAsync()
    {
        if (Album.AlbumThumbnailAssetId is not { Length: > 0 } id) return;
        try
        {
            var s = AppServices.I;
            Thumb = new BitmapImage(new Uri(await s.Cache.GetThumbAsync(id, s.Api!))) { DecodePixelWidth = 360 };
        }
        catch { }
    }
}
