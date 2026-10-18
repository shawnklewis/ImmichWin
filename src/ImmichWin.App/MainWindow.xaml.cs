using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ImmichWin.App;

public sealed partial class MainWindow : Window
{
    bool _quitting;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = false;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        AppWindow.Closing += (_, e) =>
        {
            if (_quitting) return;
            e.Cancel = true;
            AppWindow.Hide(); // keep running in the tray so the pipeline keeps working
        };
        Tray.ForceCreate();
        Tray.LeftClickCommand = new ShowCommand(this);

        if (AppServices.I.TryRestoreSession()) ShowShell();
        else ShowSignIn();
    }

    sealed class ShowCommand(MainWindow w) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => w.ShowFromTray();
    }

    public void ShowFromTray() { AppWindow.Show(); Activate(); }
    void OnTrayShow(object s, RoutedEventArgs e) => ShowFromTray();
    void OnTrayQuit(object s, RoutedEventArgs e)
    {
        _quitting = true;
        Tray.Dispose();
        _ = AppServices.I.Worker?.StopAsync();
        Application.Current.Exit();
    }

    public void ShowSignIn()
    {
        Nav.IsPaneVisible = false;
        ContentFrame.Navigate(typeof(SignInPage));
    }

    public void ShowShell()
    {
        Nav.IsPaneVisible = true;
        Nav.SelectedItem = Nav.MenuItems[0];
        ContentFrame.Navigate(typeof(TimelinePage));
    }

    void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;
        ContentFrame.Navigate(tag switch
        {
            "albums" => typeof(AlbumsPage),
            "activity" => typeof(ActivityPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(TimelinePage),
        });
    }

    public nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);
}
