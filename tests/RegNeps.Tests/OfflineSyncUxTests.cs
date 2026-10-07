using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RegNeps.OfflineStore;
using RegNeps.OfflineStore.Device;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;
using RegNeps.OfflineStore.Sync.Contracts;
using RegNeps.OfflineStore.Sync.Ux;

namespace RegNeps.Tests;

/// <summary>FASE 2D.3 — UX de sincronización: contadores, gate, mensajes, conflictos, logout.</summary>
public sealed class OfflineSyncUxTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-syncux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "test.db");
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            /* ignore */
        }

        return Task.CompletedTask;
    }

    private LocalSyncDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LocalSyncDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new LocalSyncDbContext(options);
    }

    private async Task<(OfflineCaptureService Capture, OfflineSessionService Sessions, OfflineSyncUxService Ux, LocalSyncDbContext Db)>
        SeedAsync(LocalSyncDbContext db, string userId = "user-a")
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM PendingOperations");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalNepRecords");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalSessions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SyncStates");
        db.ChangeTracker.Clear();

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-" + Guid.NewGuid().ToString("N")[..8]));
        var capture = new OfflineCaptureService(db, sessions, devices);
        var ux = new OfflineSyncUxService(db);

        await sessions.UpsertUxSnapshotAsync(
            userId,
            "alice",
            "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        db.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = 0 });
        await db.SaveChangesAsync();
        return (capture, sessions, ux, db);
    }

    [Fact]
    public async Task Counters_Pending_Synced_SyncError_Conflict()
    {
        await using var db = CreateContext();
        var (capture, _, ux, _) = await SeedAsync(db);

        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "2", Neps = 11 });
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "3", Neps = 12 });
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "4", Neps = 13 });

        var ops = await db.PendingOperations.OrderBy(o => o.CreatedAtUtc).ToListAsync();
        Assert.Equal(4, ops.Count);
        ops[0].Status = PendingOperationStatus.Pending;
        ops[1].Status = PendingOperationStatus.Synced;
        ops[2].Status = PendingOperationStatus.SyncError;
        ops[2].LastServerErrorCode = "FORBIDDEN";
        ops[3].Status = PendingOperationStatus.Conflict;
        ops[3].ConflictServerSnapshotJson = JsonSerializer.Serialize(new ClientNepRecordSnapshot
        {
            Telar = "SRV",
            Neps = 50,
            QualityLabel = "OK"
        }, ClientSyncJson.Options);
        await db.SaveChangesAsync();

        var counters = await ux.GetCountersAsync("user-a");
        Assert.Equal(1, counters.Pending);
        Assert.Equal(1, counters.Synced);
        Assert.Equal(1, counters.SyncError);
        Assert.Equal(1, counters.Conflict);
    }

    [Fact]
    public async Task ManualSyncGate_Prevents_Concurrent_And_Invokes_Once()
    {
        var gate = new ManualSyncGate();
        var calls = 0;
        var engine = new CountingSyncEngine(() =>
        {
            Interlocked.Increment(ref calls);
            return Task.Delay(150).ContinueWith(_ => new SyncRunResult { Started = true, PullCompleted = true });
        });

        var runner = new ManualSyncRunner(engine, gate);
        var t1 = runner.TrySyncAsync();
        var t2 = runner.TrySyncAsync();
        var results = await Task.WhenAll(t1, t2);

        Assert.Equal(1, results.Count(r => r.Invoked));
        Assert.Equal(1, results.Count(r => !r.Invoked));
        Assert.Equal(1, calls);
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public void MapRunResult_401_RequiresLogin()
    {
        var feedback = OfflineSyncUxService.MapRunResult(new SyncRunResult
        {
            Started = true,
            AuthRequired = true,
            Message = "Unauthorized 401"
        });

        Assert.True(feedback.RequiresLogin);
        Assert.Contains("iniciar sesión", feedback.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapRunResult_MissingCookie_RequiresLogin_DidNotStart()
    {
        var feedback = OfflineSyncUxService.MapRunResult(new SyncRunResult
        {
            Started = false,
            AuthRequired = true,
            Message = "No hay cookie de autenticación del WebView."
        });

        Assert.True(feedback.RequiresLogin);
        Assert.True(feedback.DidNotStart);
    }

    [Fact]
    public void Connectivity_LocalSession_Without_Cookie()
    {
        var kind = OfflineSyncUxService.ResolveConnectivity(
            hasLocalSession: true,
            localSessionExpired: false,
            hasAuthCookie: false,
            noNetwork: false,
            serverUnreachable: false,
            lastRunRequiresLogin: false);

        Assert.Equal(SyncConnectivityUxKind.LocalSessionWithoutOnlineAuth, kind);
        Assert.Contains("iniciar sesión", OfflineSyncUxService.ConnectivityBanner(kind), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribePushResult_Messages()
    {
        Assert.Equal(SyncResultUserMessages.SyncedOk, OfflineSyncUxService.DescribePushResult(ClientSyncResultNames.Accepted));
        Assert.Equal(SyncResultUserMessages.SyncedOk, OfflineSyncUxService.DescribePushResult(ClientSyncResultNames.Duplicate));
        Assert.Equal(SyncResultUserMessages.Transient, OfflineSyncUxService.DescribePushResult(ClientSyncResultNames.TransientError));
        Assert.Equal(SyncResultUserMessages.Forbidden, OfflineSyncUxService.DescribePushResult(ClientSyncResultNames.Forbidden));
        Assert.Equal(SyncResultUserMessages.Invalid, OfflineSyncUxService.DescribePushResult(ClientSyncResultNames.Invalid));
        Assert.Equal(SyncResultUserMessages.Conflict, OfflineSyncUxService.DescribePushResult(ClientSyncResultNames.Conflict));
    }

    [Fact]
    public async Task Conflict_Presentation_Does_Not_Mutate_Local_And_Shows_Review()
    {
        await using var db = CreateContext();
        var (capture, _, ux, _) = await SeedAsync(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "LOCAL", Neps = 20 });

        var op = await db.PendingOperations.SingleAsync();
        op.Status = PendingOperationStatus.Conflict;
        op.LastError = "Concurrency conflict";
        op.ConflictServerSnapshotJson = JsonSerializer.Serialize(new ClientNepRecordSnapshot
        {
            Id = created.Record.Id,
            Telar = "SERVER",
            Neps = 50,
            MtsCalculados = 50 / 0.09,
            QualityLabel = "OK",
            ConcurrencyStamp = "srv"
        }, ClientSyncJson.Options);
        created.Record.Telar = "LOCAL";
        created.Record.Neps = 20;
        created.Record.SyncStatus = LocalSyncStatus.Conflict;
        await db.SaveChangesAsync();

        var beforeTelar = created.Record.Telar;
        var beforeNeps = created.Record.Neps;

        var items = await ux.GetConflictsAsync("user-a");
        Assert.Single(items);
        Assert.Equal("Requiere revisión", items[0].ActionHint);
        Assert.Contains("LOCAL", items[0].LocalSummary);
        Assert.Contains("SERVER", items[0].ServerSummary);
        Assert.DoesNotContain("PayloadJson", items[0].LocalSummary);

        // Releer: GetConflictsAsync es solo lectura.
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.Equal(beforeTelar, local.Telar);
        Assert.Equal(beforeNeps, local.Neps);
        Assert.Equal(LocalSyncStatus.Conflict, local.SyncStatus);
        Assert.False(string.IsNullOrWhiteSpace(op.ConflictServerSnapshotJson));
    }

    [Fact]
    public async Task Network_Error_Path_Leaves_Pending()
    {
        await using var db = CreateContext();
        var (capture, _, ux, _) = await SeedAsync(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });

        var before = await ux.GetCountersAsync("user-a");
        Assert.Equal(1, before.Pending);

        // Simula feedback de fallo de red sin tocar Outbox (como haría la UI tras SyncTransportException).
        var feedback = OfflineSyncUxService.MapRunResult(new SyncRunResult
        {
            Started = true,
            Message = "Servidor no responde",
            PullCompleted = false,
            PushedTransient = 0
        }, before);

        Assert.False(feedback.Success);
        Assert.Equal(1, feedback.StillPending);

        var after = await ux.GetCountersAsync("user-a");
        Assert.Equal(1, after.Pending);
        Assert.Equal(0, after.Synced);
    }

    [Fact]
    public async Task Logout_Clears_LocalSession_Keeps_Outbox_And_Records()
    {
        await using var db = CreateContext();
        var (capture, sessions, ux, _) = await SeedAsync(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });

        var clear = await sessions.ClearUxSnapshotAsync();
        Assert.True(clear.Cleared);
        Assert.Equal(1, clear.PendingOperationsRetained);

        Assert.Null(await sessions.GetRawSessionAsync());
        Assert.Equal(1, await db.PendingOperations.CountAsync());
        Assert.Equal(1, await db.LocalNepRecords.CountAsync());

        // Sin sesión, SyncEngine no debe arrancar (contrato existente).
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-logout"));
        var engine = new SyncEngine(
            db,
            sessions,
            devices,
            new NoopSyncApi(),
            new StaticCookie("RegNeps.Auth=x"));
        var run = await engine.SyncAsync();
        Assert.False(run.Started);
        Assert.True(run.SessionMissingOrExpired);

        var counters = await ux.GetCountersAsync("user-a");
        Assert.Equal(1, counters.Pending);
    }

    [Fact]
    public void SanitizeError_Hides_Secrets()
    {
        var sanitized = OfflineSyncUxService.SanitizeError("cookie=RegNeps.Auth=abc; path=/");
        Assert.Equal(SyncResultUserMessages.RequiresLogin, sanitized);

        var stack = OfflineSyncUxService.SanitizeError("at RegNeps.OfflineStore.Sync.SyncEngine.Push...");
        Assert.Equal("Ocurrió un error al sincronizar. Intenta de nuevo más tarde.", stack);
    }

    private sealed class CountingSyncEngine : ISyncEngine
    {
        private readonly Func<Task<SyncRunResult>> _fn;

        public CountingSyncEngine(Func<Task<SyncRunResult>> fn) => _fn = fn;

        public Task<SyncRunResult> SyncAsync(CancellationToken ct = default) => _fn();

        public Task<SyncRunResult> PullAsync(CancellationToken ct = default) =>
            Task.FromResult(new SyncRunResult { Started = true, PullCompleted = true });
    }

    private sealed class NoopSyncApi : ISyncApiClient
    {
        public Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl,
            string? cookieHeader,
            ClientSyncPushRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new ClientSyncPushResponse());

        public Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl,
            string? cookieHeader,
            ClientSyncPullRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new ClientSyncPullResponse());
    }

    private sealed class StaticCookie : ISyncAuthCookieProvider
    {
        private readonly string? _cookie;

        public StaticCookie(string? cookie) => _cookie = cookie;

        public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default) =>
            Task.FromResult(_cookie);
    }
}
