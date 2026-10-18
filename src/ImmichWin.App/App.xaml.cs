using Microsoft.UI.Xaml;

namespace ImmichWin.App;

public partial class App : Application
{
    public static MainWindow? MainWin { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWin = new MainWindow();
        MainWin.Activate();
    }
}
