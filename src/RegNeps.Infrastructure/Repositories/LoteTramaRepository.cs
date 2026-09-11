using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Repositories;

public sealed class LoteTramaRepository : ILoteTramaRepository
{
    private readonly IDbContextFactory<RegNepsDbContext> _factory;

    public LoteTramaRepository(IDbContextFactory<RegNepsDbContext> factory) =>
        _factory = factory;

    public async Task<IReadOnlyList<LoteTramaItem>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.LoteTramaItems
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Code)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LoteTramaItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.LoteTramaItems
            .AsNoTracking()
            .OrderBy(x => x.Code)
            .ToListAsync(ct);
    }

    public async Task<LoteTramaItem?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.LoteTramaItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Code == normalized, ct);
    }

    public Task<LoteTramaItem> AddAsync(LoteTramaItem item, CancellationToken ct = default) =>
        EnsureActiveByCodeAsync(item.Code, ct);

    public async Task<LoteTramaItem> EnsureActiveByCodeAsync(string code, CancellationToken ct = default)
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("El código de lote es obligatorio.", nameof(code));
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.LoteTramaItems
            .FirstOrDefaultAsync(x => x.Code == normalized, ct);
        if (existing is not null)
        {
            if (!existing.IsActive)
            {
                existing.IsActive = true;
                await db.SaveChangesAsync(ct);
            }

            return existing;
        }

        var item = new LoteTramaItem { Code = normalized, IsActive = true };
        db.LoteTramaItems.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
            return item;
        }
        catch (DbUpdateException)
        {
            await using var read = await _factory.CreateDbContextAsync(ct);
            var raced = await read.LoteTramaItems
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Code == normalized, ct);
            if (raced is not null)
            {
                return raced;
            }

            throw;
        }
    }

    public async Task UpdateAsync(LoteTramaItem item, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        item.Code = item.Code.Trim().ToUpperInvariant();
        var entity = await db.LoteTramaItems.FindAsync([item.Id], ct)
            ?? throw new InvalidOperationException("Lote no encontrado.");

        var duplicate = await db.LoteTramaItems
            .AsNoTracking()
            .AnyAsync(x => x.Code == item.Code && x.Id != item.Id, ct);
        if (duplicate)
        {
            throw new InvalidOperationException($"Ya existe el lote {item.Code}.");
        }

        entity.Code = item.Code;
        entity.IsActive = item.IsActive;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.LoteTramaItems.FindAsync([id], ct);
        if (entity is null)
        {
            return;
        }

        db.LoteTramaItems.Remove(entity);
        await db.SaveChangesAsync(ct);
    }
}
