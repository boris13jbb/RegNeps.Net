using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
#if ANDROID
using Android.Webkit;
#endif

namespace RegNeps.Mobile;

public partial class MainPage : ContentPage
{
    private const string ServerUrlPreferenceKey = "regneps_server_url";
    private const string DefaultServerUrl = "http://192.168.100.140:5080";

    /// <summary>
    /// Límite del deep-link regneps-share:// (URL completa). Solo metadatos (id/nombre), no bytes.
    /// </summary>
    private const int NativeShareUrlMaxLength = 3500;

    private const string NativeShareScheme = "regneps-share";
    private const string NativeShareHostText = "share";
    private const string NativeShareHostFile = "file";

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

            try
            {
                await Browser.EvaluateJavaScriptAsync("window.regnepsNativeShareAvailable = true;");
            }
            catch
            {
                /* WebView aún no listo */
            }

            return;
        }

        ShowError("No se pudo conectar con RegNeps. Comprueba que el servidor esté encendido, que el teléfono esté en la misma red y que el puerto 5080 sea accesible.");
    }

    /// <summary>
    /// Acepta únicamente:
    /// - regneps-share://share?title=&amp;text= (legado texto)
    /// - regneps-share://file?id=&amp;name= (archivo vía TempExport autenticado)
    /// </summary>
    private bool TryBeginNativeShare(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl.Length > NativeShareUrlMaxLength)
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

        var action = string.IsNullOrEmpty(uri.Host) ? uri.AbsolutePath.Trim('/') : uri.Host;

        if (string.Equals(action, NativeShareHostFile, StringComparison.OrdinalIgnoreCase))
        {
            var idRaw = Uri.UnescapeDataString(GetQueryValue(uri, "id") ?? string.Empty);
            var fileName = Uri.UnescapeDataString(GetQueryValue(uri, "name") ?? "regneps-export");
            if (!Guid.TryParse(idRaw, out var exportId))
            {
                return true; // esquema reconocido pero inválido: cancelar navegación sin crashear
            }

            _ = RequestNativeFileShareAsync(exportId, SanitizeFileName(fileName));
            return true;
        }

        if (string.Equals(action, NativeShareHostText, StringComparison.OrdinalIgnoreCase))
        {
            var title = Uri.UnescapeDataString(GetQueryValue(uri, "title") ?? "RegNeps");
            var text = Uri.UnescapeDataString(GetQueryValue(uri, "text") ?? string.Empty);
            _ = RequestNativeTextShareAsync(title, text);
            return true;
        }

        return false;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "regneps-export" : cleaned;
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

    private static async Task RequestNativeTextShareAsync(string title, string text)
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
            /* no tumbar la app */
        }
    }

    private async Task RequestNativeFileShareAsync(Guid exportId, string fileName)
    {
        try
        {
            var baseUrl = NormalizeServerUrl(ServerEntry.Text ?? string.Empty);
            if (baseUrl is null)
            {
                return;
            }

            var downloadUrl = $"{baseUrl}/api/export/temp/{exportId:D}";
            using var client = new HttpClient();
            AttachWebViewCookies(client, baseUrl);

            using var response = await client.GetAsync(downloadUrl);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                return;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType
                              ?? GuessContentType(fileName);

            var localPath = Path.Combine(FileSystem.CacheDirectory, fileName);
            await File.WriteAllBytesAsync(localPath, bytes);

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = fileName,
                File = new ShareFile(localPath, contentType)
            });
        }
        catch
        {
            /* no tumbar la app */
        }
    }

    private static string GuessContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".csv" => "text/csv",
            _ => "application/octet-stream"
        };
    }

    private static void AttachWebViewCookies(HttpClient client, string baseUrl)
    {
#if ANDROID
        try
        {
            var cookie = CookieManager.Instance?.GetCookie(baseUrl);
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
            }
        }
        catch
        {
            /* sin cookies: el endpoint devolverá 401/404 */
        }
#endif
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
