namespace RegNeps.Application.Alerts;

/// <summary>Implementación por defecto cuando no hay host SignalR (p. ej. herramientas de migración).</summary>
public sealed class NoOpAlertRealtimeNotifier : IAlertRealtimeNotifier
{
    public Task NotifyCriticalAlertAsync(
        IReadOnlyList<string> recipientUserIds,
        CriticalAlertPushMessage message,
        CancellationToken ct = default) =>
        Task.CompletedTask;
}
