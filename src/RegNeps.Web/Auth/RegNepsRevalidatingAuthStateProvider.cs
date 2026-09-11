using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Authentication.Cookies;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Enums;

namespace RegNeps.Web.Auth;

/// <summary>
/// Revalida la cookie en el circuito Blazor Server contra el estado real del usuario en BD.
/// </summary>
public sealed class RegNepsRevalidatingAuthStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;

    public RegNepsRevalidatingAuthStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory)
        : base(loggerFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        var user = authenticationState.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var idRaw = user.FindFirstValue(AuthClaims.UserId);
        if (!Guid.TryParse(idRaw, out var userId))
        {
            return false;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var dbUser = await users.GetByIdAsync(userId, cancellationToken);
        var claimRole = user.FindFirstValue(AuthClaims.Role);
        Enum.TryParse<AppUserRole>(claimRole, out var parsedRole);
        var claimSuper = string.Equals(user.FindFirstValue(AuthClaims.IsSuperAdmin), "true", StringComparison.OrdinalIgnoreCase);
        var roleMatches = dbUser is not null
            && dbUser.EffectiveRole == (claimSuper ? AppUserRole.SuperAdmin : parsedRole)
            && (dbUser.IsSuperAdmin || dbUser.Role == AppUserRole.SuperAdmin) == claimSuper;
        var valid = dbUser is not null && dbUser.IsActive && dbUser.DeletedAt is null && roleMatches;
        // #region agent log
        DebugSessionLog.Write("H5", "RegNepsRevalidatingAuthStateProvider.cs", "revalidate", new
        {
            userId = userId.ToString("N")[..8],
            valid,
            active = dbUser?.IsActive,
            deleted = dbUser?.DeletedAt is not null
        });
        // #endregion
        return valid;
    }
}
