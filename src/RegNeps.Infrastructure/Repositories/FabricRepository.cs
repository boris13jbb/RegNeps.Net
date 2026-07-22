using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Repositories;

public sealed class FabricRepository : IFabricRepository
{
    private readonly RegNepsDbContext _db;

    public FabricRepository(RegNepsDbContext db) => _db = db;

    public async Task<IReadOnlyList<Fabric>> GetActiveAsync(CancellationToken ct = default) =>
        await _db.Fabrics
            .AsNoTracking()
            .Where(f => f.IsActive)
            .OrderBy(f => f.Name)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Fabric>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Fabrics
            .AsNoTracking()
            .OrderBy(f => f.Name)
            .ToListAsync(ct);

    public async Task<Fabric> AddAsync(Fabric fabric, CancellationToken ct = default)
    {
        _db.Fabrics.Add(fabric);
        await _db.SaveChangesAsync(ct);
        return fabric;
    }

    public async Task UpdateAsync(Fabric fabric, CancellationToken ct = default)
    {
        _db.Fabrics.Update(fabric);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.Fabrics.FindAsync([id], ct);
        if (entity is null)
        {
            return;
        }

        _db.Fabrics.Remove(entity);
        await _db.SaveChangesAsync(ct);
    }
}
