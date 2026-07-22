using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;

namespace RegNeps.Application.Auth;

public sealed class AuthService
{
    private readonly IUserRepository _users;

    public AuthService(IUserRepository users) => _users = users;

    public async Task<AppUser> LoginAsync(string usernameOrEmail, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Usuario y contraseña son obligatorios.");
        }

        var key = usernameOrEmail.Trim();
        AppUser? user = null;

        if (key.Contains('@', StringComparison.Ordinal))
        {
            var all = await _users.ListAsync(ct: ct);
            user = all.FirstOrDefault(u =>
                string.Equals(u.Email, key, StringComparison.OrdinalIgnoreCase));
        }

        user ??= await _users.FindByUsernameAsync(key, ct);

        if (user is null || !user.IsActive || user.DeletedAt is not null)
        {
            throw new InvalidOperationException("Credenciales inválidas o usuario inactivo.");
        }

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            throw new InvalidOperationException("Credenciales inválidas o usuario inactivo.");
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _users.UpdateAsync(user, ct);
        return user;
    }

    public static string HashPassword(string password) => BCrypt.Net.BCrypt.HashPassword(password);
}

public sealed class UserAdminService
{
    private readonly IUserRepository _users;

    public UserAdminService(IUserRepository users) => _users = users;

    public Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default) =>
        _users.ListAsync(ct: ct);

    public async Task<AppUser> CreateAsync(
        string username,
        string password,
        AppUserRole role,
        string? displayName,
        string? email,
        CancellationToken ct = default)
    {
        if (role == AppUserRole.SuperAdmin)
        {
            throw new InvalidOperationException("No se puede crear un super_admin desde el panel.");
        }

        username = username.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Usuario y contraseña son obligatorios.");
        }

        if (await _users.FindByUsernameAsync(username, ct) is not null)
        {
            throw new InvalidOperationException("El nombre de usuario ya existe.");
        }

        var user = new AppUser
        {
            Username = username,
            DisplayName = displayName?.Trim() ?? username,
            Email = email?.Trim(),
            Role = role,
            PasswordHash = AuthService.HashPassword(password),
            IsActive = true
        };
        return await _users.AddAsync(user, ct);
    }

    public async Task UpdateRoleAsync(Guid userId, AppUserRole role, CancellationToken ct = default)
    {
        if (role == AppUserRole.SuperAdmin)
        {
            throw new InvalidOperationException("No se puede promover a super_admin desde el panel.");
        }

        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new InvalidOperationException("Usuario no encontrado.");

        if (user.IsSuperAdmin || user.Role == AppUserRole.SuperAdmin)
        {
            throw new InvalidOperationException("No se puede cambiar el rol de un super administrador.");
        }

        user.Role = role;
        user.UpdatedAt = DateTime.UtcNow;
        await _users.UpdateAsync(user, ct);
    }

    public async Task SetActiveAsync(Guid userId, bool active, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new InvalidOperationException("Usuario no encontrado.");

        if ((user.IsSuperAdmin || user.Role == AppUserRole.SuperAdmin) && !active)
        {
            var supers = await _users.CountSuperAdminsAsync(ct);
            if (supers <= 1)
            {
                throw new InvalidOperationException("No se puede desactivar el último super administrador.");
            }
        }

        user.IsActive = active;
        user.UpdatedAt = DateTime.UtcNow;
        await _users.UpdateAsync(user, ct);
    }

    public async Task ResetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
        {
            throw new ArgumentException("La contraseña debe tener al menos 6 caracteres.");
        }

        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new InvalidOperationException("Usuario no encontrado.");
        user.PasswordHash = AuthService.HashPassword(newPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await _users.UpdateAsync(user, ct);
    }

    public async Task SoftDeleteAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new InvalidOperationException("Usuario no encontrado.");

        if (user.IsSuperAdmin || user.Role == AppUserRole.SuperAdmin)
        {
            var supers = await _users.CountSuperAdminsAsync(ct);
            if (supers <= 1)
            {
                throw new InvalidOperationException("No se puede eliminar el último super administrador.");
            }
        }

        user.IsActive = false;
        user.DeletedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await _users.UpdateAsync(user, ct);
    }
}
