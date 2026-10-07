namespace RegNeps.OfflineStore.Sync.Recovery;

/// <summary>
/// Estados de conectividad/recuperación del cliente (FASE 2E).
/// Un fallo de SignalR no implica <c>SyncError</c> de Outbox.
/// </summary>
public enum SyncRecoveryPhase
{
    /// <summary>Sin recuperación en curso; último Pull OK o aún no se ha intentado.</summary>
    Idle = 0,

    /// <summary>Hub o red desconectados (informativo).</summary>
    Disconnected = 1,

    /// <summary>WithAutomaticReconnect en progreso.</summary>
    Reconnecting = 2,

    /// <summary>Reconectado o trigger recibido; Pull pendiente de ejecutarse.</summary>
    PendingPull = 3,

    /// <summary>Pull (recuperación) en ejecución.</summary>
    Synchronizing = 4,

    /// <summary>Última recuperación drenó páginas sin error.</summary>
    Synchronized = 5,

    /// <summary>Fallo temporal del Pull de recuperación (cursor/Outbox intactos).</summary>
    SyncError = 6
}
