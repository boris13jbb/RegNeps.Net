using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using RegNeps.Application.Permissions;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;

namespace RegNeps.Web.Auth;

public static class AuthClaims
{
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string Username = ClaimTypes.Name;
    public const string DisplayName = "display_name";
    public const string Role = ClaimTypes.Role;
    public const string IsSuperAdmin = "is_super_admin";
    public const string ExternalUserId = "external_user_id";

    public static ClaimsPrincipal CreatePrincipal(AppUser user)
    {
        var role = user.EffectiveRole.ToString();
        var claims = new List<Claim>
        {
            new(UserId, user.Id.ToString()),
            new(Username, user.Username),
            new(DisplayName, user.EffectiveDisplayName),
            new(Role, role),
            new(IsSuperAdmin, user.IsSuperAdmin || user.Role == AppUserRole.SuperAdmin ? "true" : "false")
        };

        if (!string.IsNullOrWhiteSpace(user.ExternalUserId))
        {
            claims.Add(new Claim(ExternalUserId, user.ExternalUserId));
        }

        // El rol viaja en la cookie. Los permisos no: se resuelven en cada comprobación
        // para que un cambio del Super Administrador no quede congelado hasta el logout.
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    public static async Task SignInAsync(HttpContext http, AppUser user)
    {
        var principal = CreatePrincipal(user);
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            });
    }
}

public sealed class CurrentUserService
{
    private readonly AuthenticationStateProvider _authState;
    private readonly IPermissionService _permissions;

    public CurrentUserService(AuthenticationStateProvider authState, IPermissionService permissions)
    {
        _authState = authState;
        _permissions = permissions;
    }

    public async Task<UserSession?> GetAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var user = state.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var id = user.FindFirstValue(AuthClaims.UserId);
        var username = user.FindFirstValue(AuthClaims.Username) ?? "";
        var display = user.FindFirstValue(AuthClaims.DisplayName) ?? username;
        var roleRaw = user.FindFirstValue(AuthClaims.Role) ?? nameof(AppUserRole.Operario);
        Enum.TryParse<AppUserRole>(roleRaw, out var role);
        var isSuper = string.Equals(user.FindFirstValue(AuthClaims.IsSuperAdmin), "true", StringComparison.OrdinalIgnoreCase)
                      || role == AppUserRole.SuperAdmin;
        var external = user.FindFirstValue(AuthClaims.ExternalUserId);
        await _permissions.EnsureLoadedAsync();

        return new UserSession(id, username, display, role, isSuper, external, _permissions);
    }

    /// <summary>Vuelve a leer la matriz vigente. No basta con el permiso que había al abrir la página.</summary>
    public async Task<bool> HasAsync(AppPermission permission)
    {
        var session = await GetAsync();
        return session?.Has(permission) == true;
    }
}

public sealed record UserSession(
    string? UserId,
    string Username,
    string DisplayName,
    AppUserRole Role,
    bool IsSuperAdmin,
    string? ExternalUserId = null,
    IPermissionService? Permissions = null)
{
    public AppUserRole EffectiveRole => IsSuperAdmin ? AppUserRole.SuperAdmin : Role;

    public bool Has(AppPermission permission) =>
        Permissions?.HasPermission(Role, IsSuperAdmin, true, permission)
        ?? RolePermissions.Has(Role, IsSuperAdmin, true, permission);

    public CallerContext ToCaller()
    {
        Guid? id = Guid.TryParse(UserId, out var parsed) ? parsed : null;
        return new CallerContext(id, Role, IsSuperAdmin);
    }

    /// <summary>Operario solo ve sus registros; el resto ve el workspace.</summary>
    public bool SeesAllRecords =>
        EffectiveRole is not AppUserRole.Operario;

    public Application.Records.RecordActor ToActor() =>
        Application.Records.RecordActor.Create(
            UserId, Username, DisplayName, Role, IsSuperAdmin, ExternalUserId);
}
