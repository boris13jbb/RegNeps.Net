using Microsoft.AspNetCore.SignalR;
using RegNeps.Application.Alerts;

namespace RegNeps.Web.Realtime;

public sealed class SignalRAlertRealtimeNotifier : IAlertRealtimeNotifier
{
    private readonly IHubContext<AlertNotificationHub> _hub;

    public SignalRAlertRealtimeNotifier(IHubContext<AlertNotificationHub> hub)
    {
        _hub = hub;
    }

    public async Task NotifyCriticalAlertAsync(
        IReadOnlyList<string> recipientUserIds,
        CriticalAlertPushMessage message,
        CancellationToken ct = default)
    {
        if (recipientUserIds.Count == 0)
        {
            return;
        }

        var payload = new
        {
            recordId = message.RecordId,
            createdAtUtc = message.CreatedAtUtc,
            summary = message.Summary
        };

        var tasks = new List<Task>(recipientUserIds.Count);
        foreach (var userId in recipientUserIds.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                continue;
            }

            tasks.Add(_hub.Clients
                .Group(AlertNotificationHub.UserGroupName(userId))
                .SendAsync(AlertNotificationHub.CriticalAlertMethod, payload, ct));
        }

        await Task.WhenAll(tasks);
    }
}
