using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;

namespace RegNeps.OfflineStore.Models;

/// <summary>Acciones de resolución explícita (Keep Server unifica Cancel).</summary>
[Flags]
public enum ConflictResolutionActions
{
    None = 0,
    KeepServer = 1,
    KeepLocal = 2,
    EditAndRetry = 4
}

public enum ConflictResolutionDecision
{
    KeepServer = 0,
    KeepLocal = 1,
    EditAndRetry = 2
}

public enum ConflictResolutionOutcome
{
    Success = 0,
    AlreadyResolved = 1,
    Rejected = 2,
    NotFound = 3,
    Unauthorized = 4,
    InvalidState = 5
}

/// <summary>Campos de negocio comparables (captura). QualityLabel se deriva de Neps.</summary>
public sealed class ConflictFieldSnapshot
{
    public string Telar { get; init; } = string.Empty;
    public double Neps { get; init; }
    public string Tela { get; init; } = string.Empty;
    public string LoteTrama { get; init; } = string.Empty;
    public string Turno { get; init; } = string.Empty;
    public string Operario { get; init; } = string.Empty;
    public string LineaProduccion { get; init; } = string.Empty;
    public string Observacion { get; init; } = string.Empty;
    public string QualityLabel { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }
    public string? ConcurrencyStamp { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
}

public sealed class ConflictFieldDiff
{
    public string FieldName { get; init; } = string.Empty;
    public string LocalValue { get; init; } = string.Empty;
    public string ServerValue { get; init; } = string.Empty;
}

public sealed class ConflictResolutionView
{
    public Guid OperationId { get; init; }
    public Guid? LocalNepRecordId { get; init; }
    public Guid? EntityId { get; init; }
    public OfflineConflictKind Kind { get; init; }
    public string KindLabel { get; init; } = string.Empty;
    public string ReasonLabel { get; init; } = string.Empty;
    public OfflineOperationType OperationType { get; init; }
    public string OriginalClientOperationId { get; init; } = string.Empty;
    public string? LastServerErrorCode { get; init; }
    public DateTime ConflictedAtUtc { get; init; }
    public ConflictFieldSnapshot Local { get; init; } = new();
    public ConflictFieldSnapshot? Server { get; init; }
    public bool ServerIsDeleted { get; init; }
    public IReadOnlyList<ConflictFieldDiff> Differences { get; init; } = Array.Empty<ConflictFieldDiff>();
    public ConflictResolutionActions AllowedActions { get; init; }
    public string KeepServerButtonText { get; init; } = "Aceptar versión del servidor";
    public string? KeepLocalButtonText { get; init; }
    public string? EditAndRetryHint { get; init; }
    public string? BlockedKeepLocalReason { get; init; }
}

public sealed class ConflictEditFields
{
    public string Telar { get; set; } = string.Empty;
    public double Neps { get; set; }
    public string? Tela { get; set; }
    public string? LoteTrama { get; set; }
    public string? Turno { get; set; }
    public string? Operario { get; set; }
    public string? LineaProduccion { get; set; }
    public string? Observacion { get; set; }
}

public sealed class ConflictResolutionResult
{
    public ConflictResolutionOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
    public OfflineConflictKind Kind { get; init; }
    public ConflictResolutionDecision? Decision { get; init; }
    public Guid? ClosedOperationId { get; init; }
    public Guid? NewOperationId { get; init; }
    public string? NewClientOperationId { get; init; }
    public string? ExpectedConcurrencyStamp { get; init; }
    public PendingOperation? ClosedOperation { get; init; }
    public PendingOperation? NewOperation { get; init; }
    public LocalNepRecord? Record { get; init; }
    public string? QualityLabel { get; init; }

    public bool IsSuccess =>
        Outcome is ConflictResolutionOutcome.Success or ConflictResolutionOutcome.AlreadyResolved;
}
