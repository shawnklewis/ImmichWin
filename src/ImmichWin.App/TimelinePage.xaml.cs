using ImmichWin.Upload;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace ImmichWin.App;

public sealed record AlbumNav(string Id, string Name);

public sealed partial class TimelinePage : Page
{
    AssetCollection? _items;

    public TimelinePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var album = e.Parameter as AlbumNav;
        if (album != null) TitleText.Text = album.Name;
        _items = new AssetCollection(album?.Id);
        _items.OfflineChanged += off => DispatcherQueue.TryEnqueue(() => OfflineBar.IsOpen = off);
        Grid.ItemsSource = _items;
    }

    void OnContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ThumbItem t) return;
        args.RegisterUpdateCallback((s, a) => { _ = t.EnsureThumbAsync(); });
    }

    void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (_items == null || e.ClickedItem is not ThumbItem t) return;
        Frame.Navigate(typeof(ViewerPage), new ViewerArgs(_items.Select(i => i.Asset).ToList(), _items.IndexOf(t)));
    }

    async void OnUpload(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWin!.Hwnd);
        foreach (var x in new[] { ".jpg", ".jpeg", ".png", ".heic", ".webp", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".mp4", ".mov" }) picker.FileTypeFilter.Add(x);
        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0 || AppServices.I.Uploader is not { } up) return;
        UploadBtn.IsEnabled = false;
        int ok = 0, dup = 0, fail = 0;
        foreach (var f in files)
        {
            UploadStatus.Text = $"Uploading {ok + dup + fail + 1}/{files.Count}…";
            switch (await up.UploadFileAsync(f.Path))
            {
                case UploadOutcome.Uploaded: ok++; break;
                case UploadOutcome.Duplicate: dup++; break;
                default: fail++; break;
            }
        }
        UploadStatus.Text = $"Done: {ok} uploaded, {dup} already on server, {fail} failed";
        UploadBtn.IsEnabled = true;
    }
}
