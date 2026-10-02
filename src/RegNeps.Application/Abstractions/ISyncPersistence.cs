using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Entities;

namespace RegNeps.Application.Abstractions;

/// <summary>
/// Persistencia atómica de sync sobre un único DbContext/transacción.
/// Necesario porque el repositorio de registros usa factory (contexto por operación).
/// </summary>
public interface ISyncPersistence
{
    /// <summary>
    /// Crea NepRecord + SyncChangeLog en una sola transacción, o devuelve Duplicate
    /// si ya existe (CreatedByUserId, ClientOperationId).
    /// </summary>
    Task<SyncCreatePersistResult> CreateRecordAtomicallyAsync(
        CreateNepRecordRequest request,
        RecordActor actor,
        string deviceId,
        CancellationToken ct = default);

    Task<SyncPullPersistResult> PullAuthorizedChangesAsync(
        RecordActor actor,
        long cursor,
        int pageSize,
        CancellationToken ct = default);

    Task<long> CountChangeLogsAsync(CancellationToken ct = default);

    Task<int> CountRecordsByClientOperationAsync(
        string userId,
        string clientOperationId,
        CancellationToken ct = default);
}

public sealed class SyncCreatePersistResult
{
    public SyncOperationResult Result { get; init; }
    public string? Message { get; init; }
    public string? ErrorCode { get; init; }
    public NepRecord? Record { get; init; }
    public long? ChangeSequence { get; init; }
}

public sealed class SyncPullPersistResult
{
    public long NextCursor { get; init; }
    public bool HasMore { get; init; }
    public IReadOnlyList<SyncChangeLog> AuthorizedChanges { get; init; } = [];
}
