using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Domain.Services;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Repositories;

public sealed class NepRecordRepository : INepRecordRepository
{
    private readonly RegNepsDbContext _db;

    public NepRecordRepository(RegNepsDbContext db) => _db = db;

    public async Task<IReadOnlyList<NepRecord>> GetRecentAsync(int take = 100, CancellationToken ct = default) =>
        await _db.NepRecords
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<NepRecord>> QueryAsync(
        RecordFilters filters,
        string? viewerUserId,
        bool viewerSeesAll,
        int take = 500,
        CancellationToken ct = default)
    {
        filters ??= new RecordFilters();
        ReportDateRange.EnsureConsolidatedRange(filters);

        var hasDateRange = filters.FromUtc is not null
            || filters.ToExclusiveUtc is not null
            || filters.ToUtc is not null;
        var maxTake = hasDateRange ? 50_000 : 10_000;
        take = Math.Clamp(take, 1, maxTake);

        var query = _db.NepRecords.AsNoTracking().AsQueryable();

        if (!viewerSeesAll && !string.IsNullOrWhiteSpace(viewerUserId))
        {
            string? externalUid = null;
            if (Guid.TryParse(viewerUserId, out var viewerGuid))
            {
                externalUid = await _db.Users.AsNoTracking()
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

        if (!string.IsNullOrWhiteSpace(filters.Telar))
        {
            var telar = filters.Telar.Trim();
            query = query.Where(r => r.Telar == telar);
        }

        if (!string.IsNullOrWhiteSpace(filters.Tela))
        {
            var tela = filters.Tela.Trim();
            query = query.Where(r => r.Tela == tela);
        }

        if (!string.IsNullOrWhiteSpace(filters.LoteTrama))
        {
            var lote = filters.LoteTrama.Trim();
            query = query.Where(r => r.LoteTrama == lote);
        }

        if (!string.IsNullOrWhiteSpace(filters.Turno))
        {
            var turno = filters.Turno.Trim();
            query = query.Where(r => r.Turno == turno);
        }

        if (!string.IsNullOrWhiteSpace(filters.Operario))
        {
            var operario = filters.Operario.Trim();
            query = query.Where(r => r.Operario == operario);
        }

        if (!string.IsNullOrWhiteSpace(filters.LineaProduccion))
        {
            var linea = filters.LineaProduccion.Trim();
            query = query.Where(r => r.LineaProduccion == linea);
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

        if (filters.FromUtc is not null)
        {
            query = query.Where(r => r.CreatedAt >= filters.FromUtc.Value);
        }

        // Preferir fin exclusivo para incluir todo el día "Hasta" sin ambigüedad de ticks.
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

        var needsMemoryFilter =
            filters.AlertLevel is not null ||
            filters.SoloPendientes ||
            filters.MtsMin is not null ||
            filters.MtsMax is not null;

        var dbTake = needsMemoryFilter ? Math.Min(take * 3, maxTake) : take;

        var ordered = hasDateRange
            ? query.OrderBy(r => r.CreatedAt)
            : query.OrderByDescending(r => r.CreatedAt);

        var candidates = await ordered
            .Take(dbTake)
            .ToListAsync(ct);

        if (!needsMemoryFilter)
        {
            return candidates;
        }

        var config = await _db.AlertConfigs.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AlertConfig();

        IEnumerable<NepRecord> filtered = candidates;

        if (filters.AlertLevel is not null)
        {
            filtered = filtered.Where(r => AlertEvaluator.GetLevel(r.Neps, config) == filters.AlertLevel.Value);
        }

        if (filters.SoloPendientes)
        {
            filtered = filtered.Where(r => r.RequiereSeguimiento(config));
        }

        if (filters.MtsMin is not null)
        {
            filtered = filtered.Where(r => r.MtsCalculados >= filters.MtsMin.Value);
        }

        if (filters.MtsMax is not null)
        {
            filtered = filtered.Where(r => r.MtsCalculados <= filters.MtsMax.Value);
        }

        var result = filtered.Take(take).ToList();
        if (hasDateRange)
        {
            result = result
                .OrderBy(r => r.CreatedAt)
                .ThenBy(r => r.Telar, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return result;
    }

    public Task<NepRecord?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.NepRecords
            .Include(r => r.HistorialAcciones)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<NepRecord> AddAsync(NepRecord record, CancellationToken ct = default)
    {
        _db.NepRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    public async Task UpdateAsync(NepRecord record, CancellationToken ct = default)
    {
        record.UpdatedAt = DateTime.UtcNow;
        _db.NepRecords.Update(record);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.NepRecords.FindAsync([id], ct);
        if (entity is null)
        {
            return;
        }

        _db.NepRecords.Remove(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await _db.CorrectiveActions.ExecuteDeleteAsync(ct);
        await _db.NepRecords.ExecuteDeleteAsync(ct);
    }

    public Task<int> CountAsync(CancellationToken ct = default) =>
        _db.NepRecords.CountAsync(ct);
}
