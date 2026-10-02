namespace RegNeps.OfflineStore.Entities;

/// <summary>Estado singleton de sincronización del dispositivo (cursor futuro en 2B).</summary>
public sealed class SyncState
{
    public int Id { get; set; } = 1;

    public DateTime? LastSuccessfulSyncUtc { get; set; }

    /// <summary>Cursor monotónico del pull (reservado; 2B).</summary>
    public long LastPulledSequence { get; set; }

    public string? LastError { get; set; }

    public string? LastConnectivityStatus { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public DateTime? UpdatedAtUtc { get; set; }
}
