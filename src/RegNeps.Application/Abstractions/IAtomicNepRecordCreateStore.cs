using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Entities;

namespace RegNeps.Application.Abstractions;

/// <summary>
/// Persistencia atómica de mutaciones observables por sync
/// (NepRecord + SyncChangeLog en el mismo DbContext/transacción).
/// </summary>
public interface IAtomicNepRecordCreateStore
{
    Task<AtomicNepRecordCreateResult> CreateWithChangeLogAsync(
        NepRecord record,
        string actorUserId,
        string? deviceId,
        CancellationToken ct = default);

    /// <param name="expectedConcurrencyStamp">
    /// Si es null/vacío (ruta online sin stamp), no se aplica la comparación previa;
    /// el token EF sigue protegiendo la escritura.
    /// </param>
    /// <param name="clientOperationId">
    /// Obligatorio en Push sync; null en mutaciones online sin idempotencia de cliente.
    /// </param>
    Task<AtomicNepRecordMutationResult> UpdateWithChangeLogAsync(
        SyncUpdateRecordPayload fields,
        string? expectedConcurrencyStamp,
        string? clientOperationId,
        string? captureSessionId,
        RecordActor actor,
        string? deviceId,
        CancellationToken ct = default);

    Task<AtomicNepRecordMutationResult> DeleteWithTombstoneAsync(
        Guid entityId,
        string? expectedConcurrencyStamp,
        string? clientOperationId,
        RecordActor actor,
        string? deviceId,
        CancellationToken ct = default);

    /// <summary>
    /// Mutación online ApplyCorrective + RecordUpserted en la misma transacción.
    /// Sin ClientOperationId (no forma parte del Push sync v1).
    /// </summary>
    Task<AtomicNepRecordMutationResult> ApplyCorrectiveWithChangeLogAsync(
        Guid entityId,
        string accion,
        string responsable,
        bool marcarRevisado,
        RecordActor actor,
        CancellationToken ct = default);
}

public sealed class AtomicNepRecordCreateResult
{
    public required NepRecord Record { get; init; }
    public bool Inserted { get; init; }
    public long? ChangeSequence { get; init; }
}

public sealed class AtomicNepRecordMutationResult
{
    public SyncOperationResult Result { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public NepRecord? Record { get; init; }
    public long? ChangeSequence { get; init; }
    public string? ServerConcurrencyStamp { get; init; }
    public string? ServerSnapshotJson { get; init; }
    public Guid? EntityId { get; init; }
}
