namespace RegNeps.OfflineStore.Bridge;

/// <summary>
/// Acciones explícitas del bridge WebView ↔ MAUI (Fase 2D.1).
/// No hay dispatch genérico ni ejecución arbitraria.
/// </summary>
public static class OfflineBridgeActions
{
    public const string SessionSave = "offline.session.save";
    public const string SessionClear = "offline.session.clear";
    public const string SessionGet = "offline.session.get";
    public const string CaptureOpen = "offline.capture.open";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        SessionSave,
        SessionClear,
        SessionGet,
        CaptureOpen
    };

    public static bool IsAllowed(string? action) =>
        !string.IsNullOrWhiteSpace(action) && Allowed.Contains(action.Trim());
}
