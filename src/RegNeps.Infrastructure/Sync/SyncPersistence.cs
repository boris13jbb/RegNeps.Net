using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.Domain.Sync;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Sync;

/// <summary>
/// Push/Pull durable sobre un único DbContext por operación (transacción real SQLite/SQL Server).
/// </summary>
public sealed class SyncPersistence : ISyncPersistence
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDbContextFactory<RegNepsDbContext> _factory;

    public SyncPersistence(IDbContextFactory<RegNepsDbContext> factory) =>
        _factory = factory;

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

        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var existing = await db.NepRecords
                .FirstOrDefaultAsync(r =>
                    r.CreatedByUserId == actor.UserId && r.ClientOperationId == opId, ct);

            if (existing is not null)
            {
                if (sessionId is not null
                    && !string.IsNullOrWhiteSpace(existing.CaptureSessionId)
                    && !string.Equals(existing.CaptureSessionId, sessionId, StringComparison.Ordinal))
                {
                    await tx.RollbackAsync(ct);
                    return new SyncCreatePersistResult
                    {
                        Result = SyncOperationResult.Forbidden,
                        ErrorCode = "SESSION_MISMATCH",
                        Message = "La operación no pertenece a la sesión de captura actual."
                    };
                }

                var existingSequence = await db.SyncChangeLogs.AsNoTracking()
                    .Where(c => c.EntityType == SyncConstants.EntityNepRecord
                                && c.EntityId == existing.Id
                                && c.ClientOperationId == opId)
                    .OrderBy(c => c.Sequence)
                    .Select(c => (long?)c.Sequence)
                    .FirstOrDefaultAsync(ct);

                await tx.CommitAsync(ct);
                return new SyncCreatePersistResult
                {
                    Result = SyncOperationResult.Duplicate,
                    Record = existing,
                    ChangeSequence = existingSequence
                };
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

            var level = AlertEvaluator.GetLevel(record.Neps);
            var snapshot = new SyncNepRecordSnapshot
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

            var change = new SyncChangeLog
            {
                EntityType = SyncConstants.EntityNepRecord,
                EntityId = record.Id,
                ChangeType = SyncConstants.ChangeRecordUpserted,
                OccurredAtUtc = now,
                ActorUserId = actor.UserId,
                OwnerUserId = actor.UserId,
                ClientOperationId = opId,
                DeviceId = deviceId,
                PayloadJson = JsonSerializer.Serialize(snapshot, PayloadJsonOptions)
            };

            db.NepRecords.Add(record);
            db.SyncChangeLogs.Add(change);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new SyncCreatePersistResult
            {
                Result = SyncOperationResult.Accepted,
                Record = record,
                ChangeSequence = change.Sequence
            };
        }
        catch (DbUpdateException ex) when (IsUniqueClientOperationViolation(ex))
        {
            await tx.RollbackAsync(ct);

            // Carrera: otro hilo insertó la misma operación. Releer y tratar como Duplicate.
            await using var read = await _factory.CreateDbContextAsync(ct);
            var raced = await read.NepRecords.AsNoTracking()
                .FirstOrDefaultAsync(r =>
                    r.CreatedByUserId == actor.UserId && r.ClientOperationId == opId, ct);
            if (raced is null)
            {
                return new SyncCreatePersistResult
                {
                    Result = SyncOperationResult.TransientError,
                    ErrorCode = "RACE",
                    Message = "Error temporal al procesar la operación."
                };
            }

            var seq = await read.SyncChangeLogs.AsNoTracking()
                .Where(c => c.EntityType == SyncConstants.EntityNepRecord
                            && c.EntityId == raced.Id
                            && c.ClientOperationId == opId)
                .OrderBy(c => c.Sequence)
                .Select(c => (long?)c.Sequence)
                .FirstOrDefaultAsync(ct);

            return new SyncCreatePersistResult
            {
                Result = SyncOperationResult.Duplicate,
                Record = raced,
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
                // ignorar fallo de rollback secundario
            }

            throw;
        }
    }

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

        // NextCursor = última Sequence examinada (incluye no autorizadas).
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

        // Sin filas nuevas: NextCursor permanece en el cursor solicitado.
        if (!scannedAny)
        {
            nextCursor = cursor;
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

    private static bool IsUniqueClientOperationViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("IX_NepRecords_CreatedBy_ClientOperation", StringComparison.OrdinalIgnoreCase)
               || message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SyncNepRecordSnapshot
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
}
