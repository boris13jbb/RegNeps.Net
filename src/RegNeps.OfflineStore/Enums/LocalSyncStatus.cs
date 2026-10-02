namespace RegNeps.OfflineStore.Enums;

/// <summary>Estado de sincronización de un <see cref="Entities.LocalNepRecord"/>.</summary>
public enum LocalSyncStatus
{
    PendingSync = 0,
    Synced = 1,
    SyncError = 2,
    Conflict = 3
}
