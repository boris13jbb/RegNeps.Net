using Microsoft.Extensions.DependencyInjection;
using RegNeps.OfflineStore.Bridge;
using RegNeps.OfflineStore.Services;

namespace RegNeps.Mobile.Local;

/// <summary>
/// Handlers nativos del bridge 2D.1. No expone sync push/pull.
/// </summary>
public sealed class MauiOfflineBridgeHandlers : IOfflineBridgeHandlers
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<Task> _openCaptureAsync;

    public MauiOfflineBridgeHandlers(IServiceScopeFactory scopeFactory, Func<Task> openCaptureAsync)
    {
        _scopeFactory = scopeFactory;
        _openCaptureAsync = openCaptureAsync;
    }

    public async Task SaveSessionAsync(OfflineSessionSavePayload payload, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<OfflineSessionService>();

        // Nunca pasar cookie/token: secureAuthMaterial queda null en 2D.1.
        await sessions.UpsertUxSnapshotAsync(
            payload.UserId,
            payload.Username,
            payload.RoleCode,
            payload.Permissions,
            payload.ServerBaseUrl,
            ttl: null,
            secureAuthMaterial: null,
            ct);
    }

    public async Task<ClearLocalSessionResult> ClearSessionAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<OfflineSessionService>();
        return await sessions.ClearUxSnapshotAsync(ct);
    }

    public async Task<OfflineSessionViewDto> GetSessionAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<OfflineSessionService>();
        var raw = await sessions.GetRawSessionAsync(ct);
        return sessions.ToView(raw);
    }

    public Task OpenCaptureAsync(CancellationToken ct = default) => _openCaptureAsync();
}
