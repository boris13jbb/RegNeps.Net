using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Repositories;

public sealed class LoteTramaRepository : ILoteTramaRepository
{
    private readonly RegNepsDbContext _db;

    public LoteTramaRepository(RegNepsDbContext db) => _db = db;

    public async Task<IReadOnlyList<LoteTramaItem>> GetActiveAsync(CancellationToken ct = default) =>
        await _db.LoteTramaItems
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Code)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<LoteTramaItem>> GetAllAsync(CancellationToken ct = default) =>
        await _db.LoteTramaItems
            .AsNoTracking()
            .OrderBy(x => x.Code)
            .ToListAsync(ct);

    public async Task<LoteTramaItem?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return await _db.LoteTramaItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Code == normalized, ct);
    }

    public async Task<LoteTramaItem> AddAsync(LoteTramaItem item, CancellationToken ct = default)
    {
        item.Code = item.Code.Trim().ToUpperInvariant();
        var existing = await _db.LoteTramaItems
            .FirstOrDefaultAsync(x => x.Code == item.Code, ct);
        if (existing is not null)
        {
            existing.IsActive = true;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        _db.LoteTramaItems.Add(item);
        await _db.SaveChangesAsync(ct);
        return item;
    }

    public async Task UpdateAsync(LoteTramaItem item, CancellationToken ct = default)
    {
        item.Code = item.Code.Trim().ToUpperInvariant();
        var entity = await _db.LoteTramaItems.FindAsync([item.Id], ct)
            ?? throw new InvalidOperationException("Lote no encontrado.");

        var duplicate = await _db.LoteTramaItems
            .AsNoTracking()
            .AnyAsync(x => x.Code == item.Code && x.Id != item.Id, ct);
        if (duplicate)
        {
            throw new InvalidOperationException($"Ya existe el lote {item.Code}.");
        }

        entity.Code = item.Code;
        entity.IsActive = item.IsActive;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.LoteTramaItems.FindAsync([id], ct);
        if (entity is null)
        {
            return;
        }

        _db.LoteTramaItems.Remove(entity);
        await _db.SaveChangesAsync(ct);
    }
}
