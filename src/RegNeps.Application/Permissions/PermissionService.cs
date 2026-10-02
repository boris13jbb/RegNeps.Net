using System.Collections.Immutable;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Constants;
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
    private readonly IRoleRepository _roles;
    private readonly IPermissionMatrix _matrix;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public PermissionService(
        IRolePermissionRepository store,
        IRoleRepository roles,
        IPermissionMatrix matrix)
    {
        _store = store;
        _roles = roles;
        _matrix = matrix;
    }

    public bool HasPermission(AppUserRole role, bool isSuperAdmin, bool isActive, AppPermission permission) =>
        _matrix.Has(role, isSuperAdmin, isActive, permission);

    public bool HasPermissionByRoleCode(string? roleCode, bool isSuperAdmin, bool isActive, AppPermission permission) =>
        _matrix.HasByRoleCode(roleCode, isSuperAdmin, isActive, permission);

    public bool CanAdministerRoles(AppUserRole role, bool isSuperAdmin, bool isActive) =>
        isSuperAdmin && isActive;

    public bool SeesAllRecords(string? roleCode, bool isSuperAdmin) =>
        _matrix.SeesAllRecords(roleCode, isSuperAdmin);

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

    public async Task<IReadOnlySet<AppPermission>> GetPermissionsForRoleIdAsync(Guid roleId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var entry = _matrix.FindById(roleId);
        return entry?.Permissions ?? ImmutableHashSet<AppPermission>.Empty;
    }

    public async Task<IReadOnlyList<AppRole>> GetManageableRolesAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _matrix.GetAllRoles()
            .Select(x => _matrix.ToAppRole(x))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<AppUserRole, IReadOnlySet<AppPermission>>> GetRolePermissionsAsync(
        CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return Enum.GetValues<AppUserRole>()
            .ToDictionary(role => role, role => _matrix.GetEnabled(role));
    }

    public Task UpdateRolePermissionAsync(
        AppUserRole role,
        AppPermission permission,
        bool isEnabled,
        CallerContext actor,
        CancellationToken ct = default) =>
        UpdateRolePermissionsAsync(
            role,
            new Dictionary<AppPermission, bool> { [permission] = isEnabled },
            actor,
            ct);

    public async Task<int> UpdateRolePermissionsAsync(
        AppUserRole role,
        IReadOnlyDictionary<AppPermission, bool> desired,
        CallerContext actor,
        CancellationToken ct = default)
    {
        var roleRow = await _roles.GetByCodeAsync(SystemRoleCodes.FromEnum(role), ct)
            ?? throw new InvalidOperationException("Rol de sistema no encontrado. Ejecute «Inicializar roles base».");
        return await UpdateRolePermissionsForRoleIdAsync(roleRow.Id, desired, actor, ct);
    }

    public async Task<int> UpdateRolePermissionsForRoleIdAsync(
        Guid roleId,
        IReadOnlyDictionary<AppPermission, bool> desired,
        CallerContext actor,
        CancellationToken ct = default)
    {
        EnsureCanMutateMatrix(actor);

        var roleRow = await _roles.GetByIdAsync(roleId, ct)
            ?? throw new ArgumentException("Rol no encontrado.", nameof(roleId));

        if (SystemRoleCodes.IsSuperAdminCode(roleRow.Code))
        {
            throw new InvalidOperationException(
                "El Super Administrador tiene acceso total al sistema y sus permisos no pueden modificarse.");
        }

        if (!roleRow.IsActive)
        {
            throw new InvalidOperationException("No se pueden editar permisos de un rol inactivo.");
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
        var byKey = current.ToDictionary(x => (x.RoleId, x.Permission));
        var now = DateTime.UtcNow;
        var upserts = new List<RolePermission>();
        var audits = new List<RolePermissionAudit>();

        foreach (var definition in PermissionCatalog.All)
        {
            var permission = definition.Permission;
            var currentEnabled = byKey.TryGetValue((roleId, permission), out var row) && row.IsEnabled;
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
                    RoleId = roleId,
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
                    RoleId = roleId,
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

    public Task RefreshMatrixAsync(CancellationToken ct = default) => ReloadAsync(ct);

    private async Task ReloadAsync(CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct);
        try
        {
            var roleRows = await _roles.ListAsync(includeInactive: true, ct);
            var permRows = await _store.ListAsync(ct);
            var entries = new List<RoleMatrixEntry>();

            foreach (var role in roleRows)
            {
                var enabled = permRows
                    .Where(x => x.RoleId == role.Id && x.IsEnabled && x.Permission != AppPermission.ManageRoles)
                    .Select(x => x.Permission)
                    .Where(PermissionCatalog.IsKnown)
                    .ToImmutableHashSet();

                entries.Add(new RoleMatrixEntry(
                    role.Id,
                    role.Code,
                    role.Name,
                    role.IsActive,
                    role.IsSystem,
                    role.SeesAllRecords,
                    enabled));
            }

            _matrix.Replace(entries);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void EnsureCanMutateMatrix(CallerContext actor)
    {
        if (!CanAdministerRoles(actor.Role, actor.IsSuperAdmin, actor.IsActive) || actor.UserId is null)
        {
            throw new UnauthorizedAccessException("No tiene permiso para administrar roles y permisos.");
        }
    }
}
