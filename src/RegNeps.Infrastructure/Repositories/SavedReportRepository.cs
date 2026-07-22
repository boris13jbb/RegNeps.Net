using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Repositories;

public sealed class SavedReportRepository : ISavedReportRepository
{
    private readonly RegNepsDbContext _db;

    public SavedReportRepository(RegNepsDbContext db) => _db = db;

    public async Task<IReadOnlyList<SavedReport>> ListAsync(CancellationToken ct = default) =>
        await _db.SavedReports
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

    public Task<SavedReport?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.SavedReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<SavedReport> AddAsync(SavedReport report, CancellationToken ct = default)
    {
        _db.SavedReports.Add(report);
        await _db.SaveChangesAsync(ct);
        return report;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.SavedReports.FindAsync([id], ct);
        if (entity is null)
        {
            return;
        }

        _db.SavedReports.Remove(entity);
        await _db.SaveChangesAsync(ct);
    }
}
