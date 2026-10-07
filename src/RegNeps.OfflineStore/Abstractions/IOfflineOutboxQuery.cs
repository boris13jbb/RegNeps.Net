using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;

namespace RegNeps.OfflineStore.Abstractions;

/// <summary>
/// Abstracción mínima para futuras fases de sync (2B+). Sin comportamiento de envío.
/// </summary>
public interface IOfflineOutboxQuery
{
    Task<IReadOnlyList<PendingOperation>> ListByStatusAsync(
        PendingOperationStatus status,
        int take = 100,
        CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);
}
