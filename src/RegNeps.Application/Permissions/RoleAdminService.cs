using RegNeps.Application.Abstractions;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;

namespace RegNeps.Application.Permissions;

public sealed class RoleAdminService
{
    private readonly IRoleRepository _roles;
    private readonly IPermissionService _permissions;

    public RoleAdminService(IRoleRepository roles, IPermissionService permissions)
    {
        _roles = roles;
        _permissions = permissions;
    }

    public Task<IReadOnlyList<AppRole>> ListAllAsync(CancellationToken ct = default) =>
        _roles.ListAsync(includeInactive: true, ct);

    public Task<IReadOnlyList<AppRole>> ListAssignableAsync(CancellationToken ct = default) =>
        _roles.ListAsync(includeInactive: false, ct);

    public async Task<AppRole> CreateCustomRoleAsync(
        string code,
        string name,
        bool seesAllRecords,
        CallerContext actor,
        CancellationToken ct = default)
    {
        EnsureSuperAdmin(actor);
        code = NormalizeCustomCode(code);
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del rol es obligatorio.");
        }

        if (await _roles.GetByCodeAsync(code, ct) is not null)
        {
            throw new InvalidOperationException("Ya existe un rol con ese código.");
        }

        var now = DateTime.UtcNow;
        var role = new AppRole
        {
            Code = code,
            Name = name,
            IsActive = true,
            IsSystem = false,
            SeesAllRecords = seesAllRecords,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _roles.AddAsync(role, ct);
        await _permissions.RefreshMatrixAsync(ct);
        return role;
    }

    public async Task UpdateNameAsync(Guid roleId, string name, CallerContext actor, CancellationToken ct = default)
    {
        EnsureSuperAdmin(actor);
        var role = await RequireMutableRoleAsync(roleId, ct);
        role.Name = name.Trim();
        if (string.IsNullOrWhiteSpace(role.Name))
        {
            throw new ArgumentException("El nombre del rol es obligatorio.");
        }

        role.UpdatedAt = DateTime.UtcNow;
        await _roles.UpdateAsync(role, ct);
        await _permissions.RefreshMatrixAsync(ct);
    }

    public async Task SetActiveAsync(Guid roleId, bool active, CallerContext actor, CancellationToken ct = default)
    {
        EnsureSuperAdmin(actor);
        var role = await RequireMutableRoleAsync(roleId, ct);
        if (role.IsSystem && SystemRoleCodes.IsSuperAdminCode(role.Code))
        {
            throw new InvalidOperationException("No se puede desactivar el rol Super Administrador.");
        }

        role.IsActive = active;
        role.UpdatedAt = DateTime.UtcNow;
        await _roles.UpdateAsync(role, ct);
        await _permissions.RefreshMatrixAsync(ct);
    }

    public async Task SetSeesAllRecordsAsync(
        Guid roleId,
        bool seesAllRecords,
        CallerContext actor,
        CancellationToken ct = default)
    {
        EnsureSuperAdmin(actor);
        var role = await RequireMutableRoleAsync(roleId, ct);
        if (SystemRoleCodes.IsSuperAdminCode(role.Code))
        {
            throw new InvalidOperationException("No se puede modificar el alcance del Super Administrador.");
        }

        role.SeesAllRecords = seesAllRecords;
        role.UpdatedAt = DateTime.UtcNow;
        await _roles.UpdateAsync(role, ct);
        await _permissions.RefreshMatrixAsync(ct);
    }

    public async Task DeleteCustomRoleAsync(Guid roleId, CallerContext actor, CancellationToken ct = default)
    {
        EnsureSuperAdmin(actor);
        var role = await RequireMutableRoleAsync(roleId, ct);
        if (role.IsSystem)
        {
            throw new InvalidOperationException("Los roles de sistema no se pueden eliminar.");
        }

        var users = await _roles.CountUsersForRoleAsync(role.Code, ct);
        if (users > 0)
        {
            throw new InvalidOperationException("No se puede eliminar un rol asignado a usuarios.");
        }

        await _roles.DeleteWithPermissionsAsync(roleId, ct);
        await _permissions.RefreshMatrixAsync(ct);
    }

    public async Task<int> InitializeBaseRolesAsync(CallerContext actor, CancellationToken ct = default)
    {
        EnsureSuperAdmin(actor);
        await _roles.InitializeBaseRolesAsync(ct);
        await _permissions.RefreshMatrixAsync(ct);
        var roles = await _roles.ListAsync(ct: ct);
        return roles.Count;
    }

    private async Task<AppRole> RequireMutableRoleAsync(Guid roleId, CancellationToken ct)
    {
        var role = await _roles.GetByIdAsync(roleId, ct);
        if (role is null)
        {
            throw new InvalidOperationException("Rol no encontrado.");
        }

        return role;
    }

    private static void EnsureSuperAdmin(CallerContext actor)
    {
        if (!actor.IsSuperAdmin || !actor.IsActive)
        {
            throw new UnauthorizedAccessException("No tiene permiso para administrar roles y permisos.");
        }
    }

    private static string NormalizeCustomCode(string code)
    {
        code = code.Trim().ToLowerInvariant();
        if (code.Length < 2)
        {
            throw new ArgumentException("El código debe tener al menos 2 caracteres.");
        }

        if (!code.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-'))
        {
            throw new ArgumentException("El código solo puede contener letras, números, guion y guion bajo.");
        }

        if (SystemRoleCodes.Definitions.Any(d =>
                string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Ese código está reservado para un rol de sistema.");
        }

        return code;
    }
}
