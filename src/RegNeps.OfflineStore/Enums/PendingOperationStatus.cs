namespace RegNeps.OfflineStore.Enums;

/// <summary>
/// Estado de una operación en el Outbox local.
/// Valores enteros estables (2=Synced, 3=SyncError) — compatibles con filas 2A (Succeeded/Failed).
/// </summary>
public enum PendingOperationStatus
{
    Pending = 0,
    Sending = 1,
    /// <summary>Accepted o Duplicate en servidor. Antes: Succeeded.</summary>
    Synced = 2,
    /// <summary>Error permanente (Invalid/Forbidden/reuso). Antes: Failed.</summary>
    SyncError = 3,
    Conflict = 4,
    Cancelled = 5
}
