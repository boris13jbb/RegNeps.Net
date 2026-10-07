using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RegNeps.OfflineStore.Sync.Ux;

namespace RegNeps.OfflineStore.Sync.Recovery;

/// <summary>
/// FASE 2E — deduplicación/coalescing: como máximo un Pull activo por contexto.
/// Trigger durante Pull → marca necesidad de otro Pull al terminar (no cola infinita).
/// Usa el mismo <see cref="ManualSyncGate"/> que el sync manual para evitar concurrencia.
/// </summary>
public sealed class SyncRecoveryCoordinator : ISyncRecoveryCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ManualSyncGate _gate;
    private readonly ILogger<SyncRecoveryCoordinator> _logger;
    private readonly object _lock = new();

    private bool _pullRunning;
    private bool _pullRequested;
    private SyncRecoveryPhase _phase = SyncRecoveryPhase.Idle;
    private string? _lastCorrelationId;

    public SyncRecoveryCoordinator(
        IServiceScopeFactory scopeFactory,
        ManualSyncGate gate,
        ILogger<SyncRecoveryCoordinator>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _gate = gate;
        _logger = logger ?? NullLogger<SyncRecoveryCoordinator>.Instance;
    }

    public SyncRecoveryPhase Phase
    {
        get
        {
            lock (_lock)
            {
                return _phase;
            }
        }
    }

    public bool IsPullRunning
    {
        get
        {
            lock (_lock)
            {
                return _pullRunning;
            }
        }
    }

    public bool IsPullRequested
    {
        get
        {
            lock (_lock)
            {
                return _pullRequested;
            }
        }
    }

    public void SetConnectivityPhase(SyncRecoveryPhase phase)
    {
        if (phase is not (SyncRecoveryPhase.Disconnected
            or SyncRecoveryPhase.Reconnecting
            or SyncRecoveryPhase.Idle))
        {
            return;
        }

        lock (_lock)
        {
            if (_pullRunning || _pullRequested)
            {
                return;
            }

            _phase = phase;
        }
    }

    public void RequestRecovery(string reason, string? correlationId = null)
    {
        var corr = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("N")
            : correlationId.Trim();

        var shouldStart = false;
        lock (_lock)
        {
            _pullRequested = true;
            _lastCorrelationId = corr;
            _phase = SyncRecoveryPhase.PendingPull;
            if (!_pullRunning)
            {
                _pullRunning = true;
                shouldStart = true;
            }
        }

        _logger.LogInformation(
            "Sync recovery requested. Reason={Reason} CorrelationId={CorrelationId} StartedRunner={StartedRunner}",
            reason,
            corr,
            shouldStart);

        if (shouldStart)
        {
            _ = Task.Run(() => RunLoopAsync(reason, corr));
        }
    }

    private async Task RunLoopAsync(string initialReason, string initialCorrelationId)
    {
        var reason = initialReason;
        var correlationId = initialCorrelationId;

        try
        {
            while (true)
            {
                lock (_lock)
                {
                    _pullRequested = false;
                    _phase = SyncRecoveryPhase.Synchronizing;
                    correlationId = _lastCorrelationId ?? correlationId;
                }

                var startedUtc = DateTime.UtcNow;
                long cursorBefore = -1;
                long cursorAfter = -1;
                var pages = 0;
                var pullCompleted = false;
                string? error = null;

                // Esperar gate compartido (sync manual / otro recovery).
                while (!_gate.TryEnter())
                {
                    await Task.Delay(40).ConfigureAwait(false);
                }

                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var engine = scope.ServiceProvider.GetRequiredService<ISyncEngine>();

                    var result = await engine.PullAsync(CancellationToken.None).ConfigureAwait(false);
                    cursorAfter = result.CursorAfter;
                    pullCompleted = result.PullCompleted;
                    cursorBefore = result.CursorBefore;
                    pages = result.PullPagesProcessed;

                    if (!result.Started)
                    {
                        error = result.Message ?? "Pull no iniciado";
                        lock (_lock)
                        {
                            _phase = result.SessionMissingOrExpired || result.AuthRequired
                                ? SyncRecoveryPhase.Idle
                                : SyncRecoveryPhase.SyncError;
                        }
                    }
                    else if (!pullCompleted)
                    {
                        error = result.Message ?? "Pull incompleto";
                        lock (_lock)
                        {
                            _phase = SyncRecoveryPhase.SyncError;
                        }
                    }
                    else
                    {
                        lock (_lock)
                        {
                            _phase = SyncRecoveryPhase.Synchronized;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex.Message;
                    lock (_lock)
                    {
                        _phase = SyncRecoveryPhase.SyncError;
                    }

                    _logger.LogWarning(
                        ex,
                        "Sync recovery Pull failed. Reason={Reason} CorrelationId={CorrelationId}",
                        reason,
                        correlationId);
                }
                finally
                {
                    _gate.Exit();
                }

                var durationMs = (long)(DateTime.UtcNow - startedUtc).TotalMilliseconds;
                _logger.LogInformation(
                    "Sync recovery Pull finished. Reason={Reason} CorrelationId={CorrelationId} CursorBefore={CursorBefore} CursorAfter={CursorAfter} Pages={Pages} PullCompleted={PullCompleted} DurationMs={DurationMs} Error={Error}",
                    reason,
                    correlationId,
                    cursorBefore,
                    cursorAfter,
                    pages,
                    pullCompleted,
                    durationMs,
                    error);

                lock (_lock)
                {
                    if (!_pullRequested)
                    {
                        _pullRunning = false;
                        return;
                    }

                    // Otro trigger llegó durante el Pull: un solo Pull adicional (coalesce).
                    reason = "coalesced-pending";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync recovery runner crashed. CorrelationId={CorrelationId}", correlationId);
            lock (_lock)
            {
                _pullRunning = false;
                _phase = SyncRecoveryPhase.SyncError;
                if (_pullRequested)
                {
                    _pullRunning = true;
                    _ = Task.Run(() => RunLoopAsync("runner-restart", _lastCorrelationId ?? correlationId));
                }
            }
        }
    }
}
