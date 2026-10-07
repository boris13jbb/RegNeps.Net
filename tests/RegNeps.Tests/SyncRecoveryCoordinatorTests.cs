using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RegNeps.OfflineStore.Sync;
using RegNeps.OfflineStore.Sync.Recovery;
using RegNeps.OfflineStore.Sync.Ux;

namespace RegNeps.Tests;

/// <summary>FASE 2E — coalescing / un Pull activo / triggers no perdidos.</summary>
public sealed class SyncRecoveryCoordinatorTests
{
    [Fact]
    public async Task Coalesces_Multiple_Triggers_Into_At_Most_Two_Pulls()
    {
        var pull = new CountingPullEngine(delayMs: 120);
        var services = new ServiceCollection();
        services.AddSingleton<ISyncEngine>(pull);
        var sp = services.BuildServiceProvider();
        var gate = new ManualSyncGate();
        var coordinator = new SyncRecoveryCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            gate,
            NullLogger<SyncRecoveryCoordinator>.Instance);

        coordinator.RequestRecovery("t1");
        coordinator.RequestRecovery("t2");
        coordinator.RequestRecovery("t3");
        coordinator.RequestRecovery("t4");

        await WaitUntilAsync(() => !coordinator.IsPullRunning && !coordinator.IsPullRequested, 5000);

        // Primer Pull + como máximo un coalesced; nunca 4 concurrentes.
        Assert.InRange(pull.PullCalls, 1, 2);
        Assert.Equal(1, pull.MaxConcurrent);
        Assert.Equal(SyncRecoveryPhase.Synchronized, coordinator.Phase);
    }

    [Fact]
    public async Task Trigger_During_Pull_Schedules_FollowUp()
    {
        var pull = new CountingPullEngine(delayMs: 200);
        var services = new ServiceCollection();
        services.AddSingleton<ISyncEngine>(pull);
        var sp = services.BuildServiceProvider();
        var coordinator = new SyncRecoveryCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ManualSyncGate(),
            NullLogger<SyncRecoveryCoordinator>.Instance);

        coordinator.RequestRecovery("first");
        await WaitUntilAsync(() => pull.PullCalls >= 1, 2000);
        coordinator.RequestRecovery("during");
        await WaitUntilAsync(() => !coordinator.IsPullRunning && !coordinator.IsPullRequested, 5000);

        Assert.Equal(2, pull.PullCalls);
        Assert.Equal(1, pull.MaxConcurrent);
    }

    [Fact]
    public async Task Temporary_Pull_Failure_Does_Not_Corrupt_And_Allows_Retry()
    {
        var pull = new CountingPullEngine(delayMs: 10) { FailNext = true };
        var services = new ServiceCollection();
        services.AddSingleton<ISyncEngine>(pull);
        var sp = services.BuildServiceProvider();
        var coordinator = new SyncRecoveryCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ManualSyncGate(),
            NullLogger<SyncRecoveryCoordinator>.Instance);

        coordinator.RequestRecovery("fail");
        await WaitUntilAsync(() => !coordinator.IsPullRunning, 3000);
        Assert.Equal(SyncRecoveryPhase.SyncError, coordinator.Phase);

        pull.FailNext = false;
        coordinator.RequestRecovery("retry");
        await WaitUntilAsync(() => coordinator.Phase == SyncRecoveryPhase.Synchronized, 3000);
        Assert.True(pull.PullCalls >= 2);
    }

    [Fact]
    public async Task Concurrent_Triggers_Never_Exceed_One_Active_Pull()
    {
        var pull = new CountingPullEngine(delayMs: 80);
        var services = new ServiceCollection();
        services.AddSingleton<ISyncEngine>(pull);
        var sp = services.BuildServiceProvider();
        var coordinator = new SyncRecoveryCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ManualSyncGate(),
            NullLogger<SyncRecoveryCoordinator>.Instance);

        var tasks = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => coordinator.RequestRecovery("burst-" + i)))
            .ToArray();
        await Task.WhenAll(tasks);
        await WaitUntilAsync(() => !coordinator.IsPullRunning && !coordinator.IsPullRequested, 8000);

        Assert.Equal(1, pull.MaxConcurrent);
        Assert.True(pull.PullCalls >= 1);
        Assert.True(pull.PullCalls <= 21);
    }

    [Fact]
    public void SignalR_Disconnect_Phase_Is_Not_Outbox_SyncError()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISyncEngine>(new CountingPullEngine(0));
        var sp = services.BuildServiceProvider();
        var coordinator = new SyncRecoveryCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ManualSyncGate());

        coordinator.SetConnectivityPhase(SyncRecoveryPhase.Disconnected);
        Assert.Equal(SyncRecoveryPhase.Disconnected, coordinator.Phase);
        coordinator.SetConnectivityPhase(SyncRecoveryPhase.Reconnecting);
        Assert.Equal(SyncRecoveryPhase.Reconnecting, coordinator.Phase);
        // No invoca Pull solo por fase de conectividad.
        Assert.False(coordinator.IsPullRunning);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Timeout esperando condición de recuperación.");
    }

    private sealed class CountingPullEngine : ISyncEngine
    {
        private readonly int _delayMs;
        private int _concurrent;
        private int _maxConcurrent;

        public CountingPullEngine(int delayMs) => _delayMs = delayMs;

        public int PullCalls { get; private set; }
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public bool FailNext { get; set; }

        public Task<SyncRunResult> SyncAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Recovery no debe llamar SyncAsync (Push+Pull).");

        public async Task<SyncRunResult> PullAsync(CancellationToken ct = default)
        {
            var c = Interlocked.Increment(ref _concurrent);
            Interlocked.Exchange(ref _maxConcurrent, Math.Max(Volatile.Read(ref _maxConcurrent), c));
            try
            {
                PullCalls++;
                if (_delayMs > 0)
                {
                    await Task.Delay(_delayMs, ct);
                }

                if (FailNext)
                {
                    FailNext = false;
                    return new SyncRunResult
                    {
                        Started = true,
                        PullCompleted = false,
                        Message = "transient",
                        CursorBefore = 10,
                        CursorAfter = 10
                    };
                }

                return new SyncRunResult
                {
                    Started = true,
                    PullCompleted = true,
                    CursorBefore = 10,
                    CursorAfter = 12,
                    PullPagesProcessed = 1
                };
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }
}
