using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Repositories;

public sealed class AlertConfigRepository : IAlertConfigRepository
{
    private readonly RegNepsDbContext _db;

    public AlertConfigRepository(RegNepsDbContext db) => _db = db;

    public async Task<AlertConfig> GetAsync(CancellationToken ct = default)
    {
        var config = await _db.AlertConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
        return config ?? new AlertConfig();
    }

    public async Task SaveAsync(AlertConfig config, CancellationToken ct = default)
    {
        config.Id = 1;
        var existing = await _db.AlertConfigs.FindAsync([1], ct);
        if (existing is null)
        {
            _db.AlertConfigs.Add(config);
        }
        else
        {
            existing.LimiteNormalMax = config.LimiteNormalMax;
            existing.LimiteAdvertenciaMax = config.LimiteAdvertenciaMax;
            existing.CantidadReincidenciasCriticas = config.CantidadReincidenciasCriticas;
            existing.DiasParaReincidencia = config.DiasParaReincidencia;
            existing.AlertasActivas = config.AlertasActivas;
        }

        await _db.SaveChangesAsync(ct);
    }
}
