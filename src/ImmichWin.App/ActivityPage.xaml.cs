using ImmichWin.Api;
using ImmichWin.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ImmichWin.App;


public sealed partial class ActivityPage : Page
{
    public ActivityPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        AppServices.I.Log += OnLog;
        Reload();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => AppServices.I.Log -= OnLog;

    void OnLog(string m) => DispatcherQueue.TryEnqueue(() => { LogBox.Text += m + "\n"; Reload(); });

    void Reload() => List.ItemsSource = AppServices.I.Db.ListChanges().Select(c => new ChangeRow(c)).ToList();

    void OnRefresh(object s, RoutedEventArgs e) => Reload();

    async void OnAct(object sender, RoutedEventArgs e)
    {
        var id = (long)((Button)sender).Tag;
        var ap = AppServices.I.Applier!;
        try
        {
            var c = AppServices.I.Db.GetChange(id);
            if (c?.Type == "pending") await ap.ApprovePendingAsync(id); else await ap.UndoAsync(id);
        }
        catch (ImmichException ex) { Status.Text = ex.Message; }
        Reload();
    }

    void OnReject(object sender, RoutedEventArgs e)
    {
        AppServices.I.Applier!.RejectPending((long)((Button)sender).Tag);
        Reload();
    }

    async void OnBackfill(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Classify existing photos?",
            Content = "This sends the newest 200 unprocessed photos in your library to Claude (it uses your Claude usage). Continue?",
            PrimaryButtonText = "Classify", CloseButtonText = "Cancel",
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        Status.Text = "Working…";
        try
        {
            var s = AppServices.I;
            var page = await s.Api!.SearchAsync(new SearchQuery { Size = 200, WithExif = false });
            var n = await s.Worker!.ProcessAssetsAsync(page.Items);
            Status.Text = $"Classified {n} photos";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        Reload();
    }
}
