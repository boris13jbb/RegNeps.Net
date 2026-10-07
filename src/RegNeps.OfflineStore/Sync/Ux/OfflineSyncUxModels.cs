namespace RegNeps.OfflineStore.Sync.Ux;

/// <summary>
/// Estado de conectividad / sesión para la UX offline (FASE 2D.3).
/// No equivale a autenticación de servidor: la cookie sigue siendo la autoridad.
/// </summary>
public enum SyncConnectivityUxKind
{
    /// <summary>Sin sesión local.</summary>
    NoLocalSession,

    /// <summary>Sesión local expirada (TTL).</summary>
    LocalSessionExpired,

    /// <summary>Sesión local válida; cookie online ausente — captura temporal OK, sync no.</summary>
    LocalSessionWithoutOnlineAuth,

    /// <summary>Sin red en el dispositivo.</summary>
    OfflineNoNetwork,

    /// <summary>Hay red pero el servidor no responde.</summary>
    ServerUnreachable,

    /// <summary>Servidor alcanzable y cookie presente — sync posible.</summary>
    OnlineReady,

    /// <summary>Servidor alcanzable pero requiere re-login (401 / cookie inválida).</summary>
    RequiresLogin
}

public sealed class OfflineOutboxCounters
{
    public int Pending { get; init; }
    public int Synced { get; init; }
    public int SyncError { get; init; }
    public int Conflict { get; init; }

    public int Total => Pending + Synced + SyncError + Conflict;
}

public sealed class OfflineSyncSummary
{
    public OfflineOutboxCounters Counters { get; init; } = new();
    public DateTime? LastSyncAtUtc { get; init; }
    public string? LastConnectivityStatus { get; init; }
    public string? LastSyncErrorSummary { get; init; }
    public long LastPulledSequence { get; init; }
}

/// <summary>Vista amigable de un conflicto (sin PayloadJson crudo).</summary>
public sealed class ConflictReviewItem
{
    public Guid OperationId { get; init; }
    public Guid EntityId { get; init; }
    public string OperationTypeLabel { get; init; } = string.Empty;
    public string EntityLabel { get; init; } = string.Empty;
    public string LocalSummary { get; init; } = string.Empty;
    public string ServerSummary { get; init; } = string.Empty;
    public DateTime? RelevantAtUtc { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string ActionHint { get; init; } = "Requiere revisión";
}

public sealed class SyncRunUxFeedback
{
    public bool Success { get; init; }
    public bool Partial { get; init; }
    public bool RequiresLogin { get; init; }
    public bool DidNotStart { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int AcceptedOrDuplicate { get; init; }
    public int ConflictCount { get; init; }
    public int ErrorCount { get; init; }
    public int StillPending { get; init; }
}

public static class SyncResultUserMessages
{
    public const string Transient =
        "No se pudo sincronizar todavía. La operación permanece pendiente y podrás intentarlo nuevamente.";

    public const string Forbidden =
        "No tienes permisos para realizar esta operación en el servidor.";

    public const string Invalid =
        "El servidor no pudo aceptar esta operación. Revisa los datos o vuelve a capturar.";

    public const string Conflict =
        "Esta información cambió en el servidor y requiere revisión.";

    public const string SyncedOk = "Sincronizado correctamente.";

    public const string RequiresLogin =
        "Tu sesión online expiró o no está disponible. Vuelve a iniciar sesión para sincronizar.";

    public const string LocalOnlyCapture =
        "Tu sesión local permite capturar temporalmente, pero necesitas volver a conectarte e iniciar sesión para sincronizar.";

    public const string LogoutKeepsWork =
        "Se cerró la sesión local. El trabajo offline pendiente se conserva y se sincronizará cuando vuelvas a iniciar sesión.";
}
