using Microsoft.EntityFrameworkCore;
using RegNeps.OfflineStore.Abstractions;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;

namespace RegNeps.OfflineStore.Services;

public sealed class OfflineOutboxQuery : IOfflineOutboxQuery
{
    private readonly LocalSyncDbContext _db;

    public OfflineOutboxQuery(LocalSyncDbContext db) => _db = db;

    public async Task<IReadOnlyList<PendingOperation>> ListByStatusAsync(
        PendingOperationStatus status,
        int take = 100,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        return await _db.PendingOperations.AsNoTracking()
            .Where(o => o.Status == status)
            .OrderBy(o => o.CreatedAtUtc)
            .Take(take)
            .ToListAsync(ct);
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default) =>
        _db.PendingOperations.CountAsync(o => o.Status == PendingOperationStatus.Pending, ct);
}
