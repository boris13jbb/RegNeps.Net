using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RegNeps.Application.Permissions;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
using RegNeps.Web.Auth;

namespace RegNeps.Web.Realtime;

[Authorize]
public sealed class AlertNotificationHub : Hub
{
    public const string CriticalAlertMethod = "CriticalAlertReceived";

    private readonly IPermissionService _permissions;

    public AlertNotificationHub(IPermissionService permissions) => _permissions = permissions;

    public static string UserGroupName(string userId) => $"alert-user:{userId}";

    public override async Task OnConnectedAsync()
    {
        await _permissions.EnsureLoadedAsync(Context.ConnectionAborted);

        var userId = Context.User?.FindFirstValue(AuthClaims.UserId);
        if (string.IsNullOrWhiteSpace(userId) || !await HasViewAlertsAsync(Context.User!))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(userId));
        await base.OnConnectedAsync();
    }

    private async Task<bool> HasViewAlertsAsync(ClaimsPrincipal user)
    {
        var isSuper = string.Equals(
            user.FindFirstValue(AuthClaims.IsSuperAdmin), "true", StringComparison.OrdinalIgnoreCase);
        var roleCode = user.FindFirstValue(AuthClaims.RoleCode);
        if (!string.IsNullOrWhiteSpace(roleCode))
        {
            return _permissions.HasPermissionByRoleCode(roleCode, isSuper, true, AppPermission.ViewAlerts);
        }

        Enum.TryParse<AppUserRole>(user.FindFirstValue(AuthClaims.Role), out var role);
        return await _permissions.HasPermissionAsync(role, isSuper, true, AppPermission.ViewAlerts);
    }
}
