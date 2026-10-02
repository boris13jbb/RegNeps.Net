using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Sync;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Sync;

/// <summary>
/// Push/Pull durable. Create/Update/Delete delegan en <see cref="IAtomicNepRecordCreateStore"/>
/// (mismo camino atómico que la captura online).
/// </summary>
public sealed class SyncPersistence : ISyncPersistence
{
    private readonly IDbContextFactory<RegNepsDbContext> _factory;
    private readonly IAtomicNepRecordCreateStore _atomicCreate;

    public SyncPersistence(
        IDbContextFactory<RegNepsDbContext> factory,
        IAtomicNepRecordCreateStore atomicCreate)
    {
        _factory = factory;
        _atomicCreate = atomicCreate;
    }

    public async Task<SyncCreatePersistResult> CreateRecordAtomicallyAsync(
        CreateNepRecordRequest request,
        RecordActor actor,
        string deviceId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var opId = request.ClientOperationId!.Trim();
        var sessionId = string.IsNullOrWhiteSpace(request.CaptureSessionId)
            ? null
            : request.CaptureSessionId.Trim();

        // Comprobación rápida de sesión / duplicate fuera de TX (el store revalida en TX).
        await using (var peek = await _factory.CreateDbContextAsync(ct))
        {
            var existing = await peek.NepRecords.AsNoTracking()
                .FirstOrDefaultAsync(r =>
                    r.CreatedByUserId == actor.UserId && r.ClientOperationId == opId, ct);

            if (existing is not null)
            {
                if (sessionId is not null
                    && !string.IsNullOrWhiteSpace(existing.CaptureSessionId)
                    && !string.Equals(existing.CaptureSessionId, sessionId, StringComparison.Ordinal))
                {
                    return new SyncCreatePersistResult
                    {
                        Result = SyncOperationResult.Forbidden,
                        ErrorCode = "SESSION_MISMATCH",
                        Message = "La operación no pertenece a la sesión de captura actual."
                    };
                }

                var existingSequence = await peek.SyncChangeLogs.AsNoTracking()
                    .Where(c => c.EntityType == SyncConstants.EntityNepRecord
                                && c.EntityId == existing.Id
                                && c.ClientOperationId == opId)
                    .OrderBy(c => c.Sequence)
                    .Select(c => (long?)c.Sequence)
                    .FirstOrDefaultAsync(ct);

                return new SyncCreatePersistResult
                {
                    Result = SyncOperationResult.Duplicate,
                    Record = existing,
                    ChangeSequence = existingSequence
                };
            }
        }

        var now = DateTime.UtcNow;
        var lote = string.IsNullOrWhiteSpace(request.LoteTrama)
            ? NepsConstants.LoteTramaPrefix
            : request.LoteTrama.Trim().ToUpperInvariant();

        var record = new NepRecord
        {
            Id = Guid.NewGuid(),
            Telar = request.Telar.Trim(),
            Neps = request.Neps,
            Tela = request.Tela?.Trim() ?? string.Empty,
            LoteTrama = lote,
            Turno = request.Turno?.Trim() ?? string.Empty,
            Operario = request.Operario?.Trim() ?? string.Empty,
            LineaProduccion = request.LineaProduccion?.Trim() ?? string.Empty,
            Observacion = request.Observacion?.Trim() ?? string.Empty,
            CreatedAt = now,
            CreatedByUserId = actor.UserId,
            CreatedByEmail = actor.Username,
            CreatedByRole = actor.EffectiveRole.ToString(),
            ClientOperationId = opId,
            CaptureSessionId = sessionId,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        try
        {
            var outcome = await _atomicCreate.CreateWithChangeLogAsync(
                record,
                actor.UserId,
                deviceId,
                ct);

            return new SyncCreatePersistResult
            {
                Result = outcome.Inserted
                    ? SyncOperationResult.Accepted
                    : SyncOperationResult.Duplicate,
                Record = outcome.Record,
                ChangeSequence = outcome.ChangeSequence
            };
        }
        catch (DbUpdateException)
        {
            return new SyncCreatePersistResult
            {
                Result = SyncOperationResult.TransientError,
                ErrorCode = "PERSISTENCE",
                Message = "Error temporal al procesar la operación."
            };
        }
    }

    public Task<AtomicNepRecordMutationResult> UpdateRecordAtomicallyAsync(
        SyncUpdateRecordPayload fields,
        string expectedConcurrencyStamp,
        string clientOperationId,
        string? captureSessionId,
        RecordActor actor,
        string deviceId,
        CancellationToken ct = default) =>
        _atomicCreate.UpdateWithChangeLogAsync(
            fields, expectedConcurrencyStamp, clientOperationId, captureSessionId, actor, deviceId, ct);

    public Task<AtomicNepRecordMutationResult> DeleteRecordAtomicallyAsync(
        Guid entityId,
        string expectedConcurrencyStamp,
        string clientOperationId,
        RecordActor actor,
        string deviceId,
        CancellationToken ct = default) =>
        _atomicCreate.DeleteWithTombstoneAsync(
            entityId, expectedConcurrencyStamp, clientOperationId, actor, deviceId, ct);

    public async Task<SyncPullPersistResult> PullAuthorizedChangesAsync(
        RecordActor actor,
        long cursor,
        int pageSize,
        CancellationToken ct = default)
    {
        pageSize = SyncProtocol.NormalizePageSize(pageSize);
        cursor = cursor < 0 ? 0 : cursor;

        await using var db = await _factory.CreateDbContextAsync(ct);

        string? externalUid = null;
        if (!actor.SeesAllRecords && Guid.TryParse(actor.UserId, out var viewerGuid))
        {
            externalUid = await db.Users.AsNoTracking()
                .Where(u => u.Id == viewerGuid)
                .Select(u => u.ExternalUserId)
                .FirstOrDefaultAsync(ct);
        }

        long nextCursor = cursor;
        var authorized = new List<SyncChangeLog>(pageSize);
        const int scanBatch = 100;
        var scannedAny = false;

        while (authorized.Count < pageSize)
        {
            var batch = await db.SyncChangeLogs.AsNoTracking()
                .Where(c => c.Sequence > nextCursor)
                .OrderBy(c => c.Sequence)
                .Take(scanBatch)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            scannedAny = true;
            foreach (var item in batch)
            {
                nextCursor = item.Sequence;
                if (!IsEntitySupportedForPull(item.EntityType))
                {
                    continue;
                }

                if (!IsAuthorized(actor, item, externalUid))
                {
                    continue;
                }

                authorized.Add(item);
                if (authorized.Count >= pageSize)
                {
                    break;
                }
            }

            if (batch.Count < scanBatch)
            {
                break;
            }
        }

        var hasMore = await db.SyncChangeLogs.AsNoTracking()
            .AnyAsync(c => c.Sequence > nextCursor, ct);

        if (!scannedAny)
        {
            var maxSeq = await db.SyncChangeLogs.AsNoTracking()
                .Select(c => (long?)c.Sequence)
                .MaxAsync(ct) ?? 0L;
            nextCursor = cursor > maxSeq ? maxSeq : cursor;
            hasMore = false;
        }

        return new SyncPullPersistResult
        {
            NextCursor = nextCursor,
            HasMore = hasMore,
            AuthorizedChanges = authorized
        };
    }

    public async Task<long> CountChangeLogsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.SyncChangeLogs.LongCountAsync(ct);
    }

    public async Task<int> CountRecordsByClientOperationAsync(
        string userId,
        string clientOperationId,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.NepRecords.CountAsync(r =>
            r.CreatedByUserId == userId && r.ClientOperationId == clientOperationId, ct);
    }

    private static bool IsEntitySupportedForPull(string entityType) =>
        string.Equals(entityType, SyncConstants.EntityNepRecord, StringComparison.Ordinal);

    private static bool IsAuthorized(RecordActor actor, SyncChangeLog item, string? externalUid)
    {
        if (actor.SeesAllRecords)
        {
            return true;
        }

        if (string.Equals(item.OwnerUserId, actor.UserId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(externalUid)
            && string.Equals(item.OwnerUserId, externalUid, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
