namespace RegNeps.OfflineStore;

public static class OfflineStoreConstants
{
    public const int ProtocolVersion = 1;

    public const string DatabaseFileName = "regneps_local.db";

    /// <summary>Duración por defecto del snapshot UX de sesión offline.</summary>
    public static readonly TimeSpan DefaultSessionTtl = TimeSpan.FromHours(72);

    public const string CaptureRecordsPermission = "CaptureRecords";

    /// <summary>Permiso UX para editar; el servidor revalida EditRecords.</summary>
    public const string EditRecordsPermission = "EditRecords";
}
