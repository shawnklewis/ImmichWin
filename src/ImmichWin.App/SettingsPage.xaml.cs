using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ImmichWin.App;

public sealed partial class SettingsPage : Page
{
    readonly List<string> _folders = [];

    public SettingsPage()
    {
        InitializeComponent();
        var s = AppServices.I.Settings;
        Url.Text = s.ServerUrl; Fallback.Text = s.FallbackUrl ?? "";
        CacheMb.Value = s.CacheLimitMb; Auto.IsOn = s.AutoClassify; Review.IsOn = s.ReviewBeforeApply;
        Poll.Value = s.PollMinutes; Cap.Value = s.DailyCap;
        ClaudeCmd.Text = s.ClaudeCommand; Prompt.Text = s.ClassifyPrompt; Develop.Text = s.DevelopCommandTemplate ?? "";
        _folders.AddRange(s.WatchFolders);
        Folders.ItemsSource = _folders;
    }

    async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var p = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(p, App.MainWin!.Hwnd);
        p.FileTypeFilter.Add("*");
        var f = await p.PickSingleFolderAsync();
        if (f == null || _folders.Contains(f.Path)) return;
        _folders.Add(f.Path);
        Folders.ItemsSource = null; Folders.ItemsSource = _folders;
    }

    void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (Folders.SelectedItem is not string f) return;
        _folders.Remove(f);
        Folders.ItemsSource = null; Folders.ItemsSource = _folders;
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        var s = AppServices.I.Settings;
        s.ServerUrl = Url.Text.Trim();
        s.FallbackUrl = string.IsNullOrWhiteSpace(Fallback.Text) ? null : Fallback.Text.Trim();
        s.CacheLimitMb = (int)CacheMb.Value; s.AutoClassify = Auto.IsOn; s.ReviewBeforeApply = Review.IsOn;
        s.PollMinutes = (int)Poll.Value; s.DailyCap = (int)Cap.Value;
        s.ClaudeCommand = ClaudeCmd.Text.Trim(); s.ClassifyPrompt = Prompt.Text;
        s.DevelopCommandTemplate = string.IsNullOrWhiteSpace(Develop.Text) ? null : Develop.Text.Trim();
        s.WatchFolders = [.._folders];
        AppServices.I.SaveSettings();
        Saved.Text = "Saved (server URL changes apply after sign-in)";
    }

    void OnSignOut(object sender, RoutedEventArgs e)
    {
        AppServices.I.SignOut();
        App.MainWin!.ShowSignIn();
    }
}
