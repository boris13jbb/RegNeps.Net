namespace RegNeps.OfflineStore.Sync.Ux;

/// <summary>
/// Impide ejecuciones concurrentes de sincronización manual (FASE 2D.3).
/// No inicia timers ni background sync.
/// </summary>
public sealed class ManualSyncGate
{
    private int _busy;

    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void Exit() => Interlocked.Exchange(ref _busy, 0);
}

/// <summary>
/// Orquesta una invocación única a <see cref="ISyncEngine"/> bajo el gate manual.
/// </summary>
public sealed class ManualSyncRunner
{
    private readonly ISyncEngine _engine;
    private readonly ManualSyncGate _gate;

    public ManualSyncRunner(ISyncEngine engine, ManualSyncGate? gate = null)
    {
        _engine = engine;
        _gate = gate ?? new ManualSyncGate();
    }

    public ManualSyncGate Gate => _gate;

    /// <returns>
    /// <c>Started=false</c> si ya había otra sincronización en curso (no invoca el motor).
    /// </returns>
    public async Task<(bool Invoked, SyncRunResult? Result)> TrySyncAsync(CancellationToken ct = default)
    {
        if (!_gate.TryEnter())
        {
            return (false, null);
        }

        try
        {
            var result = await _engine.SyncAsync(ct);
            return (true, result);
        }
        finally
        {
            _gate.Exit();
        }
    }
}
