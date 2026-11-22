using Microsoft.Maui.Controls.Hosting;

namespace HomeSafe.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp.CreateBuilder().UseMauiApp<App>().Build();
}

public sealed class App : Application
{
    public App() => UserAppTheme = AppTheme.Light;

    protected override Window CreateWindow(IActivationState? activationState)
        => new(new NavigationPage(new HomePage())
        {
            BarBackgroundColor = Design.Paper,
            BarTextColor = Design.Ink
        });
}
