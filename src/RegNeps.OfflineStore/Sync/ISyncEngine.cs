namespace RegNeps.OfflineStore.Sync;

public interface ISyncEngine
{
    /// <summary>Ejecuta Push → Pull de forma explícita (sin background).</summary>
    Task<SyncRunResult> SyncAsync(CancellationToken ct = default);

    /// <summary>
    /// FASE 2E — solo Pull desde el cursor local (sin Push/Outbox).
    /// Usado por recuperación SignalR/conectividad. No avanza cursor por notificación.
    /// </summary>
    Task<SyncRunResult> PullAsync(CancellationToken ct = default);
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
    /// <summary>Cursor local al iniciar Pull (antes de aplicar páginas).</summary>
    public long CursorBefore { get; set; }
    public long CursorAfter { get; set; }
    public bool PullCompleted { get; set; }
    /// <summary>Páginas de Pull aplicadas en esta corrida (HasMore drenado).</summary>
    public int PullPagesProcessed { get; set; }
}
