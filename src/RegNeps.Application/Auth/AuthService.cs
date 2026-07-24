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
            user = await _users.FindByEmailAsync(key, ct);
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
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Usuario y contraseña son obligatorios.");
        }

        ValidatePasswordStrength(password);

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

    public async Task SetActiveAsync(Guid userId, bool active, Guid? actorUserId = null, CancellationToken ct = default)
    {
        if (actorUserId is not null && actorUserId == userId && !active)
        {
            throw new InvalidOperationException("No puede desactivar su propia cuenta.");
        }

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
        ValidatePasswordStrength(newPassword);

        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new InvalidOperationException("Usuario no encontrado.");
        user.PasswordHash = AuthService.HashPassword(newPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await _users.UpdateAsync(user, ct);
    }

    public async Task SoftDeleteAsync(Guid userId, Guid? actorUserId = null, CancellationToken ct = default)
    {
        if (actorUserId is not null && actorUserId == userId)
        {
            throw new InvalidOperationException("No puede eliminar su propia cuenta.");
        }

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

    private static void ValidatePasswordStrength(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new ArgumentException("La contraseña debe tener al menos 8 caracteres.");
        }

        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
        {
            throw new ArgumentException("La contraseña debe incluir letras y números.");
        }
    }
}
