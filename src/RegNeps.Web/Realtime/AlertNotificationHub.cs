using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RegNeps.Application.Permissions;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
using RegNeps.Web.Auth;

namespace RegNeps.Web.Realtime;

/// <summary>
/// Hub de alertas críticas y señal liviana de recuperación de sync (FASE 2E).
/// SignalR notifica; Pull sincroniza. No transporta change logs ni avanza cursores.
/// </summary>
[Authorize]
public sealed class AlertNotificationHub : Hub
{
    public const string CriticalAlertMethod = "CriticalAlertReceived";

    /// <summary>
    /// Señal de recuperación: «puede haber cambios → ejecutar Pull».
    /// Payload vacío / mínimo; no incluye snapshots ni Sequence.
    /// </summary>
    public const string SyncRecoveryMethod = "SyncRecoverySuggested";

    private readonly IPermissionService _permissions;

    public AlertNotificationHub(IPermissionService permissions) => _permissions = permissions;

    public static string UserGroupName(string userId) => $"alert-user:{userId}";

    /// <summary>Grupo para señal de recuperación (Capture/View/ViewAlerts).</summary>
    public static string SyncUserGroupName(string userId) => $"sync-user:{userId}";

    public override async Task OnConnectedAsync()
    {
        await _permissions.EnsureLoadedAsync(Context.ConnectionAborted);

        var userId = Context.User?.FindFirstValue(AuthClaims.UserId);
        if (string.IsNullOrWhiteSpace(userId) || Context.User is null)
        {
            Context.Abort();
            return;
        }

        var canAlerts = await HasPermissionAsync(Context.User, AppPermission.ViewAlerts);
        var canSyncSignal = canAlerts
            || await HasPermissionAsync(Context.User, AppPermission.CaptureRecords)
            || await HasPermissionAsync(Context.User, AppPermission.ViewRecords);

        if (!canSyncSignal)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, SyncUserGroupName(userId));
        if (canAlerts)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(userId));
        }

        await base.OnConnectedAsync();
    }

    private async Task<bool> HasPermissionAsync(ClaimsPrincipal user, AppPermission permission)
    {
        var isSuper = string.Equals(
            user.FindFirstValue(AuthClaims.IsSuperAdmin), "true", StringComparison.OrdinalIgnoreCase);
        var roleCode = user.FindFirstValue(AuthClaims.RoleCode);
        if (!string.IsNullOrWhiteSpace(roleCode))
        {
            return _permissions.HasPermissionByRoleCode(roleCode, isSuper, true, permission);
        }

        Enum.TryParse<AppUserRole>(user.FindFirstValue(AuthClaims.Role), out var role);
        return await _permissions.HasPermissionAsync(role, isSuper, true, permission);
    }
}
