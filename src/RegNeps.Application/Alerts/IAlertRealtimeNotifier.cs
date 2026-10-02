namespace RegNeps.Application.Alerts;

public interface IAlertRealtimeNotifier
{
    Task NotifyCriticalAlertAsync(
        IReadOnlyList<string> recipientUserIds,
        CriticalAlertPushMessage message,
        CancellationToken ct = default);
}
