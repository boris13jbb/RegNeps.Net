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

        // Mts = Neps / TestLengthM → filtrar por neps equivalentes (traducible a SQL).
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

        // Umbrales en SQL (antes del Take). Evita filtrar alerta en memoria sobre un subconjunto truncado.
        if (filters.AlertLevel is not null || filters.SoloPendientes)
        {
            var config = await _db.AlertConfigs.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AlertConfig();
            if (config.AlertasActivas)
            {
                // Equivalente a Math.Round(neps, AwayFromZero) para neps >= 0:
                // Round(x) <= L  ⇔  x < L + 0.5
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

        // Listados: más recientes primero. Exports/Analytics reordenan si lo necesitan.
        return await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Telar)
            .Take(take)
            .ToListAsync(ct);
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
