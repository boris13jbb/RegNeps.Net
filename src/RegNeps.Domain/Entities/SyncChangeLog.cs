namespace RegNeps.Domain.Entities;

/// <summary>
/// Log global monotónico de cambios sincronizables (cursor = Sequence).
/// No es una credencial ni sustituye la autorización en Pull.
/// </summary>
public sealed class SyncChangeLog
{
    /// <summary>Secuencia global monotónica (IDENTITY / AUTOINCREMENT).</summary>
    public long Sequence { get; set; }

    /// <summary>Tipo de entidad, p. ej. <c>NepRecord</c>. Preparado para catálogos futuros.</summary>
    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    /// <summary>Tipo de cambio, p. ej. <c>RecordUpserted</c>.</summary>
    public string ChangeType { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    /// <summary>Usuario autenticado que originó el cambio (claims del servidor).</summary>
    public string ActorUserId { get; set; } = string.Empty;

    /// <summary>
    /// Propietario del registro para filtrar Pull sin depender solo del reloj.
    /// Para NepRecord coincide con CreatedByUserId.
    /// </summary>
    public string OwnerUserId { get; set; } = string.Empty;

    public string? ClientOperationId { get; set; }

    /// <summary>Auditoría de dispositivo; no es identidad de autorización.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Snapshot mínimo JSON para reconstruir el cambio en Pull (sin secretos).</summary>
    public string PayloadJson { get; set; } = "{}";
}
