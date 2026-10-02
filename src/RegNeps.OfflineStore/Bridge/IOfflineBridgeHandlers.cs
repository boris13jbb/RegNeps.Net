namespace RegNeps.OfflineStore.Bridge;

/// <summary>Handlers nativos del bridge. Solo acciones whitelist.</summary>
public interface IOfflineBridgeHandlers
{
    Task SaveSessionAsync(OfflineSessionSavePayload payload, CancellationToken ct = default);

    Task<ClearLocalSessionResult> ClearSessionAsync(CancellationToken ct = default);

    Task<OfflineSessionViewDto> GetSessionAsync(CancellationToken ct = default);

    Task OpenCaptureAsync(CancellationToken ct = default);
}
