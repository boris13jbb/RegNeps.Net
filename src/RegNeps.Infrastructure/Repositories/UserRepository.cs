using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly RegNepsDbContext _db;

    public UserRepository(RegNepsDbContext db) => _db = db;

    public Task<AppUser?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        var key = username.Trim().ToLowerInvariant();
        return _db.Users.FirstOrDefaultAsync(u => u.Username == key, ct);
    }

    public Task<AppUser?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<IReadOnlyList<AppUser>> ListAsync(bool includeDeleted = false, CancellationToken ct = default)
    {
        var query = _db.Users.AsNoTracking().AsQueryable();
        if (!includeDeleted)
        {
            query = query.Where(u => u.DeletedAt == null);
        }

        return await query
            .OrderBy(u => u.Username)
            .ToListAsync(ct);
    }

    public async Task<AppUser> AddAsync(AppUser user, CancellationToken ct = default)
    {
        user.Username = user.Username.Trim().ToLowerInvariant();
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task UpdateAsync(AppUser user, CancellationToken ct = default)
    {
        _db.Users.Update(user);
        await _db.SaveChangesAsync(ct);
    }

    public Task<int> CountSuperAdminsAsync(CancellationToken ct = default) =>
        _db.Users.CountAsync(
            u => u.DeletedAt == null && u.IsActive && (u.IsSuperAdmin || u.Role == AppUserRole.SuperAdmin),
            ct);
}
