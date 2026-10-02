namespace RegNeps.OfflineStore.Models;

/// <summary>Payload Outbox DeleteRecord (wire = SyncDeleteRecordPayload).</summary>
public sealed class DeleteRecordPayload
{
    public Guid EntityId { get; set; }
}

/// <summary>Solicitud de Delete offline (FASE 2D.6).</summary>
public sealed class OfflineDeleteRecordRequest
{
    public Guid LocalRecordId { get; set; }
}

public enum OfflineDeleteBlockReason
{
    None = 0,
    NoSession,
    NoDeletePermission,
    NotFound,
    Deleted,
    NotOwner,
    CreateStillPending,
    MissingServerId,
    MissingConcurrencyStamp,
    MutationAlreadyPending,
    ConflictRequiresReview
}

public sealed class OfflineDeleteEligibility
{
    public bool CanDelete { get; init; }
    public OfflineDeleteBlockReason Reason { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid? LocalRecordId { get; init; }
    public Guid? ServerRecordId { get; init; }
}
