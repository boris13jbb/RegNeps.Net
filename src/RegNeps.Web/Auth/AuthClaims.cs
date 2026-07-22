using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
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

        foreach (var permission in RolePermissions.ForRole(user.EffectiveRole))
        {
            claims.Add(new Claim("permission", permission.ToString()));
        }

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

    public CurrentUserService(AuthenticationStateProvider authState) => _authState = authState;

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

        return new UserSession(id, username, display, role, isSuper);
    }
}

public sealed record UserSession(
    string? UserId,
    string Username,
    string DisplayName,
    AppUserRole Role,
    bool IsSuperAdmin)
{
    public AppUserRole EffectiveRole => IsSuperAdmin ? AppUserRole.SuperAdmin : Role;

    public bool Has(AppPermission permission) =>
        RolePermissions.Has(Role, IsSuperAdmin, true, permission);

    /// <summary>Operario solo ve sus registros; el resto ve el workspace.</summary>
    public bool SeesAllRecords =>
        EffectiveRole is not AppUserRole.Operario;
}
