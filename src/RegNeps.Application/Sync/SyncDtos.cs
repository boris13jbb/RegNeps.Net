using System.Text.Json;

namespace RegNeps.Application.Sync;

public enum SyncOperationResult
{
    Accepted = 0,
    Duplicate = 1,
    Forbidden = 2,
    Invalid = 3,
    TransientError = 4,
    Conflict = 5
}

public sealed class SyncPushRequest
{
    public int ProtocolVersion { get; set; } = SyncProtocol.Version;
    public string DeviceId { get; set; } = string.Empty;
    public List<SyncOperationDto> Operations { get; set; } = [];
}

public sealed class SyncOperationDto
{
    public string ClientOperationId { get; set; } = string.Empty;
    public string OperationType { get; set; } = string.Empty;
    public JsonElement? Payload { get; set; }
    public string? CaptureSessionId { get; set; }
    public string? ExpectedConcurrencyStamp { get; set; }
    public DateTime? ClientCreatedAtUtc { get; set; }
}

public sealed class SyncPushResponse
{
    public int ProtocolVersion { get; set; } = SyncProtocol.Version;
    public DateTime ServerTimeUtc { get; set; }
    public List<SyncOperationResultDto> Results { get; set; } = [];
}

public sealed class SyncOperationResultDto
{
    public string ClientOperationId { get; set; } = string.Empty;
    public string Result { get; set; } = nameof(SyncOperationResult.Invalid);
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public Guid? EntityId { get; set; }
    public string? ConcurrencyStamp { get; set; }
    public string? QualityLabel { get; set; }
    public long? ChangeSequence { get; set; }

    /// <summary>Stamp actual del servidor en Conflict.</summary>
    public string? ServerConcurrencyStamp { get; set; }

    /// <summary>Snapshot canónico del servidor en Conflict (mismo shape que Pull).</summary>
    public JsonElement? ServerSnapshot { get; set; }
}

public sealed class SyncPullRequest
{
    public int ProtocolVersion { get; set; } = SyncProtocol.Version;
    public string DeviceId { get; set; } = string.Empty;
    public long Cursor { get; set; }
    public int? PageSize { get; set; }
}

public sealed class SyncPullResponse
{
    public int ProtocolVersion { get; set; } = SyncProtocol.Version;
    public DateTime ServerTimeUtc { get; set; }
    public long NextCursor { get; set; }
    public bool HasMore { get; set; }
    public List<SyncChangeDto> Changes { get; set; } = [];
}

public sealed class SyncChangeDto
{
    public long Sequence { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public JsonElement Payload { get; set; }
}

/// <summary>Payload CreateRecord (no es entidad EF).</summary>
public sealed class SyncCreateRecordPayload
{
    public string Telar { get; set; } = string.Empty;
    public double Neps { get; set; }
    public string Tela { get; set; } = string.Empty;
    public string LoteTrama { get; set; } = string.Empty;
    public string Turno { get; set; } = string.Empty;
    public string Operario { get; set; } = string.Empty;
    public string LineaProduccion { get; set; } = string.Empty;
    public string Observacion { get; set; } = string.Empty;
}

/// <summary>Payload UpdateRecord (campos de negocio; stamp también puede ir en la operación).</summary>
public sealed class SyncUpdateRecordPayload
{
    public Guid EntityId { get; set; }
    public string Telar { get; set; } = string.Empty;
    public double Neps { get; set; }
    public string Tela { get; set; } = string.Empty;
    public string LoteTrama { get; set; } = string.Empty;
    public string Turno { get; set; } = string.Empty;
    public string Operario { get; set; } = string.Empty;
    public string LineaProduccion { get; set; } = string.Empty;
    public string Observacion { get; set; } = string.Empty;

    /// <summary>Opcional si ya viene en SyncOperationDto.ExpectedConcurrencyStamp.</summary>
    public string? ExpectedConcurrencyStamp { get; set; }
}

/// <summary>Payload DeleteRecord.</summary>
public sealed class SyncDeleteRecordPayload
{
    public Guid EntityId { get; set; }

    /// <summary>Opcional si ya viene en SyncOperationDto.ExpectedConcurrencyStamp.</summary>
    public string? ExpectedConcurrencyStamp { get; set; }
}

/// <summary>
/// Payload ApplyCorrective (FASE 2D.10). Solo parámetros de corrección;
/// no permite mutar Telar/Neps/campos de captura.
/// </summary>
public sealed class SyncApplyCorrectivePayload
{
    public Guid EntityId { get; set; }
    public string Accion { get; set; } = string.Empty;
    public string Responsable { get; set; } = string.Empty;
    public bool MarcarRevisado { get; set; } = true;

    /// <summary>Opcional si ya viene en SyncOperationDto.ExpectedConcurrencyStamp.</summary>
    public string? ExpectedConcurrencyStamp { get; set; }
}
