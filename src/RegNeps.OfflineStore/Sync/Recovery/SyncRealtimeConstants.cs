namespace RegNeps.OfflineStore.Sync.Recovery;

/// <summary>Nombres de métodos SignalR compartidos (cliente MAUI / docs). El hub Web debe coincidir.</summary>
public static class SyncRealtimeConstants
{
    public const string CriticalAlertMethod = "CriticalAlertReceived";
    public const string SyncRecoveryMethod = "SyncRecoverySuggested";
}
