namespace RegNeps.OfflineStore.Sync;

/// <summary>
/// Obtiene la cookie de sesión del WebView para llamadas autenticadas.
/// No almacena ni persiste la cookie en SQLite.
/// </summary>
public interface ISyncAuthCookieProvider
{
    /// <returns>Header Cookie completo, o null si no hay sesión de servidor.</returns>
    Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default);
}
