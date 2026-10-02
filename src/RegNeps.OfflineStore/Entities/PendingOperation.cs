using RegNeps.OfflineStore.Enums;

namespace RegNeps.OfflineStore.Entities;

/// <summary>Operación durable en Outbox local (sobrevive reinicios).</summary>
public sealed class PendingOperation
{
    public Guid Id { get; set; }

    /// <summary>Clave de idempotencia futura contra el servidor. Única por dispositivo.</summary>
    public string ClientOperationId { get; set; } = string.Empty;

    public OfflineOperationType OperationType { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public int ProtocolVersion { get; set; } = OfflineStoreConstants.ProtocolVersion;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastAttemptAtUtc { get; set; }

    public int AttemptCount { get; set; }

    public PendingOperationStatus Status { get; set; } = PendingOperationStatus.Pending;

    public string? LastError { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public string? CaptureSessionId { get; set; }

    public Guid? LocalNepRecordId { get; set; }

    public Guid? TargetServerRecordId { get; set; }

    public string? ExpectedConcurrencyStamp { get; set; }

    public LocalNepRecord? LocalNepRecord { get; set; }
}
