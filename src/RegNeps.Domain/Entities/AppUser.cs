using RegNeps.Domain.Constants;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;

namespace RegNeps.Domain.Entities;

/// <summary>Usuario de la aplicación (migración desde AppUser / Firebase Auth).</summary>
public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public AppUserRole Role { get; set; } = AppUserRole.Operario;

    /// <summary>Código del rol en <see cref="AppRole"/>; si es nulo se usa <see cref="Role"/>.</summary>
    public string? RoleCode { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsSuperAdmin { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>UID de Firebase Auth (migración). Permite enlazar registros históricos.</summary>
    public string? ExternalUserId { get; set; }

    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

    public AppUserRole EffectiveRole => IsSuperAdmin ? AppUserRole.SuperAdmin : Role;

    /// <summary>Código efectivo para permisos y visibilidad de registros.</summary>
    public string EffectiveRoleCode =>
        IsSuperAdmin
            ? SystemRoleCodes.SuperAdmin
            : string.IsNullOrWhiteSpace(RoleCode)
                ? SystemRoleCodes.FromEnum(Role)
                : RoleCode.Trim();

    public void ApplyRoleCode(string roleCode)
    {
        RoleCode = roleCode.Trim();
        if (SystemRoleCodes.TryParseEnum(RoleCode, out var parsed))
        {
            Role = parsed;
        }
    }

    /// <summary>
    /// Consulta la matriz inicial, no la configuración persistida.
    /// La autorización en runtime debe usar <c>IPermissionService</c>.
    /// </summary>
    public bool HasPermission(AppPermission permission) =>
        RolePermissions.Has(Role, IsSuperAdmin, IsActive && DeletedAt is null, permission);
}
