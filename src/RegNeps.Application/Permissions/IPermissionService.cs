using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;

namespace RegNeps.Application.Permissions;

/// <summary>Identidad que solicita un cambio de permisos o una operación de usuarios.</summary>
public sealed record CallerContext(
    Guid? UserId,
    AppUserRole Role,
    bool IsSuperAdmin,
    bool IsActive = true,
    string? RoleCode = null)
{
    public string EffectiveRoleCode =>
        IsSuperAdmin
            ? Domain.Constants.SystemRoleCodes.SuperAdmin
            : string.IsNullOrWhiteSpace(RoleCode)
                ? Domain.Constants.SystemRoleCodes.FromEnum(Role)
                : RoleCode.Trim();
}

public interface IPermissionService
{
    /// <summary>Lectura de la caché ya cargada. No consulta la base en cada llamada.</summary>
    bool HasPermission(AppUserRole role, bool isSuperAdmin, bool isActive, AppPermission permission);

    bool HasPermissionByRoleCode(string? roleCode, bool isSuperAdmin, bool isActive, AppPermission permission);

    bool CanAdministerRoles(AppUserRole role, bool isSuperAdmin, bool isActive);

    bool SeesAllRecords(string? roleCode, bool isSuperAdmin);

    Task EnsureLoadedAsync(CancellationToken ct = default);

    Task<bool> HasPermissionAsync(
        AppUserRole role,
        bool isSuperAdmin,
        bool isActive,
        AppPermission permission,
        CancellationToken ct = default);

    Task<IReadOnlySet<AppPermission>> GetPermissionsForRoleAsync(AppUserRole role, CancellationToken ct = default);

    Task<IReadOnlySet<AppPermission>> GetPermissionsForRoleIdAsync(Guid roleId, CancellationToken ct = default);

    Task<IReadOnlyList<AppRole>> GetManageableRolesAsync(CancellationToken ct = default);

    Task<IReadOnlyDictionary<AppUserRole, IReadOnlySet<AppPermission>>> GetRolePermissionsAsync(CancellationToken ct = default);

    Task UpdateRolePermissionAsync(
        AppUserRole role,
        AppPermission permission,
        bool isEnabled,
        CallerContext actor,
        CancellationToken ct = default);

    /// <summary>Aplica el conjunto deseado del rol y registra solo los valores que cambian.</summary>
    Task<int> UpdateRolePermissionsAsync(
        AppUserRole role,
        IReadOnlyDictionary<AppPermission, bool> desired,
        CallerContext actor,
        CancellationToken ct = default);

    Task<int> UpdateRolePermissionsForRoleIdAsync(
        Guid roleId,
        IReadOnlyDictionary<AppPermission, bool> desired,
        CallerContext actor,
        CancellationToken ct = default);

    Task RefreshMatrixAsync(CancellationToken ct = default);
}
