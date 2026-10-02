using System.Collections.Immutable;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
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
    bool HasByRoleCode(string? roleCode, bool isSuperAdmin, bool isActive, AppPermission permission);
    IReadOnlySet<AppPermission> GetEnabled(AppUserRole role);
    IReadOnlySet<AppPermission> GetEnabledByRoleCode(string roleCode);
    bool SeesAllRecords(string? roleCode, bool isSuperAdmin);
    IReadOnlyList<RoleMatrixEntry> GetAllRoles();
    RoleMatrixEntry? FindByCode(string roleCode);
    RoleMatrixEntry? FindById(Guid roleId);
    AppRole ToAppRole(RoleMatrixEntry entry);
    void Replace(IReadOnlyList<RoleMatrixEntry> roles);
}

public sealed class PermissionMatrix : IPermissionMatrix
{
    private ImmutableDictionary<string, RoleMatrixEntry> _byCode =
        ImmutableDictionary<string, RoleMatrixEntry>.Empty;

    private int _loaded;

    public bool IsLoaded => Volatile.Read(ref _loaded) == 1;
    public long Version { get; private set; }
    public event Action? Changed;

    public bool Has(AppUserRole role, bool isSuperAdmin, bool isActive, AppPermission permission) =>
        HasByRoleCode(SystemRoleCodes.FromEnum(role), isSuperAdmin, isActive, permission);

    public bool HasByRoleCode(string? roleCode, bool isSuperAdmin, bool isActive, AppPermission permission)
    {
        if (!isActive)
        {
            return false;
        }

        if (isSuperAdmin || SystemRoleCodes.IsSuperAdminCode(roleCode))
        {
            return PermissionCatalog.IsKnown(permission);
        }

        if (permission == AppPermission.ManageRoles)
        {
            return false;
        }

        var code = string.IsNullOrWhiteSpace(roleCode)
            ? SystemRoleCodes.Operario
            : roleCode.Trim();

        if (!IsLoaded)
        {
            if (SystemRoleCodes.TryParseEnum(code, out var enumRole))
            {
                return RolePermissions.Has(enumRole, permission);
            }

            return false;
        }

        if (!_byCode.TryGetValue(code, out var entry) || !entry.IsActive)
        {
            return false;
        }

        return entry.Permissions.Contains(permission);
    }

    public IReadOnlySet<AppPermission> GetEnabled(AppUserRole role) =>
        GetEnabledByRoleCode(SystemRoleCodes.FromEnum(role));

    public IReadOnlySet<AppPermission> GetEnabledByRoleCode(string roleCode)
    {
        if (SystemRoleCodes.IsSuperAdminCode(roleCode))
        {
            return PermissionCatalog.All.Select(x => x.Permission).ToHashSet();
        }

        if (!IsLoaded)
        {
            if (SystemRoleCodes.TryParseEnum(roleCode, out var enumRole))
            {
                return RolePermissions.ForRole(enumRole)
                    .Where(p => p != AppPermission.ManageRoles)
                    .ToHashSet();
            }

            return ImmutableHashSet<AppPermission>.Empty;
        }

        return _byCode.TryGetValue(roleCode, out var entry)
            ? entry.Permissions
            : (IReadOnlySet<AppPermission>)ImmutableHashSet<AppPermission>.Empty;
    }

    public bool SeesAllRecords(string? roleCode, bool isSuperAdmin)
    {
        if (isSuperAdmin || SystemRoleCodes.IsSuperAdminCode(roleCode))
        {
            return true;
        }

        var code = string.IsNullOrWhiteSpace(roleCode)
            ? SystemRoleCodes.Operario
            : roleCode.Trim();

        if (!IsLoaded)
        {
            return SystemRoleCodes.TryParseEnum(code, out var enumRole)
                && enumRole is not AppUserRole.Operario;
        }

        return _byCode.TryGetValue(code, out var entry) && entry.SeesAllRecords;
    }

    public IReadOnlyList<RoleMatrixEntry> GetAllRoles() =>
        _byCode.Values.OrderBy(x => x.IsSystem ? 0 : 1).ThenBy(x => x.Code).ToList();

    public RoleMatrixEntry? FindByCode(string roleCode) =>
        _byCode.TryGetValue(roleCode.Trim(), out var entry) ? entry : null;

    public RoleMatrixEntry? FindById(Guid roleId) =>
        _byCode.Values.FirstOrDefault(x => x.Id == roleId);

    public AppRole ToAppRole(RoleMatrixEntry entry) =>
        new()
        {
            Id = entry.Id,
            Code = entry.Code,
            Name = entry.Name,
            IsActive = entry.IsActive,
            IsSystem = entry.IsSystem,
            SeesAllRecords = entry.SeesAllRecords
        };

    public void Replace(IReadOnlyList<RoleMatrixEntry> roles)
    {
        var next = roles.ToImmutableDictionary(
            x => x.Code,
            x => x,
            StringComparer.OrdinalIgnoreCase);
        _byCode = next;
        Volatile.Write(ref _loaded, 1);
        Version++;
        Changed?.Invoke();
    }
}
