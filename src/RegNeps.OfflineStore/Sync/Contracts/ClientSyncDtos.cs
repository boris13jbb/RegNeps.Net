using System.Text.Json;
using System.Text.Json.Serialization;
using RegNeps.Domain.Sync;

namespace RegNeps.OfflineStore.Sync.Contracts;

/// <summary>
/// DTOs del cliente SyncEngine (JSON camelCase).
/// Mapeo 1:1 con <c>RegNeps.Application.Sync</c> SyncPush/Pull* — OfflineStore no referencia Application.
/// </summary>
public static class ClientSyncJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class ClientSyncPushRequest
{
    public int ProtocolVersion { get; set; } = SyncConstants.ProtocolVersion;
    public string DeviceId { get; set; } = string.Empty;
    public List<ClientSyncOperationDto> Operations { get; set; } = [];
}

public sealed class ClientSyncOperationDto
{
    public string ClientOperationId { get; set; } = string.Empty;
    public string OperationType { get; set; } = string.Empty;
    public JsonElement? Payload { get; set; }
    public string? CaptureSessionId { get; set; }
    public string? ExpectedConcurrencyStamp { get; set; }
    public DateTime? ClientCreatedAtUtc { get; set; }
}

public sealed class ClientSyncPushResponse
{
    public int ProtocolVersion { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public List<ClientSyncOperationResultDto> Results { get; set; } = [];
}

public sealed class ClientSyncOperationResultDto
{
    public string ClientOperationId { get; set; } = string.Empty;
    public string Result { get; set; } = "Invalid";
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public Guid? EntityId { get; set; }
    public string? ConcurrencyStamp { get; set; }
    public string? QualityLabel { get; set; }
    public long? ChangeSequence { get; set; }
    public string? ServerConcurrencyStamp { get; set; }
    public JsonElement? ServerSnapshot { get; set; }
}

public sealed class ClientSyncPullRequest
{
    public int ProtocolVersion { get; set; } = SyncConstants.ProtocolVersion;
    public string DeviceId { get; set; } = string.Empty;
    public long Cursor { get; set; }
    public int? PageSize { get; set; }
}

public sealed class ClientSyncPullResponse
{
    public int ProtocolVersion { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public long NextCursor { get; set; }
    public bool HasMore { get; set; }
    public List<ClientSyncChangeDto> Changes { get; set; } = [];
}

public sealed class ClientSyncChangeDto
{
    public long Sequence { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public JsonElement Payload { get; set; }
}

/// <summary>Réplica RecordUpserted (mismo shape que SyncNepRecordPayload del servidor).</summary>
public sealed class ClientNepRecordSnapshot
{
    public Guid Id { get; set; }
    public string Telar { get; set; } = string.Empty;
    public double Neps { get; set; }
    public double MtsCalculados { get; set; }
    public string Tela { get; set; } = string.Empty;
    public string LoteTrama { get; set; } = string.Empty;
    public string Turno { get; set; } = string.Empty;
    public string Operario { get; set; } = string.Empty;
    public string LineaProduccion { get; set; } = string.Empty;
    public string Observacion { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string ConcurrencyStamp { get; set; } = string.Empty;
    public string? ClientOperationId { get; set; }
    public string? CaptureSessionId { get; set; }
    public string? OwnerUserId { get; set; }
    public string QualityLabel { get; set; } = string.Empty;
    public string AccionCorrectiva { get; set; } = string.Empty;
    public string ResponsableRevision { get; set; } = string.Empty;
    public bool RevisadoPorSupervisor { get; set; }
    public DateTime? FechaRevisionUtc { get; set; }
}

public sealed class ClientNepRecordDeletedSnapshot
{
    public Guid Id { get; set; }
    public string OwnerUserId { get; set; } = string.Empty;
    public DateTime DeletedAtUtc { get; set; }
    public string? LastConcurrencyStamp { get; set; }
}

public static class ClientSyncResultNames
{
    public const string Accepted = "Accepted";
    public const string Duplicate = "Duplicate";
    public const string Forbidden = "Forbidden";
    public const string Invalid = "Invalid";
    public const string TransientError = "TransientError";
    public const string Conflict = "Conflict";
}
