using System.Text.Json;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;

namespace RegNeps.Application.Sync;

/// <summary>
/// Payload canónico de réplica para Pull (online y Push deben producir el mismo formato).
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
            ConcurrencyStamp = record.ConcurrencyStamp,
            ClientOperationId = record.ClientOperationId,
            CaptureSessionId = record.CaptureSessionId,
            OwnerUserId = record.CreatedByUserId,
            QualityLabel = level.ToDisplayLabel()
        };
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static SyncChangeLog CreateRecordUpsertedEntry(
        NepRecord record,
        string actorUserId,
        string? deviceId,
        DateTime? occurredAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        return new SyncChangeLog
        {
            EntityType = Domain.Sync.SyncConstants.EntityNepRecord,
            EntityId = record.Id,
            ChangeType = Domain.Sync.SyncConstants.ChangeRecordUpserted,
            OccurredAtUtc = occurredAtUtc ?? DateTime.UtcNow,
            ActorUserId = actorUserId.Trim(),
            OwnerUserId = record.CreatedByUserId ?? actorUserId.Trim(),
            ClientOperationId = record.ClientOperationId,
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim(),
            PayloadJson = ToPayloadJson(record)
        };
    }
}

/// <summary>DTO de payload en ChangeLog / Pull (no es entidad EF).</summary>
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
    public string ConcurrencyStamp { get; set; } = string.Empty;
    public string? ClientOperationId { get; set; }
    public string? CaptureSessionId { get; set; }
    public string? OwnerUserId { get; set; }
    public string QualityLabel { get; set; } = string.Empty;
}
