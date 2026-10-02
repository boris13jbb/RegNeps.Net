using RegNeps.Domain.Permissions;

namespace RegNeps.Domain.Entities;

/// <summary>
/// Asignación persistida de un permiso a un rol. La autorización en runtime no lee la matriz estática.
/// </summary>
public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public AppRole? Role { get; set; }
    public AppPermission Permission { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
