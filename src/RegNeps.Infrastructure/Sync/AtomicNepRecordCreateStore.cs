using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Sync;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Sync;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Sync;

/// <summary>
/// Único punto de escritura atómica NepRecord + SyncChangeLog (online y Push).
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
            if (opId is not null && !string.IsNullOrWhiteSpace(record.CreatedByUserId))
            {
                var existing = await db.NepRecords
                    .FirstOrDefaultAsync(r =>
                        r.CreatedByUserId == record.CreatedByUserId
                        && r.ClientOperationId == opId, ct);

                if (existing is not null)
                {
                    var existingSeq = await FindChangeSequenceAsync(db, existing, opId, ct);
                    await tx.CommitAsync(ct);
                    return new AtomicNepRecordCreateResult
                    {
                        Record = existing,
                        Inserted = false,
                        ChangeSequence = existingSeq
                    };
                }
            }

            var change = SyncNepRecordPayloadMapper.CreateRecordUpsertedEntry(
                record,
                actor,
                deviceId,
                record.CreatedAt);

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

            if (opId is null || string.IsNullOrWhiteSpace(record.CreatedByUserId))
            {
                throw;
            }

            await using var read = await _factory.CreateDbContextAsync(ct);
            var raced = await read.NepRecords.AsNoTracking()
                .FirstOrDefaultAsync(r =>
                    r.CreatedByUserId == record.CreatedByUserId
                    && r.ClientOperationId == opId, ct);

            if (raced is null)
            {
                throw;
            }

            var seq = await FindChangeSequenceAsync(read, raced, opId, ct);
            return new AtomicNepRecordCreateResult
            {
                Record = raced,
                Inserted = false,
                ChangeSequence = seq
            };
        }
        catch
        {
            try
            {
                await tx.RollbackAsync(ct);
            }
            catch
            {
                // ignorar rollback secundario
            }

            throw;
        }
    }

    private static Task<long?> FindChangeSequenceAsync(
        RegNepsDbContext db,
        NepRecord record,
        string? opId,
        CancellationToken ct) =>
        db.SyncChangeLogs.AsNoTracking()
            .Where(c => c.EntityType == SyncConstants.EntityNepRecord
                        && c.EntityId == record.Id
                        && (opId == null || c.ClientOperationId == opId))
            .OrderBy(c => c.Sequence)
            .Select(c => (long?)c.Sequence)
            .FirstOrDefaultAsync(ct);
}
