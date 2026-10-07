using Microsoft.EntityFrameworkCore;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;

namespace RegNeps.OfflineStore.Services;

/// <summary>
/// Lectura de catálogos locales (FASE 2D.9). Solo server→client vía Pull; sin mutaciones.
/// </summary>
public sealed class OfflineCatalogService
{
    private readonly LocalSyncDbContext _db;

    public OfflineCatalogService(LocalSyncDbContext db) => _db = db;

    public Task<int> CountAsync(CancellationToken ct = default) =>
        _db.LocalCatalogItems.AsNoTracking().CountAsync(ct);

    public Task<int> CountActiveAsync(LocalCatalogKind kind, CancellationToken ct = default) =>
        _db.LocalCatalogItems.AsNoTracking()
            .CountAsync(c => c.Kind == kind && c.IsActive, ct);

    public async Task<IReadOnlyList<LocalCatalogItem>> ListActiveAsync(
        LocalCatalogKind kind,
        CancellationToken ct = default)
    {
        return await _db.LocalCatalogItems.AsNoTracking()
            .Where(c => c.Kind == kind && c.IsActive)
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Code)
            .ToListAsync(ct);
    }

    public async Task<OfflineCatalogAvailability> GetAvailabilityAsync(CancellationToken ct = default)
    {
        var fabrics = await CountActiveAsync(LocalCatalogKind.Fabric, ct);
        var lotes = await CountActiveAsync(LocalCatalogKind.Lote, ct);
        var total = await CountAsync(ct);
        return new OfflineCatalogAvailability
        {
            TotalItems = total,
            ActiveFabrics = fabrics,
            ActiveLotes = lotes,
            HasAnyCatalog = total > 0,
            HasActiveFabrics = fabrics > 0,
            HasActiveLotes = lotes > 0
        };
    }
}

public sealed class OfflineCatalogAvailability
{
    public int TotalItems { get; init; }
    public int ActiveFabrics { get; init; }
    public int ActiveLotes { get; init; }
    public bool HasAnyCatalog { get; init; }
    public bool HasActiveFabrics { get; init; }
    public bool HasActiveLotes { get; init; }

    public string StatusMessage =>
        !HasAnyCatalog
            ? "Catálogos no descargados. Use texto libre o sincronice online primero."
            : $"Catálogos locales: {ActiveFabrics} telas activas · {ActiveLotes} lotes activos"
              + (ActiveFabrics == 0 && ActiveLotes == 0
                  ? " (todos inactivos — texto libre permitido)."
                  : ".");
}
