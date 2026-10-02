using RegNeps.OfflineStore.Sync;
#if ANDROID
using Android.Webkit;
#endif

namespace RegNeps.Mobile.Local;

/// <summary>
/// Lee la cookie de autenticación del WebView Android (no se persiste en SQLite).
/// </summary>
public sealed class MauiWebViewCookieProvider : ISyncAuthCookieProvider
{
    public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default)
    {
#if ANDROID
        try
        {
            var cookie = CookieManager.Instance?.GetCookie(serverBaseUrl);
            return Task.FromResult(string.IsNullOrWhiteSpace(cookie) ? null : cookie);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
#else
        return Task.FromResult<string?>(null);
#endif
    }
}
