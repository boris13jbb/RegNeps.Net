using System.Text.Json;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Sync;

namespace RegNeps.Application.Sync;

/// <summary>
/// Payload canónico de catálogo mínimo (FASE 2D.9) para SyncChangeLog / Pull.
/// Solo referencia de captura: Id estable, kind, code, name, isActive.
/// </summary>
public static class SyncCatalogPayloadMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static SyncCatalogItemPayload FromFabric(Fabric fabric, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(fabric);
        var name = (fabric.Name ?? string.Empty).Trim();
        var code = string.IsNullOrWhiteSpace(fabric.Code) ? name : fabric.Code.Trim();
        return new SyncCatalogItemPayload
        {
            Id = fabric.Id,
            Kind = SyncConstants.CatalogKindFabric,
            Code = code,
            Name = name,
            IsActive = isActive,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public static SyncCatalogItemPayload FromLote(LoteTramaItem lote, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(lote);
        var code = (lote.Code ?? string.Empty).Trim().ToUpperInvariant();
        return new SyncCatalogItemPayload
        {
            Id = lote.Id,
            Kind = SyncConstants.CatalogKindLote,
            Code = code,
            Name = code,
            IsActive = isActive,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public static string ToPayloadJson(SyncCatalogItemPayload payload) =>
        JsonSerializer.Serialize(payload, JsonOptions);

    public static SyncChangeLog CreateUpsertedEntry(
        SyncCatalogItemPayload payload,
        string? actorUserId = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var now = payload.UpdatedAtUtc == default ? DateTime.UtcNow : payload.UpdatedAtUtc;
        return new SyncChangeLog
        {
            EntityType = SyncConstants.EntityCatalogItem,
            EntityId = payload.Id,
            ChangeType = SyncConstants.ChangeCatalogUpserted,
            OccurredAtUtc = now,
            ActorUserId = string.IsNullOrWhiteSpace(actorUserId) ? "system" : actorUserId.Trim(),
            OwnerUserId = SyncConstants.CatalogOwnerUserId,
            ClientOperationId = null,
            DeviceId = null,
            PayloadJson = ToPayloadJson(payload)
        };
    }

    public static SyncChangeLog CreateDeletedEntry(
        SyncCatalogItemPayload payload,
        string? actorUserId = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        payload.IsActive = false;
        var now = DateTime.UtcNow;
        payload.UpdatedAtUtc = now;
        return new SyncChangeLog
        {
            EntityType = SyncConstants.EntityCatalogItem,
            EntityId = payload.Id,
            ChangeType = SyncConstants.ChangeCatalogDeleted,
            OccurredAtUtc = now,
            ActorUserId = string.IsNullOrWhiteSpace(actorUserId) ? "system" : actorUserId.Trim(),
            OwnerUserId = SyncConstants.CatalogOwnerUserId,
            ClientOperationId = null,
            DeviceId = null,
            PayloadJson = ToPayloadJson(payload)
        };
    }
}

public sealed class SyncCatalogItemPayload
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; }
}
