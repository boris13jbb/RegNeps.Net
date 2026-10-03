namespace RegNeps.OfflineStore.Models;

/// <summary>
/// Payload Outbox ApplyCorrective (wire = SyncApplyCorrectivePayload).
/// Solo parámetros de corrección; no incluye Telar/Neps ni stamp (stamp en PendingOperation).
/// </summary>
public sealed class ApplyCorrectivePayload
{
    public Guid EntityId { get; set; }
    public string Accion { get; set; } = string.Empty;
    public string Responsable { get; set; } = string.Empty;
    public bool MarcarRevisado { get; set; } = true;
}

/// <summary>Solicitud ApplyCorrective offline (FASE 2D.10).</summary>
public sealed class OfflineApplyCorrectiveRequest
{
    public Guid LocalRecordId { get; set; }
    public string Accion { get; set; } = string.Empty;
    public string Responsable { get; set; } = string.Empty;
    public bool MarcarRevisado { get; set; } = true;
}

public enum OfflineCorrectiveBlockReason
{
    None = 0,
    NoSession,
    NoCorrectivePermission,
    NotFound,
    Deleted,
    NotOwner,
    CreateStillPending,
    MissingServerId,
    MissingConcurrencyStamp,
    MutationAlreadyPending,
    ConflictRequiresReview,
    AccionRequired
}

public sealed class OfflineCorrectiveEligibility
{
    public bool CanApply { get; init; }
    public OfflineCorrectiveBlockReason Reason { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid? LocalRecordId { get; init; }
    public Guid? ServerRecordId { get; init; }
}
