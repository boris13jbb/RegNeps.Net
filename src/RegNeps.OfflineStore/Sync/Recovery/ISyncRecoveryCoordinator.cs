namespace RegNeps.OfflineStore.Sync.Recovery;

/// <summary>
/// Coalescing de triggers SignalR/conectividad → un solo Pull activo vía <see cref="ISyncEngine"/>.
/// SignalR notifica; este coordinador pide Pull; el cursor solo avanza en Pull.
/// </summary>
public interface ISyncRecoveryCoordinator
{
    SyncRecoveryPhase Phase { get; }

    /// <summary>True mientras hay un Pull de recuperación en ejecución.</summary>
    bool IsPullRunning { get; }

    /// <summary>True si hay un trigger pendiente de procesar tras el Pull actual.</summary>
    bool IsPullRequested { get; }

    /// <summary>
    /// Solicita recuperación. No modifica datos locales ni avanza el cursor.
    /// Varios triggers próximos se coalescen; no se pierde el último pedido.
    /// </summary>
    void RequestRecovery(string reason, string? correlationId = null);

    /// <summary>Marca fase informativa de conectividad SignalR (no dispara Pull).</summary>
    void SetConnectivityPhase(SyncRecoveryPhase phase);
}
