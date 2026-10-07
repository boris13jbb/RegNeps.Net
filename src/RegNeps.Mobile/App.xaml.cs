using Microsoft.Extensions.DependencyInjection;
using RegNeps.Mobile.Local;

namespace RegNeps.Mobile;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var main = _services.GetRequiredService<MainPage>();
        var window = new Window(new NavigationPage(main));

        // FASE 2E: al volver a primer plano, reasegurar hub y pedir Pull (cursor local).
        window.Resumed += (_, _) =>
        {
            var hub = _services.GetService<MauiSyncHubRecoveryService>();
            if (hub is not null)
            {
                _ = hub.OnAppResumedAsync();
            }
        };

        // Primer arranque: intentar hub si ya hay sesión/cookie.
        var recoveryHub = _services.GetService<MauiSyncHubRecoveryService>();
        if (recoveryHub is not null)
        {
            _ = recoveryHub.EnsureConnectedAsync();
        }

        return window;
    }
}
