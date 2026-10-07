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

/// <summary>FASE 2D.4 — inventario Outbox, detalle, retry seguro y aislamiento multiusuario.</summary>
public sealed class OfflineOperationsUxTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-ops4-" + Guid.NewGuid().ToString("N"));
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
        catch { /* ignore */ }

        return Task.CompletedTask;
    }

    private LocalSyncDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LocalSyncDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new LocalSyncDbContext(options);
    }

    private async Task<(OfflineCaptureService Capture, OfflineSessionService Sessions, OfflineOperationsUxService Ops)>
        SeedUserAsync(LocalSyncDbContext db, string userId, string username = "alice")
    {
        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "d-" + Guid.NewGuid().ToString("N")[..8]));
        var capture = new OfflineCaptureService(db, sessions, devices);
        var ops = new OfflineOperationsUxService(db);

        await sessions.UpsertUxSnapshotAsync(
            userId,
            username,
            "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        if (!await db.SyncStates.AnyAsync(s => s.Id == 1))
        {
            db.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = 0 });
            await db.SaveChangesAsync();
        }

        return (capture, sessions, ops);
    }

    private async Task ResetAsync(LocalSyncDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM PendingOperations");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalNepRecords");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalSessions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SyncStates");
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task List_Filters_Pending_Synced_Error_Conflict()
    {
        await using var db = CreateContext();
        await ResetAsync(db);
        var (capture, _, ops) = await SeedUserAsync(db, "user-a");

        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "2", Neps = 11 });
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "3", Neps = 12 });
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "4", Neps = 13 });

        var allOps = await db.PendingOperations.OrderBy(o => o.CreatedAtUtc).ToListAsync();
        allOps[1].Status = PendingOperationStatus.Synced;
        allOps[2].Status = PendingOperationStatus.SyncError;
        allOps[2].LastServerErrorCode = "MISSING_RESULT";
        allOps[3].Status = PendingOperationStatus.Conflict;
        await db.SaveChangesAsync();

        Assert.Single(await ops.ListAsync("user-a", OfflineOperationListFilter.Pending));
        Assert.Single(await ops.ListAsync("user-a", OfflineOperationListFilter.Synced));
        Assert.Single(await ops.ListAsync("user-a", OfflineOperationListFilter.SyncError));
        Assert.Single(await ops.ListAsync("user-a", OfflineOperationListFilter.Conflict));
        Assert.Equal(4, (await ops.ListAsync("user-a", OfflineOperationListFilter.All)).Count);
    }

    [Fact]
    public async Task Detail_Create_Shows_ServerId_Pending_And_Distinct_After_Accept()
    {
        await using var db = CreateContext();
        await ResetAsync(db);
        var (capture, sessions, ops) = await SeedUserAsync(db, "user-a");
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "T1", Neps = 18 });

        var detailPending = await ops.GetDetailAsync("user-a", created.Operation.Id);
        Assert.NotNull(detailPending);
        Assert.Contains("servidor aún no asignado", detailPending!.ServerIdDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(created.Record.Id.ToString("D"), detailPending.LocalIdDisplay);
        Assert.Equal(created.Operation.ClientOperationId, detailPending.ClientOperationId);
        Assert.DoesNotContain("PayloadJson", detailPending.LocalRecordSummary);

        var serverId = Guid.NewGuid();
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "acc"));
        var api = new ScriptApi(
            req => new ClientSyncPushResponse
            {
                Results =
                [
                    new ClientSyncOperationResultDto
                    {
                        ClientOperationId = created.Operation.ClientOperationId,
                        Result = ClientSyncResultNames.Accepted,
                        EntityId = serverId,
                        ConcurrencyStamp = "c1"
                    }
                ]
            },
            _ => new ClientSyncPullResponse { NextCursor = 0 });
        var engine = new SyncEngine(db, sessions, devices, api, new Cookie("RegNeps.Auth=x"));
        await engine.SyncAsync();

        var detailSynced = await ops.GetDetailAsync("user-a", created.Operation.Id);
        Assert.NotNull(detailSynced);
        Assert.Equal(PendingOperationStatus.Synced, detailSynced!.Status);
        Assert.Equal(serverId.ToString("D"), detailSynced.ServerIdDisplay);
        Assert.NotEqual(detailSynced.LocalIdDisplay, detailSynced.ServerIdDisplay);
        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
    }

    [Fact]
    public async Task Detail_Conflict_Shows_Snapshot_Does_Not_Mutate_Local()
    {
        await using var db = CreateContext();
        await ResetAsync(db);
        var (capture, _, ops) = await SeedUserAsync(db, "user-a");
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "LOC", Neps = 20 });
        var op = await db.PendingOperations.SingleAsync();
        op.Status = PendingOperationStatus.Conflict;
        op.ConflictServerConcurrencyStamp = "srv";
        op.ConflictServerSnapshotJson = JsonSerializer.Serialize(new ClientNepRecordSnapshot
        {
            Telar = "SRV",
            Neps = 55,
            QualityLabel = "OK",
            ConcurrencyStamp = "srv"
        }, ClientSyncJson.Options);
        created.Record.Telar = "LOC";
        created.Record.Neps = 20;
        await db.SaveChangesAsync();

        var detail = await ops.GetDetailAsync("user-a", op.Id);
        Assert.NotNull(detail);
        Assert.True(detail!.RequiresReview);
        Assert.Equal(OfflineOperationUxAction.RequiresReview, detail.PrimaryAction);
        Assert.Contains("LOC", detail.LocalRecordSummary);
        Assert.Contains("SRV", detail.ServerRecordSummary);
        Assert.Equal("srv", detail.ConflictStampDisplay);

        var local = await db.LocalNepRecords.SingleAsync();
        Assert.Equal("LOC", local.Telar);
        Assert.Equal(20, local.Neps);
    }

    [Fact]
    public async Task Retry_Recoverable_SyncError_Same_ClientOperationId_No_New_Op()
    {
        await using var db = CreateContext();
        await ResetAsync(db);
        var (capture, sessions, ops) = await SeedUserAsync(db, "user-a");
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        var clientOpId = created.Operation.ClientOperationId;
        var op = await db.PendingOperations.SingleAsync();
        op.Status = PendingOperationStatus.SyncError;
        op.LastServerErrorCode = "MISSING_RESULT";
        await db.SaveChangesAsync();

        Assert.Equal(OfflineOperationUxAction.RetrySync, OfflineOperationsUxService.ClassifyAction(op));
        var (prepared, _) = await ops.TryPrepareRetryAsync("user-a", op.Id);
        Assert.True(prepared);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);
        Assert.Equal(clientOpId, (await db.PendingOperations.SingleAsync()).ClientOperationId);
        Assert.Equal(1, await db.PendingOperations.CountAsync());

        var serverId = Guid.NewGuid();
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "retry"));
        var api = new ScriptApi(
            req =>
            {
                Assert.Single(req.Operations);
                Assert.Equal(clientOpId, req.Operations[0].ClientOperationId);
                return new ClientSyncPushResponse
                {
                    Results =
                    [
                        new ClientSyncOperationResultDto
                        {
                            ClientOperationId = clientOpId,
                            Result = ClientSyncResultNames.Accepted,
                            EntityId = serverId,
                            ConcurrencyStamp = "ok"
                        }
                    ]
                };
            },
            _ => new ClientSyncPullResponse { NextCursor = 0 });

        var engine = new SyncEngine(db, sessions, devices, api, new Cookie("RegNeps.Auth=x"));
        var gate = new ManualSyncGate();
        var runner = new ManualSyncRunner(engine, gate);
        var (invoked, result) = await runner.TrySyncAsync();
        Assert.True(invoked);
        Assert.Equal(1, result!.PushedAccepted);
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations.SingleAsync()).Status);
        Assert.Equal(clientOpId, (await db.PendingOperations.SingleAsync()).ClientOperationId);
    }

    [Fact]
    public async Task Forbidden_Does_Not_Prepare_Useless_Retry()
    {
        await using var db = CreateContext();
        await ResetAsync(db);
        var (capture, _, ops) = await SeedUserAsync(db, "user-a");
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        var op = await db.PendingOperations.SingleAsync();
        op.Status = PendingOperationStatus.SyncError;
        op.LastServerErrorCode = "FORBIDDEN";
        await db.SaveChangesAsync();

        Assert.Equal(OfflineOperationUxAction.NoRetryPermanent, OfflineOperationsUxService.ClassifyAction(op));
        var (prepared, msg) = await ops.TryPrepareRetryAsync("user-a", op.Id);
        Assert.False(prepared);
        Assert.Contains("reintentando", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PendingOperationStatus.SyncError, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public void Classify_Unauthorized_Is_Relogin_Not_Network()
    {
        var op = new PendingOperation
        {
            Status = PendingOperationStatus.SyncError,
            LastServerErrorCode = "UNAUTHORIZED",
            LastError = "Sesión de servidor no válida (401)."
        };
        Assert.Equal(OfflineOperationUxAction.Relogin, OfflineOperationsUxService.ClassifyAction(op));
        Assert.DoesNotContain("red", OfflineOperationsUxService.ActionHint(OfflineOperationUxAction.Relogin),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conflict_Classify_RequiresReview_Not_Retry()
    {
        await using var db = CreateContext();
        await ResetAsync(db);
        var (capture, _, ops) = await SeedUserAsync(db, "user-a");
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        var op = await db.PendingOperations.SingleAsync();
        op.Status = PendingOperationStatus.Conflict;
        await db.SaveChangesAsync();

        Assert.Equal(OfflineOperationUxAction.RequiresReview, OfflineOperationsUxService.ClassifyAction(op));
        var (prepared, _) = await ops.TryPrepareRetryAsync("user-a", op.Id);
        Assert.False(prepared);
        Assert.Equal(PendingOperationStatus.Conflict, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Multiusuario_B_Does_Not_List_Or_Send_A()
    {
        await using var db = CreateContext();
        await ResetAsync(db);
        var (captureA, sessions, ops) = await SeedUserAsync(db, "user-a", "alice");
        await captureA.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "A", Neps = 10 });
        var opA = await db.PendingOperations.SingleAsync();
        var clientA = opA.ClientOperationId;

        await sessions.ClearUxSnapshotAsync();
        Assert.Equal(1, await db.PendingOperations.CountAsync()); // Outbox conservado

        var (captureB, sessionsB, opsB) = await SeedUserAsync(db, "user-b", "bob");
        await captureB.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "B", Neps = 11 });

        var listB = await opsB.ListAsync("user-b", OfflineOperationListFilter.All);
        Assert.Single(listB);
        Assert.DoesNotContain(listB, i => i.OperationId == opA.Id);
        Assert.Null(await opsB.GetDetailAsync("user-b", opA.Id));

        var pushSeen = new List<string>();
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "multi"));
        var api = new ScriptApi(
            req =>
            {
                pushSeen.AddRange(req.Operations.Select(o => o.ClientOperationId));
                return new ClientSyncPushResponse
                {
                    Results = req.Operations.Select(o => new ClientSyncOperationResultDto
                    {
                        ClientOperationId = o.ClientOperationId,
                        Result = ClientSyncResultNames.Accepted,
                        EntityId = Guid.NewGuid(),
                        ConcurrencyStamp = "x"
                    }).ToList()
                };
            },
            _ => new ClientSyncPullResponse { NextCursor = 0 });

        var engine = new SyncEngine(db, sessionsB, devices, api, new Cookie("RegNeps.Auth=x"));
        await engine.SyncAsync();

        Assert.DoesNotContain(clientA, pushSeen);
        Assert.Equal(PendingOperationStatus.Pending,
            (await db.PendingOperations.SingleAsync(o => o.ClientOperationId == clientA)).Status);
    }

    [Fact]
    public async Task Detail_Sanitizes_Error_No_Secrets()
    {
        await using var db = CreateContext();
        await ResetAsync(db);
        var (capture, _, ops) = await SeedUserAsync(db, "user-a");
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        var op = await db.PendingOperations.SingleAsync();
        op.Status = PendingOperationStatus.SyncError;
        op.LastError = "cookie=RegNeps.Auth=secret; at RegNeps.OfflineStore.Sync.SyncEngine";
        op.LastServerErrorCode = "UNAUTHORIZED";
        await db.SaveChangesAsync();

        var detail = await ops.GetDetailAsync("user-a", op.Id);
        Assert.NotNull(detail);
        Assert.DoesNotContain("RegNeps.Auth=", detail!.ErrorFriendly ?? "");
        Assert.DoesNotContain("at RegNeps.", detail.ErrorFriendly ?? "");
        Assert.Equal(OfflineOperationUxAction.Relogin, detail.PrimaryAction);
    }

    private sealed class ScriptApi : ISyncApiClient
    {
        private readonly Func<ClientSyncPushRequest, ClientSyncPushResponse> _push;
        private readonly Func<ClientSyncPullRequest, ClientSyncPullResponse> _pull;

        public ScriptApi(
            Func<ClientSyncPushRequest, ClientSyncPushResponse> push,
            Func<ClientSyncPullRequest, ClientSyncPullResponse> pull)
        {
            _push = push;
            _pull = pull;
        }

        public Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPushRequest request, CancellationToken ct = default) =>
            Task.FromResult(_push(request));

        public Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPullRequest request, CancellationToken ct = default) =>
            Task.FromResult(_pull(request));
    }

    private sealed class Cookie : ISyncAuthCookieProvider
    {
        private readonly string? _c;
        public Cookie(string? c) => _c = c;
        public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default) =>
            Task.FromResult(_c);
    }
}
