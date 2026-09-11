using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;

namespace RegNeps.Application.Permissions;

/// <summary>
/// Fuente de verdad de permisos efectivos. SuperAdmin siempre tiene acceso total,
/// aunque la tabla diga lo contrario. La caché se reemplaza al guardar.
/// </summary>
public sealed class PermissionService : IPermissionService
{
    private readonly IRolePermissionRepository _store;
    private readonly IPermissionMatrix _matrix;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public PermissionService(IRolePermissionRepository store, IPermissionMatrix matrix)
    {
        _store = store;
        _matrix = matrix;
    }

    public bool HasPermission(AppUserRole role, bool isSuperAdmin, bool isActive, AppPermission permission) =>
        _matrix.Has(role, isSuperAdmin, isActive, permission);

    public bool CanAdministerRoles(AppUserRole role, bool isSuperAdmin, bool isActive) =>
        HasPermission(role, isSuperAdmin, isActive, AppPermission.ManageRoles);

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_matrix.IsLoaded)
        {
            return;
        }

        await ReloadAsync(ct);
    }

    public async Task<bool> HasPermissionAsync(
        AppUserRole role,
        bool isSuperAdmin,
        bool isActive,
        AppPermission permission,
        CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return HasPermission(role, isSuperAdmin, isActive, permission);
    }

    public async Task<IReadOnlySet<AppPermission>> GetPermissionsForRoleAsync(
        AppUserRole role,
        CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _matrix.GetEnabled(role);
    }

    public async Task<IReadOnlyDictionary<AppUserRole, IReadOnlySet<AppPermission>>> GetRolePermissionsAsync(
        CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return ConfigurableRoles()
            .Concat([AppUserRole.SuperAdmin])
            .ToDictionary(role => role, role => _matrix.GetEnabled(role));
    }

    public async Task UpdateRolePermissionAsync(
        AppUserRole role,
        AppPermission permission,
        bool isEnabled,
        CallerContext actor,
        CancellationToken ct = default)
    {
        await UpdateRolePermissionsAsync(
            role,
            new Dictionary<AppPermission, bool> { [permission] = isEnabled },
            actor,
            ct);
    }

    public async Task<int> UpdateRolePermissionsAsync(
        AppUserRole role,
        IReadOnlyDictionary<AppPermission, bool> desired,
        CallerContext actor,
        CancellationToken ct = default)
    {
        EnsureCanMutateMatrix(actor);

        if (role == AppUserRole.SuperAdmin)
        {
            throw new InvalidOperationException(
                "El Super Administrador tiene acceso total al sistema y sus permisos no pueden modificarse.");
        }

        if (!ConfigurableRoles().Contains(role))
        {
            throw new ArgumentException("El rol indicado no se puede configurar.", nameof(role));
        }

        foreach (var pair in desired)
        {
            if (!PermissionCatalog.IsKnown(pair.Key))
            {
                throw new ArgumentException($"El permiso {pair.Key} no existe en el catálogo.");
            }

            if (pair.Key == AppPermission.ManageRoles && pair.Value)
            {
                throw new InvalidOperationException(
                    "Administrar roles y permisos es un privilegio exclusivo del Super Administrador.");
            }
        }

        var current = await _store.ListAsync(ct);
        var byKey = current.ToDictionary(x => (x.Role, x.Permission));
        var now = DateTime.UtcNow;
        var upserts = new List<RolePermission>();
        var audits = new List<RolePermissionAudit>();

        foreach (var definition in PermissionCatalog.All)
        {
            var permission = definition.Permission;
            var currentEnabled = byKey.TryGetValue((role, permission), out var row) && row.IsEnabled;
            bool next;
            if (permission == AppPermission.ManageRoles)
            {
                next = false;
            }
            else if (desired.TryGetValue(permission, out var enabled))
            {
                next = enabled;
            }
            else
            {
                next = currentEnabled;
            }

            if (row is null)
            {
                if (!next)
                {
                    continue;
                }

                row = new RolePermission
                {
                    Role = role,
                    Permission = permission,
                    IsEnabled = false,
                    CreatedAt = now
                };
            }
            else if (row.IsEnabled == next)
            {
                continue;
            }

            var previous = row.IsEnabled;
            if (previous != next)
            {
                audits.Add(new RolePermissionAudit
                {
                    Role = role,
                    Permission = permission,
                    PreviousValue = previous,
                    NewValue = next,
                    ModifiedByUserId = actor.UserId,
                    ChangedAt = now
                });
            }

            row.IsEnabled = next;
            row.UpdatedAt = now;
            row.UpdatedByUserId = actor.UserId;
            upserts.Add(row);
        }

        if (upserts.Count > 0)
        {
            await _store.SaveChangesAsync(upserts, audits, ct);
            await ReloadAsync(ct);
        }

        return audits.Count;
    }

    private void EnsureCanMutateMatrix(CallerContext actor)
    {
        if (!CanAdministerRoles(actor.Role, actor.IsSuperAdmin, actor.IsActive) || actor.UserId is null)
        {
            throw new UnauthorizedAccessException("No tiene permiso para administrar roles y permisos.");
        }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct);
        try
        {
            var rows = await _store.ListAsync(ct);
            var matrix = new Dictionary<AppUserRole, IReadOnlySet<AppPermission>>();
            foreach (var role in ConfigurableRoles())
            {
                matrix[role] = rows
                    .Where(x => x.Role == role && x.IsEnabled && x.Permission != AppPermission.ManageRoles)
                    .Select(x => x.Permission)
                    .Where(PermissionCatalog.IsKnown)
                    .ToHashSet();
            }

            matrix[AppUserRole.SuperAdmin] = PermissionCatalog.All.Select(x => x.Permission).ToHashSet();
            _matrix.Replace(matrix);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    internal static IReadOnlyList<AppUserRole> ConfigurableRoles() =>
    [
        AppUserRole.Operario,
        AppUserRole.Supervisor,
        AppUserRole.Admin,
        AppUserRole.Gerencia
    ];
}
