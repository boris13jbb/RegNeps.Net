using Microsoft.Extensions.DependencyInjection;

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
        return new Window(new NavigationPage(main));
    }
}
