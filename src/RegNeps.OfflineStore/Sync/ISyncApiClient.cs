using RegNeps.OfflineStore.Sync.Contracts;

namespace RegNeps.OfflineStore.Sync;

/// <summary>Transporte HTTP hacia /api/sync (sin UI ni cookie propia).</summary>
public interface ISyncApiClient
{
    Task<ClientSyncPushResponse> PushAsync(
        string serverBaseUrl,
        string? cookieHeader,
        ClientSyncPushRequest request,
        CancellationToken ct = default);

    Task<ClientSyncPullResponse> PullAsync(
        string serverBaseUrl,
        string? cookieHeader,
        ClientSyncPullRequest request,
        CancellationToken ct = default);
}
