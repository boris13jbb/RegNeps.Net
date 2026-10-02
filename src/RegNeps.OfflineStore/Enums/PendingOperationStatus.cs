namespace RegNeps.OfflineStore.Enums;

/// <summary>Estado de una operación en el Outbox local.</summary>
public enum PendingOperationStatus
{
    Pending = 0,
    Sending = 1,
    Succeeded = 2,
    Failed = 3,
    Conflict = 4,
    Cancelled = 5
}
