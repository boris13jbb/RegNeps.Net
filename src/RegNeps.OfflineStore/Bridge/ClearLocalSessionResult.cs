namespace RegNeps.OfflineStore.Bridge;

/// <summary>
/// Resultado de limpiar LocalSession en logout.
/// Las PendingOperation / LocalNepRecord nunca se borran aquí (evita pérdida de Outbox).
/// </summary>
public sealed class ClearLocalSessionResult
{
    public bool Cleared { get; init; }

    public string? PreviousUserId { get; init; }

    /// <summary>Operaciones Outbox conservadas a propósito tras el logout.</summary>
    public int PendingOperationsRetained { get; init; }
}
