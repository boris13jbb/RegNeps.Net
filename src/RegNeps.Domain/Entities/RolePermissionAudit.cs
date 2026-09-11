using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;

namespace RegNeps.Domain.Entities;

/// <summary>
/// Registro de un cambio de la matriz rol → permiso. Quién lo hizo y cuándo queda en la base de datos.
/// </summary>
public sealed class RolePermissionAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AppUserRole Role { get; set; }
    public AppPermission Permission { get; set; }
    public bool PreviousValue { get; set; }
    public bool NewValue { get; set; }
    public Guid? ModifiedByUserId { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
