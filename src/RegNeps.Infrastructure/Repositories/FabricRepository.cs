using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Repositories;

public sealed class FabricRepository : IFabricRepository
{
    private readonly IDbContextFactory<RegNepsDbContext> _factory;

    public FabricRepository(IDbContextFactory<RegNepsDbContext> factory) =>
        _factory = factory;

    public async Task<IReadOnlyList<Fabric>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Fabrics
            .AsNoTracking()
            .Where(f => f.IsActive)
            .OrderBy(f => f.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Fabric>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Fabrics
            .AsNoTracking()
            .OrderBy(f => f.Name)
            .ToListAsync(ct);
    }

    public async Task<Fabric?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var key = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Fabrics
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Name.ToLower() == key.ToLower(), ct);
    }

    public async Task<Fabric> AddAsync(Fabric fabric, CancellationToken ct = default)
    {
        fabric.Name = (fabric.Name ?? "").Trim();
        return await EnsureActiveByNameAsync(fabric.Name, ct);
    }

    public async Task<Fabric> EnsureActiveByNameAsync(string name, CancellationToken ct = default)
    {
        var key = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("El nombre de la tela es obligatorio.", nameof(name));
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Fabrics
            .FirstOrDefaultAsync(f => f.Name.ToLower() == key.ToLower(), ct);
        if (existing is not null)
        {
            if (!existing.IsActive)
            {
                existing.IsActive = true;
                await db.SaveChangesAsync(ct);
            }

            return existing;
        }

        var fabric = new Fabric
        {
            Name = key,
            IsActive = true
        };
        db.Fabrics.Add(fabric);
        try
        {
            await db.SaveChangesAsync(ct);
            return fabric;
        }
        catch (DbUpdateException)
        {
            // Carrera: otro contexto insertó la misma tela (índice único).
            await using var read = await _factory.CreateDbContextAsync(ct);
            var raced = await read.Fabrics
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Name.ToLower() == key.ToLower(), ct);
            if (raced is not null)
            {
                return raced;
            }

            throw;
        }
    }

    public async Task UpdateAsync(Fabric fabric, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.Fabrics.FindAsync([fabric.Id], ct)
            ?? throw new InvalidOperationException("Tela no encontrada.");

        var name = (fabric.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("El nombre es obligatorio.");
        }

        entity.Name = name;
        entity.Code = string.IsNullOrWhiteSpace(fabric.Code) ? null : fabric.Code.Trim();
        entity.IsActive = fabric.IsActive;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.Fabrics.FindAsync([id], ct);
        if (entity is null)
        {
            return;
        }

        db.Fabrics.Remove(entity);
        await db.SaveChangesAsync(ct);
    }
}
