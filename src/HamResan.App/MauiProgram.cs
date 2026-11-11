using Shiny;
namespace HamResan.App;
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddMdns();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
public sealed class App(MainPage page) : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(page);
        window.Resumed += (_, _) => page.StartDiscovery();
        window.Stopped += (_, _) => page.StopForegroundWork();
        return window;
    }
}
