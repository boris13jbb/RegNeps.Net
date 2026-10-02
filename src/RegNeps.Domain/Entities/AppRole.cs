namespace RegNeps.Domain.Entities;

/// <summary>Rol parametrizable (sistema o personalizado) con permisos persistidos.</summary>
public sealed class AppRole
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Identificador estable (p. ej. Operario, mi_rol_qa). Único.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>Roles migrados del enum; no se eliminan desde la UI.</summary>
    public bool IsSystem { get; set; }

    /// <summary>Si false, el usuario solo ve sus propios registros (comportamiento Operario).</summary>
    public bool SeesAllRecords { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RolePermission> Permissions { get; set; } = new List<RolePermission>();
}
