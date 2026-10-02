namespace RegNeps.OfflineStore.Models;

/// <summary>
/// Solicitud de Update offline (FASE 2D.5). Campos de negocio alineados con SyncUpdateRecordPayload.
/// </summary>
public sealed class OfflineUpdateRecordRequest
{
    /// <summary>Id local del LocalNepRecord (no el ServerRecordId).</summary>
    public Guid LocalRecordId { get; set; }

    public string Telar { get; set; } = string.Empty;
    public double Neps { get; set; }
    public string Tela { get; set; } = string.Empty;
    public string LoteTrama { get; set; } = string.Empty;
    public string Turno { get; set; } = string.Empty;
    public string Operario { get; set; } = string.Empty;
    public string LineaProduccion { get; set; } = string.Empty;
    public string Observacion { get; set; } = string.Empty;
}

public enum OfflineEditBlockReason
{
    None = 0,
    NoSession,
    NoEditPermission,
    NotFound,
    Deleted,
    NotOwner,
    CreateStillPending,
    MissingServerId,
    MissingConcurrencyStamp,
    UpdateAlreadyPending
}

public sealed class OfflineEditEligibility
{
    public bool CanEdit { get; init; }
    public OfflineEditBlockReason Reason { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid? LocalRecordId { get; init; }
    public Guid? ServerRecordId { get; init; }
}
