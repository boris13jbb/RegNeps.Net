using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Repositories;

public sealed class RolePermissionRepository : IRolePermissionRepository
{
    private readonly RegNepsDbContext _db;

    public RolePermissionRepository(RegNepsDbContext db) => _db = db;

    public async Task<IReadOnlyList<RolePermission>> ListAsync(CancellationToken ct = default) =>
        await _db.RolePermissions.AsNoTracking().ToListAsync(ct);

    public async Task SaveChangesAsync(
        IReadOnlyList<RolePermission> upserts,
        IReadOnlyList<RolePermissionAudit> audits,
        CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        foreach (var row in upserts)
        {
            var existing = await _db.RolePermissions.FindAsync(
                [row.Role, row.Permission],
                ct);

            if (existing is null)
            {
                _db.RolePermissions.Add(row);
                continue;
            }

            existing.IsEnabled = row.IsEnabled;
            existing.UpdatedAt = row.UpdatedAt;
            existing.UpdatedByUserId = row.UpdatedByUserId;
        }

        if (audits.Count > 0)
        {
            _db.RolePermissionAudits.AddRange(audits);
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
