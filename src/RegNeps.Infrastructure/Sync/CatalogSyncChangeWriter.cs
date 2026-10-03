using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Sync;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Sync;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Sync;

/// <summary>
/// Emite SyncChangeLog de catálogo en el mismo DbContext que la mutación (server → Pull).
/// No hay Push de catálogos.
/// </summary>
internal static class CatalogSyncChangeWriter
{
    public static void AppendFabricUpsert(RegNepsDbContext db, Fabric fabric)
    {
        var payload = SyncCatalogPayloadMapper.FromFabric(fabric, fabric.IsActive);
        db.SyncChangeLogs.Add(SyncCatalogPayloadMapper.CreateUpsertedEntry(payload));
    }

    public static void AppendFabricDeleted(RegNepsDbContext db, Fabric fabric)
    {
        var payload = SyncCatalogPayloadMapper.FromFabric(fabric, isActive: false);
        db.SyncChangeLogs.Add(SyncCatalogPayloadMapper.CreateDeletedEntry(payload));
    }

    public static void AppendLoteUpsert(RegNepsDbContext db, LoteTramaItem lote)
    {
        var payload = SyncCatalogPayloadMapper.FromLote(lote, lote.IsActive);
        db.SyncChangeLogs.Add(SyncCatalogPayloadMapper.CreateUpsertedEntry(payload));
    }

    public static void AppendLoteDeleted(RegNepsDbContext db, LoteTramaItem lote)
    {
        var payload = SyncCatalogPayloadMapper.FromLote(lote, isActive: false);
        db.SyncChangeLogs.Add(SyncCatalogPayloadMapper.CreateDeletedEntry(payload));
    }

    /// <summary>
    /// Baseline idempotente: un CatalogUpserted por Fabric/Lote sin log CatalogItem previo.
    /// </summary>
    public static async Task EnsureBaselineAsync(RegNepsDbContext db, CancellationToken ct = default)
    {
        var existingIds = await db.SyncChangeLogs.AsNoTracking()
            .Where(c => c.EntityType == SyncConstants.EntityCatalogItem)
            .Select(c => c.EntityId)
            .Distinct()
            .ToListAsync(ct);
        var known = existingIds.ToHashSet();

        var fabrics = await db.Fabrics.AsNoTracking().ToListAsync(ct);
        foreach (var f in fabrics)
        {
            if (known.Contains(f.Id))
            {
                continue;
            }

            db.SyncChangeLogs.Add(SyncCatalogPayloadMapper.CreateUpsertedEntry(
                SyncCatalogPayloadMapper.FromFabric(f, f.IsActive)));
            known.Add(f.Id);
        }

        var lotes = await db.LoteTramaItems.AsNoTracking().ToListAsync(ct);
        foreach (var l in lotes)
        {
            if (known.Contains(l.Id))
            {
                continue;
            }

            db.SyncChangeLogs.Add(SyncCatalogPayloadMapper.CreateUpsertedEntry(
                SyncCatalogPayloadMapper.FromLote(l, l.IsActive)));
            known.Add(l.Id);
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
