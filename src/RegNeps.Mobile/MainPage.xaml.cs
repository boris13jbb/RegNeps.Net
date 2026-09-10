using Microsoft.Maui.Storage;

namespace RegNeps.Mobile;

public partial class MainPage : ContentPage
{
    private const string ServerUrlPreferenceKey = "regneps_server_url";
    private const string DefaultServerUrl = "http://192.168.100.140:5080";

    public MainPage()
    {
        InitializeComponent();

        var savedUrl = Preferences.Default.Get(ServerUrlPreferenceKey, DefaultServerUrl);
        ServerEntry.Text = savedUrl;
        NavigateTo(savedUrl);
    }

    private void OnConnectClicked(object? sender, EventArgs e)
    {
        NavigateTo(ServerEntry.Text ?? string.Empty, persist: true);
    }

    private void OnReloadClicked(object? sender, EventArgs e)
    {
        var currentUrl = ServerEntry.Text ?? Preferences.Default.Get(ServerUrlPreferenceKey, DefaultServerUrl);
        NavigateTo(currentUrl);
    }

    private void NavigateTo(string rawUrl, bool persist = false)
    {
        var normalized = NormalizeServerUrl(rawUrl);
        if (normalized is null)
        {
            ShowError("La URL no es válida. Usa http:// o https://, por ejemplo http://192.168.100.140:5080");
            return;
        }

        ServerEntry.Text = normalized;
        if (persist)
        {
            Preferences.Default.Set(ServerUrlPreferenceKey, normalized);
        }

        ShowLoading("Conectando con RegNeps...");
        Browser.Source = new UrlWebViewSource { Url = normalized };
    }

    private static string? NormalizeServerUrl(string rawUrl)
    {
        var value = rawUrl.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = $"http://{value}";
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString().TrimEnd('/')
            : null;
    }

    private void OnBrowserNavigating(object? sender, WebNavigatingEventArgs e)
    {
        ShowLoading("Cargando...");
    }

    private void OnBrowserNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result == WebNavigationResult.Success)
        {
            StatusOverlay.IsVisible = false;
            LoadingIndicator.IsRunning = false;
            RetryButton.IsVisible = false;
            return;
        }

        ShowError("No se pudo conectar con RegNeps. Comprueba que el servidor esté encendido, que el teléfono esté en la misma red y que el puerto 5080 sea accesible.");
    }

    private void ShowLoading(string message)
    {
        StatusLabel.Text = message;
        StatusOverlay.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        RetryButton.IsVisible = false;
    }

    private void ShowError(string message)
    {
        StatusLabel.Text = message;
        StatusOverlay.IsVisible = true;
        LoadingIndicator.IsRunning = false;
        RetryButton.IsVisible = true;
    }

    protected override bool OnBackButtonPressed()
    {
        if (Browser.CanGoBack)
        {
            Browser.GoBack();
            return true;
        }

        return base.OnBackButtonPressed();
    }
}
