using CommunityToolkit.Mvvm.ComponentModel;
using ImmichWin.Api;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace ImmichWin.App;


public sealed partial class AlbumsPage : Page
{
    public AlbumsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            var albums = await AppServices.I.Api!.GetAlbumsAsync();
            var items = albums.OrderBy(a => a.AlbumName).Select(a => new AlbumItem(a)).ToList();
            List.ItemsSource = items;
            foreach (var i in items) _ = i.LoadAsync();
        }
        catch (ImmichException ex)
        {
            Bar.Title = ex.Kind == ErrorKind.Unreachable ? "Offline" : "Error";
            Bar.Message = ex.Kind == ErrorKind.Unreachable ? "Albums need the server. The Timeline still shows photos saved on this PC." : ex.Message;
            Bar.IsOpen = true;
        }
    }

    void OnClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumItem a) Frame.Navigate(typeof(TimelinePage), new AlbumNav(a.Album.Id, a.Album.AlbumName));
    }
}
