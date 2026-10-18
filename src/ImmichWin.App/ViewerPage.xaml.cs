using ImmichWin.Api;
using ImmichWin.Cache;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace ImmichWin.App;

public sealed record ViewerArgs(List<AssetDto> Items, int Index);

public sealed partial class ViewerPage : Page
{
    List<AssetDto> _items = [];
    int _index;
    string? _pinned;
    int _gen;

    public ViewerPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var a = (ViewerArgs)e.Parameter;
        _items = a.Items; _index = a.Index;
        Root.Focus(FocusState.Programmatic);
        await ShowAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_pinned != null) AppServices.I.Cache.Unpin(_pinned);
    }

    async Task ShowAsync()
    {
        var gen = ++_gen;
        var asset = _items[_index];
        if (_pinned != null) AppServices.I.Cache.Unpin(_pinned);
        _pinned = asset.Id; AppServices.I.Cache.Pin(asset.Id);
        NameText.Text = $"{asset.OriginalFileName}   ({_index + 1}/{_items.Count})";
        Msg.Text = ""; Busy.IsActive = true;
        OrigBtn.IsEnabled = true;
        try
        {
            var s = AppServices.I;
            var path = await s.Cache.GetPreviewAsync(asset.Id, s.Api!);
            if (gen != _gen) return;
            Pic.Source = new BitmapImage(new Uri(path));
        }
        catch (CacheMissException)
        {
            // offline and no preview cached: fall back to whatever thumbnail we have
            if (AppServices.I.Cache.TryGetCached(asset.Id, "thumb", out var t) && gen == _gen) Pic.Source = new BitmapImage(new Uri(t));
            else if (gen == _gen) { Pic.Source = null; Msg.Text = "Not available offline"; }
        }
        catch (ImmichException ex) { if (gen == _gen) Msg.Text = ex.Message; }
        finally { if (gen == _gen) Busy.IsActive = false; }
    }

    async void OnOriginal(object s, RoutedEventArgs e)
    {
        var asset = _items[_index]; var gen = _gen;
        Busy.IsActive = true; OrigBtn.IsEnabled = false;
        try
        {
            var path = await AppServices.I.Cache.GetOriginalAsync(asset.Id, asset.OriginalFileName, AppServices.I.Api!);
            if (gen == _gen) Pic.Source = new BitmapImage(new Uri(path));
        }
        catch (Exception ex) { Msg.Text = "Could not load original: " + ex.Message; }
        finally { Busy.IsActive = false; }
    }

    async void OnPrev(object s, RoutedEventArgs e) { if (_index > 0) { _index--; await ShowAsync(); } }
    async void OnNext(object s, RoutedEventArgs e) { if (_index < _items.Count - 1) { _index++; await ShowAsync(); } }
    void OnBack(object s, RoutedEventArgs e) { if (Frame.CanGoBack) Frame.GoBack(); }

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left: OnPrev(this, null!); break;
            case Windows.System.VirtualKey.Right: OnNext(this, null!); break;
            case Windows.System.VirtualKey.Escape: OnBack(this, null!); break;
        }
    }
}
