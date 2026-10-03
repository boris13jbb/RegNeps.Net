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
    /// ApplyCorrective + RecordUpserted atómicos.
    /// Online: stamp/clientOp/device null. Push sync: stamp + ClientOperationId obligatorios.
    /// </summary>
    Task<AtomicNepRecordMutationResult> ApplyCorrectiveWithChangeLogAsync(
        Guid entityId,
        string accion,
        string responsable,
        bool marcarRevisado,
        RecordActor actor,
        string? expectedConcurrencyStamp = null,
        string? clientOperationId = null,
        string? deviceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// FASE 2D.12: ClearAll admin global — N tombstones RecordDeleted + delete físico
    /// en una sola transacción. Sin ExpectedConcurrencyStamp ni ClientOperationId.
    /// No comprueba ownership por fila (autorización = ClearAllRecords en el servicio).
    /// </summary>
    Task<AtomicClearAllResult> ClearAllWithTombstonesAsync(
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

/// <summary>Resultado de ClearAll sync-safe (N tombstones atómicos).</summary>
public sealed class AtomicClearAllResult
{
    public int DeletedCount { get; init; }
    public int TombstoneCount { get; init; }
    public long? FirstSequence { get; init; }
    public long? LastSequence { get; init; }
}
