using RegNeps.Application.Abstractions;
using RegNeps.Application.Permissions;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Permissions;

namespace RegNeps.Application.Alerts;

public sealed class AlertCriticalPublisher : IAlertCriticalPublisher
{
    private readonly IUserRepository _users;
    private readonly IAlertConfigRepository _alertConfig;
    private readonly IPermissionService _permissions;
    private readonly IAlertRealtimeNotifier _notifier;

    public AlertCriticalPublisher(
        IUserRepository users,
        IAlertConfigRepository alertConfig,
        IPermissionService permissions,
        IAlertRealtimeNotifier notifier)
    {
        _users = users;
        _alertConfig = alertConfig;
        _permissions = permissions;
        _notifier = notifier;
    }

    public async Task PublishNewCriticalAsync(NepRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var config = await _alertConfig.GetAsync(ct);
        if (!config.AlertasActivas)
        {
            return;
        }

        await _permissions.EnsureLoadedAsync(ct);

        var createdBy = record.CreatedByUserId;
        var recipients = new List<string>();

        foreach (var user in await _users.ListAsync(includeDeleted: false, ct))
        {
            if (!user.IsActive || user.DeletedAt is not null)
            {
                continue;
            }

            var roleCode = user.EffectiveRoleCode;
            var hasViewAlerts = _permissions.HasPermissionByRoleCode(
                roleCode,
                user.IsSuperAdmin,
                isActive: true,
                AppPermission.ViewAlerts);
            var seesAll = _permissions.SeesAllRecords(roleCode, user.IsSuperAdmin);

            if (!AlertNotificationRecipientRules.ShouldReceiveCriticalAlert(
                    alertasActivas: true,
                    isActive: true,
                    hasViewAlerts,
                    seesAll,
                    user.Id.ToString(),
                    user.ExternalUserId,
                    createdBy))
            {
                continue;
            }

            recipients.Add(user.Id.ToString());
        }

        if (recipients.Count == 0)
        {
            return;
        }

        var message = new CriticalAlertPushMessage(record.Id, record.CreatedAt);
        await _notifier.NotifyCriticalAlertAsync(recipients, message, ct);
    }
}
