using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Repositories;

public sealed class RoleRepository : IRoleRepository
{
    private readonly RegNepsDbContext _db;

    public RoleRepository(RegNepsDbContext db) => _db = db;

    public async Task<IReadOnlyList<AppRole>> ListAsync(bool includeInactive = true, CancellationToken ct = default)
    {
        var query = _db.Roles.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query.OrderBy(x => x.IsSystem ? 0 : 1).ThenBy(x => x.Name).ToListAsync(ct);
    }

    public Task<AppRole?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Roles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<AppRole?> GetByCodeAsync(string code, CancellationToken ct = default) =>
        _db.Roles.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code, ct);

    public Task<int> CountUsersForRoleAsync(string roleCode, CancellationToken ct = default) =>
        _db.Users.CountAsync(
            u => u.DeletedAt == null &&
                 (u.RoleCode == roleCode ||
                  ((u.RoleCode == null || u.RoleCode == "") &&
                   SystemRoleCodes.FromEnum(u.Role) == roleCode)),
            ct);

    public async Task<AppRole> AddAsync(AppRole role, CancellationToken ct = default)
    {
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(ct);
        return role;
    }

    public async Task UpdateAsync(AppRole role, CancellationToken ct = default)
    {
        var tracked = await _db.Roles.FirstOrDefaultAsync(x => x.Id == role.Id, ct)
            ?? throw new InvalidOperationException("Rol no encontrado.");
        tracked.Name = role.Name;
        tracked.IsActive = role.IsActive;
        tracked.SeesAllRecords = role.SeesAllRecords;
        tracked.UpdatedAt = role.UpdatedAt;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var role = await _db.Roles.FindAsync([id], ct);
        if (role is null)
        {
            return;
        }

        _db.Roles.Remove(role);
        await _db.SaveChangesAsync(ct);
    }

    public async Task InitializeBaseRolesAsync(CancellationToken ct = default) =>
        await RoleBootstrap.InitializeBaseRolesAsync(_db, ct);

    public async Task DeleteWithPermissionsAsync(Guid roleId, CancellationToken ct = default)
    {
        var perms = await _db.RolePermissions.Where(x => x.RoleId == roleId).ToListAsync(ct);
        _db.RolePermissions.RemoveRange(perms);
        var role = await _db.Roles.FindAsync([roleId], ct);
        if (role is not null)
        {
            _db.Roles.Remove(role);
        }

        await _db.SaveChangesAsync(ct);
    }
}
