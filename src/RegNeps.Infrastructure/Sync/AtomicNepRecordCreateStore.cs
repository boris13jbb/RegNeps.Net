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
/// Escritura atómica NepRecord + SyncChangeLog (Create/Update/Delete) para online y Push.
/// Idempotencia de mutaciones: índice único (ActorUserId, ClientOperationId) en SyncChangeLogs.
/// </summary>
public sealed class AtomicNepRecordCreateStore : IAtomicNepRecordCreateStore
{
    private readonly IDbContextFactory<RegNepsDbContext> _factory;

    public AtomicNepRecordCreateStore(IDbContextFactory<RegNepsDbContext> factory) =>
        _factory = factory;

    public async Task<AtomicNepRecordCreateResult> CreateWithChangeLogAsync(
        NepRecord record,
        string actorUserId,
        string? deviceId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        if (string.IsNullOrWhiteSpace(record.ConcurrencyStamp))
        {
            record.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        }

        if (record.Id == Guid.Empty)
        {
            record.Id = Guid.NewGuid();
        }

        var actor = actorUserId.Trim();
        var opId = string.IsNullOrWhiteSpace(record.ClientOperationId)
            ? null
            : record.ClientOperationId.Trim();
        record.ClientOperationId = opId;

        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (opId is not null)
            {
                var processed = await FindProcessedByClientOpAsync(db, actor, opId, ct);
                if (processed is not null)
                {
                    var existingRecord = await db.NepRecords
                        .FirstOrDefaultAsync(r => r.Id == processed.EntityId, ct);
                    await tx.CommitAsync(ct);
                    return new AtomicNepRecordCreateResult
                    {
                        Record = existingRecord ?? record,
                        Inserted = false,
                        ChangeSequence = processed.Sequence
                    };
                }

                if (!string.IsNullOrWhiteSpace(record.CreatedByUserId))
                {
                    var existing = await db.NepRecords
                        .FirstOrDefaultAsync(r =>
                            r.CreatedByUserId == record.CreatedByUserId
                            && r.ClientOperationId == opId, ct);

                    if (existing is not null)
                    {
                        var existingSeq = await FindChangeSequenceForEntityAsync(db, existing.Id, opId, ct);
                        await tx.CommitAsync(ct);
                        return new AtomicNepRecordCreateResult
                        {
                            Record = existing,
                            Inserted = false,
                            ChangeSequence = existingSeq
                        };
                    }
                }
            }

            var change = SyncNepRecordPayloadMapper.CreateRecordUpsertedEntry(
                record, actor, deviceId, opId, record.CreatedAt);

            db.NepRecords.Add(record);
            db.SyncChangeLogs.Add(change);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new AtomicNepRecordCreateResult
            {
                Record = record,
                Inserted = true,
                ChangeSequence = change.Sequence
            };
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);

            if (opId is null)
            {
                throw;
            }

            await using var read = await _factory.CreateDbContextAsync(ct);
            var processed = await FindProcessedByClientOpAsync(read, actor, opId, ct);
            if (processed is not null)
            {
                var racedRecord = await read.NepRecords.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == processed.EntityId, ct);
                if (racedRecord is not null)
                {
                    return new AtomicNepRecordCreateResult
                    {
                        Record = racedRecord,
                        Inserted = false,
                        ChangeSequence = processed.Sequence
                    };
                }
            }

            if (!string.IsNullOrWhiteSpace(record.CreatedByUserId))
            {
                var raced = await read.NepRecords.AsNoTracking()
                    .FirstOrDefaultAsync(r =>
                        r.CreatedByUserId == record.CreatedByUserId
                        && r.ClientOperationId == opId, ct);
                if (raced is not null)
                {
                    var seq = await FindChangeSequenceForEntityAsync(read, raced.Id, opId, ct);
                    return new AtomicNepRecordCreateResult
                    {
                        Record = raced,
                        Inserted = false,
                        ChangeSequence = seq
                    };
                }
            }

            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
            throw;
        }
    }

    public async Task<AtomicNepRecordMutationResult> UpdateWithChangeLogAsync(
        SyncUpdateRecordPayload fields,
        string? expectedConcurrencyStamp,
        string? clientOperationId,
        string? captureSessionId,
        RecordActor actor,
        string? deviceId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(actor);

        var opId = string.IsNullOrWhiteSpace(clientOperationId) ? null : clientOperationId.Trim();
        var stamp = string.IsNullOrWhiteSpace(expectedConcurrencyStamp)
            ? null
            : expectedConcurrencyStamp.Trim();

        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (opId is not null)
            {
                var processed = await FindProcessedByClientOpAsync(db, actor.UserId, opId, ct);
                if (processed is not null)
                {
                    var idempotent = await ResolveIdempotentUpsertAsync(
                        db, processed, fields.EntityId, ct);
                    await tx.CommitAsync(ct);
                    return idempotent;
                }
            }

            var entity = await db.NepRecords
                .FirstOrDefaultAsync(r => r.Id == fields.EntityId, ct);

            if (entity is null)
            {
                var tombstone = await FindLatestTombstoneAsync(db, fields.EntityId, ct);
                await tx.CommitAsync(ct);
                return new AtomicNepRecordMutationResult
                {
                    Result = SyncOperationResult.Invalid,
                    ErrorCode = tombstone is null ? "ENTITY_NOT_FOUND" : "ENTITY_DELETED",
                    Message = tombstone is null
                        ? "Registro no encontrado."
                        : "El registro ya fue eliminado.",
                    EntityId = fields.EntityId,
                    ChangeSequence = tombstone?.Sequence
                };
            }

            if (!CanMutate(actor, entity))
            {
                await tx.RollbackAsync(ct);
                return new AtomicNepRecordMutationResult
                {
                    Result = SyncOperationResult.Forbidden,
                    ErrorCode = "OWNERSHIP",
                    Message = "No puede modificar registros de otro usuario.",
                    EntityId = entity.Id
                };
            }

            if (stamp is not null
                && !string.Equals(entity.ConcurrencyStamp, stamp, StringComparison.Ordinal))
            {
                var snapshot = SyncNepRecordPayloadMapper.ToPayloadJson(entity);
                await tx.CommitAsync(ct);
                return new AtomicNepRecordMutationResult
                {
                    Result = SyncOperationResult.Conflict,
                    ErrorCode = "CONCURRENCY",
                    Message = "El registro fue modificado por otro usuario.",
                    EntityId = entity.Id,
                    Record = entity,
                    ServerConcurrencyStamp = entity.ConcurrencyStamp,
                    ServerSnapshotJson = snapshot
                };
            }

            entity.Telar = fields.Telar.Trim();
            entity.Neps = fields.Neps;
            entity.Tela = fields.Tela?.Trim() ?? string.Empty;
            entity.LoteTrama = string.IsNullOrWhiteSpace(fields.LoteTrama)
                ? NepsConstants.LoteTramaPrefix
                : fields.LoteTrama.Trim().ToUpperInvariant();
            entity.Turno = fields.Turno?.Trim() ?? string.Empty;
            entity.Operario = fields.Operario?.Trim() ?? string.Empty;
            entity.LineaProduccion = fields.LineaProduccion?.Trim() ?? string.Empty;
            entity.Observacion = fields.Observacion?.Trim() ?? string.Empty;
            entity.UpdatedAt = DateTime.UtcNow;
            entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            if (!string.IsNullOrWhiteSpace(captureSessionId))
            {
                entity.CaptureSessionId = captureSessionId.Trim();
            }

            var change = SyncNepRecordPayloadMapper.CreateRecordUpsertedEntry(
                entity, actor.UserId, deviceId, opId, entity.UpdatedAt);

            db.SyncChangeLogs.Add(change);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new AtomicNepRecordMutationResult
            {
                Result = SyncOperationResult.Accepted,
                Record = entity,
                EntityId = entity.Id,
                ChangeSequence = change.Sequence,
                ServerConcurrencyStamp = entity.ConcurrencyStamp
            };
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            await using var read = await _factory.CreateDbContextAsync(ct);
            var current = await read.NepRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == fields.EntityId, ct);
            if (current is null)
            {
                return new AtomicNepRecordMutationResult
                {
                    Result = SyncOperationResult.Invalid,
                    ErrorCode = "ENTITY_NOT_FOUND",
                    Message = "Registro no encontrado.",
                    EntityId = fields.EntityId
                };
            }

            return new AtomicNepRecordMutationResult
            {
                Result = SyncOperationResult.Conflict,
                ErrorCode = "CONCURRENCY",
                Message = "El registro fue modificado por otro usuario.",
                EntityId = current.Id,
                Record = current,
                ServerConcurrencyStamp = current.ConcurrencyStamp,
                ServerSnapshotJson = SyncNepRecordPayloadMapper.ToPayloadJson(current)
            };
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            if (opId is not null)
            {
                await using var read = await _factory.CreateDbContextAsync(ct);
                var processed = await FindProcessedByClientOpAsync(read, actor.UserId, opId, ct);
                if (processed is not null)
                {
                    return await ResolveIdempotentUpsertAsync(read, processed, fields.EntityId, ct);
                }
            }

            return new AtomicNepRecordMutationResult
            {
                Result = SyncOperationResult.TransientError,
                ErrorCode = "PERSISTENCE",
                Message = "Error temporal al procesar la operación."
            };
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
            throw;
        }
    }

    public async Task<AtomicNepRecordMutationResult> DeleteWithTombstoneAsync(
        Guid entityId,
        string? expectedConcurrencyStamp,
        string? clientOperationId,
        RecordActor actor,
        string? deviceId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var opId = string.IsNullOrWhiteSpace(clientOperationId) ? null : clientOperationId.Trim();
        var stamp = string.IsNullOrWhiteSpace(expectedConcurrencyStamp)
            ? null
            : expectedConcurrencyStamp.Trim();

        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (opId is not null)
            {
                var processed = await FindProcessedByClientOpAsync(db, actor.UserId, opId, ct);
                if (processed is not null)
                {
                    await tx.CommitAsync(ct);
                    return ResolveIdempotentDelete(processed, entityId);
                }
            }

            var entity = await db.NepRecords
                .FirstOrDefaultAsync(r => r.Id == entityId, ct);

            if (entity is null)
            {
                var tombstone = await FindLatestTombstoneAsync(db, entityId, ct);
                await tx.CommitAsync(ct);
                if (tombstone is not null)
                {
                    // Otro ClientOperationId / borrado previo: no re-tumbstone.
                    return new AtomicNepRecordMutationResult
                    {
                        Result = SyncOperationResult.Invalid,
                        ErrorCode = "ENTITY_DELETED",
                        Message = "El registro ya fue eliminado.",
                        EntityId = entityId,
                        ChangeSequence = tombstone.Sequence
                    };
                }

                return new AtomicNepRecordMutationResult
                {
                    Result = SyncOperationResult.Invalid,
                    ErrorCode = "ENTITY_NOT_FOUND",
                    Message = "Registro no encontrado.",
                    EntityId = entityId
                };
            }

            if (!CanMutate(actor, entity))
            {
                await tx.RollbackAsync(ct);
                return new AtomicNepRecordMutationResult
                {
                    Result = SyncOperationResult.Forbidden,
                    ErrorCode = "OWNERSHIP",
                    Message = "No puede eliminar registros de otro usuario.",
                    EntityId = entity.Id
                };
            }

            if (stamp is not null
                && !string.Equals(entity.ConcurrencyStamp, stamp, StringComparison.Ordinal))
            {
                var snapshot = SyncNepRecordPayloadMapper.ToPayloadJson(entity);
                await tx.CommitAsync(ct);
                return new AtomicNepRecordMutationResult
                {
                    Result = SyncOperationResult.Conflict,
                    ErrorCode = "CONCURRENCY",
                    Message = "El registro fue modificado por otro usuario.",
                    EntityId = entity.Id,
                    Record = entity,
                    ServerConcurrencyStamp = entity.ConcurrencyStamp,
                    ServerSnapshotJson = snapshot
                };
            }

            var deletedAt = DateTime.UtcNow;
            var owner = entity.CreatedByUserId ?? actor.UserId;
            var lastStamp = entity.ConcurrencyStamp;
            var change = SyncNepRecordPayloadMapper.CreateRecordDeletedEntry(
                entity.Id, owner, actor.UserId, lastStamp, opId, deviceId, deletedAt);

            db.SyncChangeLogs.Add(change);
            db.NepRecords.Remove(entity);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new AtomicNepRecordMutationResult
            {
                Result = SyncOperationResult.Accepted,
                EntityId = entityId,
                ChangeSequence = change.Sequence,
                ServerConcurrencyStamp = lastStamp
            };
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            if (opId is not null)
            {
                await using var read = await _factory.CreateDbContextAsync(ct);
                var processed = await FindProcessedByClientOpAsync(read, actor.UserId, opId, ct);
                if (processed is not null)
                {
                    return ResolveIdempotentDelete(processed, entityId);
                }
            }

            return new AtomicNepRecordMutationResult
            {
                Result = SyncOperationResult.TransientError,
                ErrorCode = "PERSISTENCE",
                Message = "Error temporal al procesar la operación."
            };
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Duplicate solo si el ChangeLog previo es el mismo upsert (mismo EntityId + RecordUpserted).
    /// Reutilizar ClientOperationId tras un Delete u otra entidad → Invalid (no fingir éxito).
    /// </summary>
    private static async Task<AtomicNepRecordMutationResult> ResolveIdempotentUpsertAsync(
        RegNepsDbContext db,
        SyncChangeLog processed,
        Guid expectedEntityId,
        CancellationToken ct)
    {
        if (!string.Equals(processed.ChangeType, SyncConstants.ChangeRecordUpserted, StringComparison.Ordinal)
            || processed.EntityId != expectedEntityId)
        {
            return new AtomicNepRecordMutationResult
            {
                Result = SyncOperationResult.Invalid,
                ErrorCode = "CLIENT_OPERATION_REUSED",
                Message =
                    "ClientOperationId ya fue usado en otra operación. Genere un nuevo ClientOperationId.",
                EntityId = expectedEntityId,
                ChangeSequence = processed.Sequence
            };
        }

        var prior = await db.NepRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == processed.EntityId, ct);
        return DuplicateMutation(processed, prior);
    }

    /// <summary>
    /// Duplicate solo si el ChangeLog previo es el mismo tombstone (mismo EntityId + RecordDeleted).
    /// </summary>
    private static AtomicNepRecordMutationResult ResolveIdempotentDelete(
        SyncChangeLog processed,
        Guid expectedEntityId)
    {
        if (!string.Equals(processed.ChangeType, SyncConstants.ChangeRecordDeleted, StringComparison.Ordinal)
            || processed.EntityId != expectedEntityId)
        {
            return new AtomicNepRecordMutationResult
            {
                Result = SyncOperationResult.Invalid,
                ErrorCode = "CLIENT_OPERATION_REUSED",
                Message =
                    "ClientOperationId ya fue usado en otra operación. Genere un nuevo ClientOperationId.",
                EntityId = expectedEntityId,
                ChangeSequence = processed.Sequence
            };
        }

        return new AtomicNepRecordMutationResult
        {
            Result = SyncOperationResult.Duplicate,
            EntityId = processed.EntityId,
            ChangeSequence = processed.Sequence,
            ErrorCode = "ALREADY_PROCESSED",
            Message = "Operación ya procesada."
        };
    }

    private static AtomicNepRecordMutationResult DuplicateMutation(
        SyncChangeLog processed,
        NepRecord? record) =>
        new()
        {
            Result = SyncOperationResult.Duplicate,
            Record = record,
            EntityId = processed.EntityId,
            ChangeSequence = processed.Sequence,
            ServerConcurrencyStamp = record?.ConcurrencyStamp,
            ServerSnapshotJson = record is null ? null : SyncNepRecordPayloadMapper.ToPayloadJson(record),
            ErrorCode = "ALREADY_PROCESSED"
        };

    private static bool CanMutate(RecordActor actor, NepRecord record)
    {
        if (actor.SeesAllRecords)
        {
            return true;
        }

        if (string.Equals(record.CreatedByUserId, actor.UserId, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(actor.ExternalUserId)
               && string.Equals(record.CreatedByUserId, actor.ExternalUserId, StringComparison.Ordinal);
    }

    private static Task<SyncChangeLog?> FindProcessedByClientOpAsync(
        RegNepsDbContext db,
        string actorUserId,
        string clientOperationId,
        CancellationToken ct) =>
        db.SyncChangeLogs
            .FirstOrDefaultAsync(c =>
                c.ActorUserId == actorUserId
                && c.ClientOperationId == clientOperationId, ct);

    private static Task<SyncChangeLog?> FindLatestTombstoneAsync(
        RegNepsDbContext db,
        Guid entityId,
        CancellationToken ct) =>
        db.SyncChangeLogs.AsNoTracking()
            .Where(c => c.EntityType == SyncConstants.EntityNepRecord
                        && c.EntityId == entityId
                        && c.ChangeType == SyncConstants.ChangeRecordDeleted)
            .OrderByDescending(c => c.Sequence)
            .FirstOrDefaultAsync(ct);

    private static Task<long?> FindChangeSequenceForEntityAsync(
        RegNepsDbContext db,
        Guid entityId,
        string? opId,
        CancellationToken ct) =>
        db.SyncChangeLogs.AsNoTracking()
            .Where(c => c.EntityType == SyncConstants.EntityNepRecord
                        && c.EntityId == entityId
                        && (opId == null || c.ClientOperationId == opId))
            .OrderBy(c => c.Sequence)
            .Select(c => (long?)c.Sequence)
            .FirstOrDefaultAsync(ct);
}
