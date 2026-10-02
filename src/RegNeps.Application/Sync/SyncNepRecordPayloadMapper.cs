using System.Text.Json;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.Domain.Sync;

namespace RegNeps.Application.Sync;

/// <summary>
/// Payload canónico de réplica para Pull (Create/Update/Delete).
/// </summary>
public static class SyncNepRecordPayloadMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ToPayloadJson(NepRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var level = AlertEvaluator.GetLevel(record.Neps);
        var snapshot = new SyncNepRecordPayload
        {
            Id = record.Id,
            Telar = record.Telar,
            Neps = record.Neps,
            MtsCalculados = record.MtsCalculados,
            Tela = record.Tela,
            LoteTrama = record.LoteTrama,
            Turno = record.Turno,
            Operario = record.Operario,
            LineaProduccion = record.LineaProduccion,
            Observacion = record.Observacion,
            CreatedAtUtc = record.CreatedAt,
            UpdatedAtUtc = record.UpdatedAt,
            ConcurrencyStamp = record.ConcurrencyStamp,
            ClientOperationId = record.ClientOperationId,
            CaptureSessionId = record.CaptureSessionId,
            OwnerUserId = record.CreatedByUserId,
            QualityLabel = level.ToDisplayLabel()
        };
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static JsonElement ToPayloadElement(NepRecord record)
    {
        using var doc = JsonDocument.Parse(ToPayloadJson(record));
        return doc.RootElement.Clone();
    }

    public static string ToTombstonePayloadJson(
        Guid entityId,
        string ownerUserId,
        DateTime deletedAtUtc,
        string? lastConcurrencyStamp)
    {
        var snapshot = new SyncNepRecordDeletedPayload
        {
            Id = entityId,
            OwnerUserId = ownerUserId,
            DeletedAtUtc = deletedAtUtc,
            LastConcurrencyStamp = lastConcurrencyStamp
        };
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static SyncChangeLog CreateRecordUpsertedEntry(
        NepRecord record,
        string actorUserId,
        string? deviceId,
        string? clientOperationId,
        DateTime? occurredAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        return new SyncChangeLog
        {
            EntityType = SyncConstants.EntityNepRecord,
            EntityId = record.Id,
            ChangeType = SyncConstants.ChangeRecordUpserted,
            OccurredAtUtc = occurredAtUtc ?? DateTime.UtcNow,
            ActorUserId = actorUserId.Trim(),
            OwnerUserId = record.CreatedByUserId ?? actorUserId.Trim(),
            // El ClientOperationId del ChangeLog es el de ESTA operación (Create/Update),
            // no el de creación del NepRecord (evita chocar el índice único en Updates online).
            ClientOperationId = string.IsNullOrWhiteSpace(clientOperationId)
                ? null
                : clientOperationId.Trim(),
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim(),
            PayloadJson = ToPayloadJson(record)
        };
    }

    public static SyncChangeLog CreateRecordDeletedEntry(
        Guid entityId,
        string ownerUserId,
        string actorUserId,
        string? lastConcurrencyStamp,
        string? clientOperationId,
        string? deviceId,
        DateTime deletedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        return new SyncChangeLog
        {
            EntityType = SyncConstants.EntityNepRecord,
            EntityId = entityId,
            ChangeType = SyncConstants.ChangeRecordDeleted,
            OccurredAtUtc = deletedAtUtc,
            ActorUserId = actorUserId.Trim(),
            OwnerUserId = ownerUserId.Trim(),
            ClientOperationId = string.IsNullOrWhiteSpace(clientOperationId) ? null : clientOperationId.Trim(),
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim(),
            PayloadJson = ToTombstonePayloadJson(entityId, ownerUserId, deletedAtUtc, lastConcurrencyStamp)
        };
    }
}

public sealed class SyncNepRecordPayload
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
}

public sealed class SyncNepRecordDeletedPayload
{
    public Guid Id { get; set; }
    public string OwnerUserId { get; set; } = string.Empty;
    public DateTime DeletedAtUtc { get; set; }
    public string? LastConcurrencyStamp { get; set; }
}
