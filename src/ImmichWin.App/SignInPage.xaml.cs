using ImmichWin.Api;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ImmichWin.App;

public sealed partial class SignInPage : Page
{
    public SignInPage()
    {
        InitializeComponent();
        UrlBox.Text = AppServices.I.Settings.ServerUrl;
    }

    async void OnSignIn(object sender, RoutedEventArgs e)
    {
        Err.IsOpen = false; Busy.IsActive = true; Go.IsEnabled = false;
        try
        {
            await AppServices.I.SignInAsync(UrlBox.Text.Trim(), KeyBox.Password.Trim());
            KeyBox.Password = "";
            App.MainWin!.ShowShell();
        }
        catch (ImmichException ex)
        {
            Err.Message = ex.Kind switch
            {
                ErrorKind.Unauthorized => "The server rejected that API key.",
                ErrorKind.Unreachable => "Could not reach the server: " + ex.Message,
                _ => ex.Message,
            };
            Err.IsOpen = true;
        }
        catch (Exception ex) { Err.Message = ex.Message; Err.IsOpen = true; }
        finally { Busy.IsActive = false; Go.IsEnabled = true; }
    }
}
