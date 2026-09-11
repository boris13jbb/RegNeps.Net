using System.Collections.Immutable;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;

namespace RegNeps.Application.Permissions;

/// <summary>
/// Instantánea en memoria de la matriz persistida. Se invalida al guardar para no servir permisos viejos.
/// </summary>
public interface IPermissionMatrix
{
    bool IsLoaded { get; }
    long Version { get; }
    event Action? Changed;

    bool Has(AppUserRole role, bool isSuperAdmin, bool isActive, AppPermission permission);
    IReadOnlySet<AppPermission> GetEnabled(AppUserRole role);
    void Replace(IReadOnlyDictionary<AppUserRole, IReadOnlySet<AppPermission>> matrix);
}

public sealed class PermissionMatrix : IPermissionMatrix
{
    private ImmutableDictionary<AppUserRole, ImmutableHashSet<AppPermission>> _matrix =
        ImmutableDictionary<AppUserRole, ImmutableHashSet<AppPermission>>.Empty;

    private int _loaded;

    public bool IsLoaded => Volatile.Read(ref _loaded) == 1;
    public long Version { get; private set; }
    public event Action? Changed;

    public bool Has(AppUserRole role, bool isSuperAdmin, bool isActive, AppPermission permission)
    {
        if (!isActive)
        {
            return false;
        }

        var effective = isSuperAdmin ? AppUserRole.SuperAdmin : role;
        if (effective == AppUserRole.SuperAdmin)
        {
            return PermissionCatalog.IsKnown(permission);
        }

        if (permission == AppPermission.ManageRoles)
        {
            return false;
        }

        if (!IsLoaded)
        {
            return RolePermissions.Has(effective, permission);
        }

        return _matrix.TryGetValue(effective, out var set) && set.Contains(permission);
    }

    public IReadOnlySet<AppPermission> GetEnabled(AppUserRole role)
    {
        if (role == AppUserRole.SuperAdmin)
        {
            return PermissionCatalog.All.Select(x => x.Permission).ToHashSet();
        }

        if (!IsLoaded)
        {
            return RolePermissions.ForRole(role)
                .Where(p => p != AppPermission.ManageRoles)
                .ToHashSet();
        }

        return _matrix.TryGetValue(role, out var set)
            ? set
            : (IReadOnlySet<AppPermission>)ImmutableHashSet<AppPermission>.Empty;
    }

    public void Replace(IReadOnlyDictionary<AppUserRole, IReadOnlySet<AppPermission>> matrix)
    {
        var next = matrix.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutableHashSet());

        _matrix = next;
        Volatile.Write(ref _loaded, 1);
        Version++;
        Changed?.Invoke();
    }
}
