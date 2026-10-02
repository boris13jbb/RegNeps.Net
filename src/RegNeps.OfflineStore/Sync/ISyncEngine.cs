namespace RegNeps.OfflineStore.Sync;

public interface ISyncEngine
{
    /// <summary>Ejecuta Push → Pull de forma explícita (sin background).</summary>
    Task<SyncRunResult> SyncAsync(CancellationToken ct = default);
}

public sealed class SyncRunResult
{
    public bool Started { get; set; }
    public bool AuthRequired { get; set; }
    public bool SessionMissingOrExpired { get; set; }
    public string? Message { get; set; }

    public int PushedAccepted { get; set; }
    public int PushedDuplicate { get; set; }
    public int PushedConflict { get; set; }
    public int PushedSyncError { get; set; }
    public int PushedTransient { get; set; }
    public int SkippedOtherUser { get; set; }

    public int PulledUpserts { get; set; }
    public int PulledDeletes { get; set; }
    public long CursorAfter { get; set; }
    public bool PullCompleted { get; set; }
}
