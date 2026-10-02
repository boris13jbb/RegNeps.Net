using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;

namespace RegNeps.Infrastructure.Persistence;

/// <summary>Sembrado idempotente de roles de sistema y permisos faltantes (no pisa ediciones).</summary>
public static class RoleBootstrap
{
    /// <summary>Garantiza filas de roles de sistema y sincroniza RoleCode en usuarios legacy.</summary>
    public static async Task EnsureSystemRolesAsync(RegNepsDbContext db, CancellationToken ct = default)
    {
        var existing = await db.Roles.AsNoTracking().Select(x => x.Code).ToListAsync(ct);
        var known = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        foreach (var def in SystemRoleCodes.Definitions)
        {
            if (known.Contains(def.Code))
            {
                continue;
            }

            db.Roles.Add(new AppRole
            {
                Code = def.Code,
                Name = def.Name,
                IsSystem = def.IsSystem,
                IsActive = true,
                SeesAllRecords = def.SeesAllRecords,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        await SyncUserRoleCodesAsync(db, ct);
    }

    /// <summary>Inserta solo asignaciones que aún no existen. No modifica IsEnabled existente.</summary>
    public static async Task EnsureDefaultRolePermissionsAsync(RegNepsDbContext db, CancellationToken ct = default)
    {
        var roles = await db.Roles.AsNoTracking().ToListAsync(ct);
        var roleByCode = roles.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

        var existing = await db.RolePermissions
            .Select(x => new { x.RoleId, x.Permission })
            .ToListAsync(ct);
        var known = existing.Select(x => (x.RoleId, x.Permission)).ToHashSet();
        var now = DateTime.UtcNow;

        foreach (var def in SystemRoleCodes.Definitions)
        {
            if (!roleByCode.TryGetValue(def.Code, out var roleRow))
            {
                continue;
            }

            if (!SystemRoleCodes.TryParseEnum(def.Code, out var enumRole))
            {
                continue;
            }

            foreach (var permissionDef in PermissionCatalog.All)
            {
                if (known.Contains((roleRow.Id, permissionDef.Permission)))
                {
                    continue;
                }

                var enabled = enumRole == AppUserRole.SuperAdmin
                    ? true
                    : RolePermissions.Has(enumRole, permissionDef.Permission);

                db.RolePermissions.Add(new RolePermission
                {
                    RoleId = roleRow.Id,
                    Permission = permissionDef.Permission,
                    IsEnabled = enabled,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public static async Task InitializeBaseRolesAsync(RegNepsDbContext db, CancellationToken ct = default)
    {
        await EnsureSystemRolesAsync(db, ct);
        await EnsureDefaultRolePermissionsAsync(db, ct);
    }

    private static async Task SyncUserRoleCodesAsync(RegNepsDbContext db, CancellationToken ct)
    {
        var users = await db.Users
            .Where(u => u.RoleCode == null || u.RoleCode == "")
            .ToListAsync(ct);

        if (users.Count == 0)
        {
            return;
        }

        foreach (var user in users)
        {
            user.RoleCode = SystemRoleCodes.FromEnum(user.Role);
        }

        await db.SaveChangesAsync(ct);
    }
}
