using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;
using RegNeps.OfflineStore.Sync.Recovery;

namespace RegNeps.Mobile.Local;

/// <summary>
/// FASE 2E — cliente SignalR MAUI: notifica recuperación; Pull vía <see cref="ISyncRecoveryCoordinator"/>.
/// No aplica change logs ni muta Outbox/SyncState directamente.
/// Auth: cookie del WebView (misma autoridad que sync HTTP); no almacena secretos.
/// </summary>
public sealed class MauiSyncHubRecoveryService : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISyncAuthCookieProvider _cookies;
    private readonly ISyncRecoveryCoordinator _recovery;
    private readonly ILogger<MauiSyncHubRecoveryService> _logger;
    private readonly object _gate = new();

    private HubConnection? _hub;
    private string? _serverBaseUrl;
    private bool _handlersWired;
    private bool _connectivityWired;
    private int _ensureBusy;

    public MauiSyncHubRecoveryService(
        IServiceScopeFactory scopeFactory,
        ISyncAuthCookieProvider cookies,
        ISyncRecoveryCoordinator recovery,
        ILogger<MauiSyncHubRecoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _cookies = cookies;
        _recovery = recovery;
        _logger = logger;
    }

    /// <summary>Conecta (o reconecta) al hub si hay sesión UX + cookie.</summary>
    public Task EnsureConnectedAsync(CancellationToken ct = default) =>
        EnsureConnectedCoreAsync(requestPullOnConnect: true, ct);

    /// <summary>Tras resume/app foreground: reasegura hub y pide Pull.</summary>
    public async Task OnAppResumedAsync(CancellationToken ct = default)
    {
        await EnsureConnectedCoreAsync(requestPullOnConnect: true, ct).ConfigureAwait(false);
        _recovery.RequestRecovery("app-resumed");
    }

    private async Task EnsureConnectedCoreAsync(bool requestPullOnConnect, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _ensureBusy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            WireConnectivityOnce();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<OfflineSessionService>();
            var session = await sessions.GetValidSessionAsync(ct).ConfigureAwait(false);
            if (session is null || string.IsNullOrWhiteSpace(session.ServerBaseUrl))
            {
                await StopHubAsync().ConfigureAwait(false);
                return;
            }

            var baseUrl = session.ServerBaseUrl!.TrimEnd('/');
            var cookie = await _cookies.GetCookieHeaderAsync(baseUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(cookie))
            {
                _logger.LogDebug("Sync hub skip: sin cookie WebView.");
                return;
            }

            lock (_gate)
            {
                if (_hub is not null
                    && string.Equals(_serverBaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)
                    && _hub.State is HubConnectionState.Connected or HubConnectionState.Connecting
                        or HubConnectionState.Reconnecting)
                {
                    return;
                }
            }

            await StopHubAsync().ConfigureAwait(false);

            var hubUrl = $"{baseUrl}/hubs/alerts";
            var hub = new HubConnectionBuilder()
                .WithUrl(hubUrl, options =>
                {
                    options.Headers["Cookie"] = cookie!;
                })
                .WithAutomaticReconnect()
                .Build();

            WireHubHandlers(hub);

            try
            {
                await hub.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Sync hub Start falló (opcional). Url={Url}", hubUrl);
                await hub.DisposeAsync().ConfigureAwait(false);
                return;
            }

            lock (_gate)
            {
                _hub = hub;
                _serverBaseUrl = baseUrl;
            }

            _logger.LogInformation("Sync hub connected. Url={Url}", hubUrl);
            if (requestPullOnConnect)
            {
                _recovery.RequestRecovery("hub-connected");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _ensureBusy, 0);
        }
    }

    private void WireHubHandlers(HubConnection hub)
    {
        if (_handlersWired)
        {
            // Cada nueva conexión registra sus handlers.
        }

        hub.On<object?>(SyncRealtimeConstants.CriticalAlertMethod, _ =>
        {
            // No aplica datos: solo pide Pull (alertas Blazor no aplican en MAUI nativo).
            _recovery.RequestRecovery("critical-alert");
        });

        hub.On<object?>(SyncRealtimeConstants.SyncRecoveryMethod, _ =>
        {
            _recovery.RequestRecovery("sync-recovery-suggested");
        });

        hub.Reconnecting += _ =>
        {
            _recovery.SetConnectivityPhase(SyncRecoveryPhase.Reconnecting);
            _logger.LogInformation("Sync hub reconnecting.");
            return Task.CompletedTask;
        };

        hub.Closed += _ =>
        {
            _recovery.SetConnectivityPhase(SyncRecoveryPhase.Disconnected);
            _logger.LogInformation("Sync hub closed.");
            return Task.CompletedTask;
        };

        hub.Reconnected += connectionId =>
        {
            _logger.LogInformation(
                "Sync hub reconnected. ConnectionIdPresent={HasId}",
                !string.IsNullOrEmpty(connectionId));
            // No asumir eventos recibidos: Pull desde cursor local.
            _recovery.RequestRecovery("hub-reconnected");
            return Task.CompletedTask;
        };

        _handlersWired = true;
    }

    private void WireConnectivityOnce()
    {
        if (_connectivityWired)
        {
            return;
        }

        _connectivityWired = true;
        Connectivity.ConnectivityChanged += (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                _recovery.RequestRecovery("connectivity-restored");
                _ = EnsureConnectedCoreAsync(requestPullOnConnect: false, CancellationToken.None);
            }
            else
            {
                _recovery.SetConnectivityPhase(SyncRecoveryPhase.Disconnected);
            }
        };
    }

    /// <summary>Corta la conexión SignalR (p. ej. logout) sin desechar el servicio DI.</summary>
    public Task StopAsync() => StopHubAsync();

    private async Task StopHubAsync()
    {
        HubConnection? hub;
        lock (_gate)
        {
            hub = _hub;
            _hub = null;
            _serverBaseUrl = null;
            _handlersWired = false;
        }

        if (hub is null)
        {
            return;
        }

        try
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopHubAsync().ConfigureAwait(false);
    }
}
