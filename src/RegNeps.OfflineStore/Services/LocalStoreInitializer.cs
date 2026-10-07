using Microsoft.EntityFrameworkCore;

namespace RegNeps.OfflineStore.Services;

/// <summary>
/// Aplica migraciones EF del store local. Seguro con Outbox pendiente
/// (solo cambios aditivos en migraciones futuras).
/// </summary>
public sealed class LocalStoreInitializer
{
    private readonly LocalSyncDbContext _db;

    public LocalStoreInitializer(LocalSyncDbContext db) => _db = db;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Migrate aplica el historial; si no hay migraciones embebidas en diseño temprano,
        // EnsureCreated+__EFMigrationsHistory se evita usando solo Migrate.
        await _db.Database.MigrateAsync(ct);
    }

    public async Task EnsureSyncStateAsync(string deviceId, CancellationToken ct = default)
    {
        var state = await _db.SyncStates.FindAsync([1], ct);
        if (state is null)
        {
            _db.SyncStates.Add(new Entities.SyncState
            {
                Id = 1,
                DeviceId = deviceId,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(state.DeviceId))
        {
            state.DeviceId = deviceId;
            state.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }
}
