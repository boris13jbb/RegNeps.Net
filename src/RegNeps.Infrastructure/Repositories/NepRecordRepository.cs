using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Repositories;

public sealed class NepRecordRepository : INepRecordRepository
{
    private readonly IDbContextFactory<RegNepsDbContext> _factory;

    public NepRecordRepository(IDbContextFactory<RegNepsDbContext> factory) =>
        _factory = factory;

    public async Task<IReadOnlyList<NepRecord>> GetRecentAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.NepRecords
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<NepRecord>> QueryAsync(
        RecordFilters filters,
        string? viewerUserId,
        bool viewerSeesAll,
        int take = 500,
        CancellationToken ct = default)
    {
        filters ??= new RecordFilters();
        ReportDateRange.EnsureConsolidatedRange(filters);

        // Fail-closed: consulta personal sin usuario válido nunca se convierte en global.
        if (!viewerSeesAll && string.IsNullOrWhiteSpace(viewerUserId))
        {
            return Array.Empty<NepRecord>();
        }

        var hasDateRange = filters.FromUtc is not null
            || filters.ToExclusiveUtc is not null
            || filters.ToUtc is not null;
        var maxTake = hasDateRange ? 50_000 : 10_000;
        take = Math.Clamp(take, 1, maxTake);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.NepRecords.AsNoTracking().AsQueryable();

        if (!viewerSeesAll)
        {
            string? externalUid = null;
            if (Guid.TryParse(viewerUserId, out var viewerGuid))
            {
                externalUid = await db.Users.AsNoTracking()
                    .Where(u => u.Id == viewerGuid)
                    .Select(u => u.ExternalUserId)
                    .FirstOrDefaultAsync(ct);
            }

            if (!string.IsNullOrWhiteSpace(externalUid))
            {
                query = query.Where(r =>
                    r.CreatedByUserId == viewerUserId || r.CreatedByUserId == externalUid);
            }
            else
            {
                query = query.Where(r => r.CreatedByUserId == viewerUserId);
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.CaptureSessionId))
        {
            var sessionId = filters.CaptureSessionId.Trim();
            query = query.Where(r => r.CaptureSessionId == sessionId);
        }

        if (!string.IsNullOrWhiteSpace(filters.Telar))
        {
            var telar = filters.Telar.Trim().ToLower();
            query = query.Where(r => r.Telar.ToLower() == telar);
        }

        if (!string.IsNullOrWhiteSpace(filters.Tela))
        {
            var tela = filters.Tela.Trim().ToLower();
            query = query.Where(r => r.Tela.ToLower() == tela);
        }

        if (!string.IsNullOrWhiteSpace(filters.LoteTrama))
        {
            var lote = filters.LoteTrama.Trim().ToLower();
            query = query.Where(r => r.LoteTrama.ToLower() == lote);
        }

        if (!string.IsNullOrWhiteSpace(filters.Turno))
        {
            var turno = filters.Turno.Trim().ToLower();
            query = query.Where(r => r.Turno.ToLower() == turno);
        }

        if (!string.IsNullOrWhiteSpace(filters.Operario))
        {
            var operario = filters.Operario.Trim().ToLower();
            query = query.Where(r => r.Operario.ToLower() == operario);
        }

        if (!string.IsNullOrWhiteSpace(filters.LineaProduccion))
        {
            var linea = filters.LineaProduccion.Trim().ToLower();
            query = query.Where(r => r.LineaProduccion.ToLower() == linea);
        }

        if (!string.IsNullOrWhiteSpace(filters.Search))
        {
            var term = filters.Search.Trim().ToLower();
            query = query.Where(r =>
                r.Telar.ToLower().Contains(term) ||
                r.Tela.ToLower().Contains(term) ||
                r.LoteTrama.ToLower().Contains(term) ||
                r.Turno.ToLower().Contains(term) ||
                r.Operario.ToLower().Contains(term) ||
                r.LineaProduccion.ToLower().Contains(term) ||
                r.Observacion.ToLower().Contains(term) ||
                r.AccionCorrectiva.ToLower().Contains(term));
        }

        if (filters.NepsMin is not null)
        {
            query = query.Where(r => r.Neps >= filters.NepsMin.Value);
        }

        if (filters.NepsMax is not null)
        {
            query = query.Where(r => r.Neps <= filters.NepsMax.Value);
        }

        if (filters.MtsMin is not null)
        {
            var nepsMin = filters.MtsMin.Value * NepsConstants.TestLengthM;
            query = query.Where(r => r.Neps >= nepsMin);
        }

        if (filters.MtsMax is not null)
        {
            var nepsMax = filters.MtsMax.Value * NepsConstants.TestLengthM;
            query = query.Where(r => r.Neps <= nepsMax);
        }

        if (filters.FromUtc is not null)
        {
            query = query.Where(r => r.CreatedAt >= filters.FromUtc.Value);
        }

        if (filters.ToExclusiveUtc is not null)
        {
            query = query.Where(r => r.CreatedAt < filters.ToExclusiveUtc.Value);
        }
        else if (filters.ToUtc is not null)
        {
            query = query.Where(r => r.CreatedAt <= filters.ToUtc.Value);
        }

        if (filters.RevisadoPorSupervisor is not null)
        {
            query = query.Where(r => r.RevisadoPorSupervisor == filters.RevisadoPorSupervisor.Value);
        }

        if (filters.ConAccionCorrectiva is not null)
        {
            query = filters.ConAccionCorrectiva.Value
                ? query.Where(r => r.AccionCorrectiva != null && r.AccionCorrectiva != string.Empty)
                : query.Where(r => r.AccionCorrectiva == null || r.AccionCorrectiva == string.Empty);
        }

        if (filters.AlertLevel is not null || filters.SoloPendientes)
        {
            var config = await db.AlertConfigs.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AlertConfig();
            if (config.AlertasActivas)
            {
                var normalExclusive = config.LimiteNormalMax + 0.5;
                var warningExclusive = config.LimiteAdvertenciaMax + 0.5;

                if (filters.AlertLevel is not null)
                {
                    query = filters.AlertLevel.Value switch
                    {
                        AlertLevel.Normal => query.Where(r => r.Neps < normalExclusive),
                        AlertLevel.Advertencia => query.Where(r =>
                            r.Neps >= normalExclusive && r.Neps < warningExclusive),
                        AlertLevel.Critico => query.Where(r => r.Neps >= warningExclusive),
                        _ => query
                    };
                }

                if (filters.SoloPendientes)
                {
                    query = query.Where(r =>
                        !r.RevisadoPorSupervisor && r.Neps >= normalExclusive);
                }
            }
            else if (filters.AlertLevel is AlertLevel.Advertencia or AlertLevel.Critico
                     || filters.SoloPendientes)
            {
                return Array.Empty<NepRecord>();
            }
        }

        return await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Telar)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<NepRecord?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.NepRecords
            .AsNoTracking()
            .Include(r => r.HistorialAcciones)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<NepRecord>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        string? viewerUserId,
        bool viewerSeesAll,
        CancellationToken ct = default)
    {
        var unique = ids?
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList() ?? [];

        if (unique.Count == 0)
        {
            return Array.Empty<NepRecord>();
        }

        // Fail-closed: consulta personal sin usuario válido nunca se convierte en global.
        if (!viewerSeesAll && string.IsNullOrWhiteSpace(viewerUserId))
        {
            return Array.Empty<NepRecord>();
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.NepRecords
            .AsNoTracking()
            .Where(r => unique.Contains(r.Id));

        if (!viewerSeesAll)
        {
            string? externalUid = null;
            if (Guid.TryParse(viewerUserId, out var viewerGuid))
            {
                externalUid = await db.Users.AsNoTracking()
                    .Where(u => u.Id == viewerGuid)
                    .Select(u => u.ExternalUserId)
                    .FirstOrDefaultAsync(ct);
            }

            if (!string.IsNullOrWhiteSpace(externalUid))
            {
                query = query.Where(r =>
                    r.CreatedByUserId == viewerUserId || r.CreatedByUserId == externalUid);
            }
            else
            {
                query = query.Where(r => r.CreatedByUserId == viewerUserId);
            }
        }

        return await query.ToListAsync(ct);
    }

    public async Task<NepRecord?> FindByClientOperationAsync(
        string userId,
        string clientOperationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(clientOperationId))
        {
            return null;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.NepRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.CreatedByUserId == userId && r.ClientOperationId == clientOperationId, ct);
    }

    public async Task<NepRecord> AddAsync(NepRecord record, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(record.ConcurrencyStamp))
        {
            record.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        }

        if (record.Id == Guid.Empty)
        {
            record.Id = Guid.NewGuid();
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        db.NepRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
            return record;
        }
        catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(record.ClientOperationId)
                                        && !string.IsNullOrWhiteSpace(record.CreatedByUserId))
        {
            // Contexto limpio: el Add fallido deja el tracker en estado inconsistente.
            await using var read = await _factory.CreateDbContextAsync(ct);
            var existing = await read.NepRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r =>
                    r.CreatedByUserId == record.CreatedByUserId
                    && r.ClientOperationId == record.ClientOperationId, ct);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
    }

    public async Task UpdateAsync(NepRecord record, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.NepRecords
            .Include(r => r.HistorialAcciones)
            .FirstOrDefaultAsync(r => r.Id == record.Id, ct)
            ?? throw new InvalidOperationException("Registro no encontrado.");

        if (!string.Equals(entity.ConcurrencyStamp, record.ConcurrencyStamp, StringComparison.Ordinal))
        {
            throw new Application.Records.RecordConcurrencyConflictException(
                "El registro fue modificado por otro usuario.");
        }

        entity.Telar = record.Telar;
        entity.Neps = record.Neps;
        entity.Tela = record.Tela;
        entity.LoteTrama = record.LoteTrama;
        entity.Turno = record.Turno;
        entity.Operario = record.Operario;
        entity.LineaProduccion = record.LineaProduccion;
        entity.Observacion = record.Observacion;
        entity.RevisadoPorSupervisor = record.RevisadoPorSupervisor;
        entity.AccionCorrectiva = record.AccionCorrectiva;
        entity.ResponsableRevision = record.ResponsableRevision;
        entity.FechaRevision = record.FechaRevision;
        entity.UpdatedAt = DateTime.UtcNow;
        entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");

        // Sincronizar historial de acciones si el caller añadió entradas nuevas.
        foreach (var entry in record.HistorialAcciones)
        {
            if (entry.Id == Guid.Empty || entity.HistorialAcciones.All(h => h.Id != entry.Id))
            {
                entity.HistorialAcciones.Add(entry);
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
            record.ConcurrencyStamp = entity.ConcurrencyStamp;
            record.UpdatedAt = entity.UpdatedAt;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new Application.Records.RecordConcurrencyConflictException(
                "El registro fue modificado por otro usuario.");
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.NepRecords.FindAsync([id], ct);
        if (entity is null)
        {
            return;
        }

        db.NepRecords.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.CorrectiveActions.ExecuteDeleteAsync(ct);
        await db.NepRecords.ExecuteDeleteAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.NepRecords.CountAsync(ct);
    }
}
