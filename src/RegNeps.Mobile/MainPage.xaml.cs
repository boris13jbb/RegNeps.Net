using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace RegNeps.Mobile;

public partial class MainPage : ContentPage
{
    private const string ServerUrlPreferenceKey = "regneps_server_url";
    private const string DefaultServerUrl = "http://192.168.100.140:5080";

    /// <summary>
    /// Límite del deep-link regneps-share:// (URL completa). Por encima se rechaza
    /// sin crashear; el JS hace fallback a clipboard.
    /// </summary>
    private const int NativeShareUrlMaxLength = 3500;

    private const string NativeShareScheme = "regneps-share";
    private const string NativeShareHost = "share";

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
        // Interceptar el puente nativo ANTES del overlay de carga.
        if (TryBeginNativeShare(e.Url))
        {
            e.Cancel = true;
            return;
        }

        ShowLoading("Cargando...");
    }

    private async void OnBrowserNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result == WebNavigationResult.Success)
        {
            StatusOverlay.IsVisible = false;
            LoadingIndicator.IsRunning = false;
            RetryButton.IsVisible = false;

            // Solo el WebView de la APK debe usar el esquema regneps-share://.
            try
            {
                await Browser.EvaluateJavaScriptAsync("window.regnepsNativeShareAvailable = true;");
            }
            catch
            {
                /* WebView aún no listo; el siguiente Navigated lo reintentará */
            }

            return;
        }

        ShowError("No se pudo conectar con RegNeps. Comprueba que el servidor esté encendido, que el teléfono esté en la misma red y que el puerto 5080 sea accesible.");
    }

    /// <summary>
    /// Acepta únicamente regneps-share://share?title=&amp;text=.
    /// No abre URLs externas ni ejecuta scripts.
    /// </summary>
    private bool TryBeginNativeShare(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        if (rawUrl.Length > NativeShareUrlMaxLength)
        {
            return false;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, NativeShareScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Host o path: regneps-share://share?...
        var action = string.IsNullOrEmpty(uri.Host) ? uri.AbsolutePath.Trim('/') : uri.Host;
        if (!string.Equals(action, NativeShareHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var title = Uri.UnescapeDataString(GetQueryValue(uri, "title") ?? "RegNeps");
        var text = Uri.UnescapeDataString(GetQueryValue(uri, "text") ?? string.Empty);

        _ = RequestNativeShareAsync(title, text);
        return true;
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query.Length < 2)
        {
            return null;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return eq < 0 ? string.Empty : part[(eq + 1)..];
        }

        return null;
    }

    private static async Task RequestNativeShareAsync(string title, string text)
    {
        try
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = title,
                Text = text,
                Subject = title
            });
        }
        catch
        {
            // No tumbar la app si el sheet nativo falla; el usuario puede reintentar.
        }
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
