using RegNeps.Domain.Entities;

namespace RegNeps.Application.Abstractions;

/// <summary>
/// Persistencia atómica de creación observable por sync:
/// NepRecord + SyncChangeLog (RecordUpserted) en el mismo DbContext/transacción.
/// </summary>
public interface IAtomicNepRecordCreateStore
{
    /// <summary>
    /// Inserta el registro y su ChangeLog. Si ya existe la clave
    /// (CreatedByUserId, ClientOperationId), retorna el existente con <c>Inserted=false</c>
    /// sin crear un segundo ChangeLog.
    /// </summary>
    Task<AtomicNepRecordCreateResult> CreateWithChangeLogAsync(
        NepRecord record,
        string actorUserId,
        string? deviceId,
        CancellationToken ct = default);
}

public sealed class AtomicNepRecordCreateResult
{
    public required NepRecord Record { get; init; }
    public bool Inserted { get; init; }
    public long? ChangeSequence { get; init; }
}
