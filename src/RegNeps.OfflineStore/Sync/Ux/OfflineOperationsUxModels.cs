using RegNeps.OfflineStore.Enums;

namespace RegNeps.OfflineStore.Sync.Ux;

/// <summary>Filtro de inventario Outbox (FASE 2D.4). No altera el enum de estado.</summary>
public enum OfflineOperationListFilter
{
    All = 0,
    Pending = 1,
    Synced = 2,
    SyncError = 3,
    Conflict = 4
}

/// <summary>Acción segura ofrecida al usuario para una operación.</summary>
public enum OfflineOperationUxAction
{
    None = 0,
    /// <summary>Incluida en el próximo SyncEngine (ya Pending).</summary>
    SyncNow = 1,
    /// <summary>SyncError recuperable: reabrir a Pending y SyncEngine, mismo ClientOperationId.</summary>
    RetrySync = 2,
    /// <summary>401 / cookie ausente.</summary>
    Relogin = 3,
    /// <summary>Conflict — solo revisión, sin merge/LWW.</summary>
    RequiresReview = 4,
    /// <summary>Forbidden / Invalid permanente — no reintentar inútilmente.</summary>
    NoRetryPermanent = 5
}

public sealed class OfflineOperationListItem
{
    public Guid OperationId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string OperationTypeLabel { get; init; } = string.Empty;
    public PendingOperationStatus Status { get; init; }
    public string StatusLabel { get; init; } = string.Empty;
    public string RecordSummary { get; init; } = string.Empty;
    public string SyncResultLabel { get; init; } = string.Empty;
    public string? ErrorSummary { get; init; }
    public int AttemptCount { get; init; }
    public bool RequiresReview { get; init; }
    public OfflineOperationUxAction PrimaryAction { get; init; }
}

public sealed class OfflineOperationDetail
{
    public Guid OperationId { get; init; }
    public string OperationTypeLabel { get; init; } = string.Empty;
    public PendingOperationStatus Status { get; init; }
    public string StatusLabel { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? LastAttemptAtUtc { get; init; }
    public int AttemptCount { get; init; }

    // Usuario
    public string UserFacingSummary { get; init; } = string.Empty;
    public string? ErrorFriendly { get; init; }
    public string LocalRecordSummary { get; init; } = string.Empty;
    public string ServerRecordSummary { get; init; } = string.Empty;
    public string ServerIdDisplay { get; init; } = string.Empty;
    public string LocalIdDisplay { get; init; } = string.Empty;
    public string? ConflictStampDisplay { get; init; }
    public bool RequiresReview { get; init; }

    // Técnico (secundario, sin secretos)
    public string ClientOperationId { get; init; } = string.Empty;
    public string? LastServerErrorCode { get; init; }
    public string? CaptureSessionId { get; init; }
    public Guid? LocalNepRecordId { get; init; }
    public Guid? ServerRecordId { get; init; }
    public Guid? TargetServerRecordId { get; init; }

    public OfflineOperationUxAction PrimaryAction { get; init; }
    public string ActionHint { get; init; } = string.Empty;
}

public static class OfflineOperationStatusLabels
{
    public const int DefaultListLimit = 100;

    public static string StatusLabel(PendingOperationStatus status) => status switch
    {
        PendingOperationStatus.Pending => "Pendiente de sincronización",
        PendingOperationStatus.Sending => "Pendiente de sincronización",
        PendingOperationStatus.Synced => "Sincronizado",
        PendingOperationStatus.SyncError => "No se pudo sincronizar",
        PendingOperationStatus.Conflict => "Requiere revisión",
        PendingOperationStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    public static string OperationTypeLabel(OfflineOperationType type) => type switch
    {
        OfflineOperationType.CreateRecord => "Crear registro",
        OfflineOperationType.UpdateRecord => "Actualizar registro",
        OfflineOperationType.DeleteRecord => "Eliminar registro",
        OfflineOperationType.ApplyCorrective => "Acción correctiva",
        _ => type.ToString()
    };
}
