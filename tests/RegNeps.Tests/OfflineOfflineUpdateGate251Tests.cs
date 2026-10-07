using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.Domain.Sync;
using RegNeps.OfflineStore;
using RegNeps.OfflineStore.Device;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;
using RegNeps.OfflineStore.Sync.Contracts;

namespace RegNeps.Tests;

/// <summary>
/// FASE 2D.5.1 — Gate: huecos de regresión Offline Update (ownership, SeesAll,
/// tombstone, re-login, sesión expirada, bloqueo cross-user).
/// </summary>
public sealed class OfflineOfflineUpdateGate251Tests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-2d51-gate-" + Guid.NewGuid().ToString("N"));
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

    private async Task ClearAsync(LocalSyncDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM PendingOperations");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalNepRecords");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalSessions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SyncStates");
        db.ChangeTracker.Clear();
    }

    private async Task<(OfflineCaptureService Capture, OfflineSessionService Sessions, SyncEngine Engine, FakeApi Api)>
        BootAsync(
            LocalSyncDbContext db,
            string userId = "user-a",
            string role = "Operario",
            string[]? permissions = null,
            string? cookie = "RegNeps.Auth=t",
            TimeSpan? ttl = null)
    {
        await ClearAsync(db);
        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-" + Guid.NewGuid().ToString("N")[..8]));
        var capture = new OfflineCaptureService(db, sessions, devices);
        var api = new FakeApi();
        var engine = new SyncEngine(db, sessions, devices, api, new StaticCookie(cookie));

        permissions ??=
        [
            OfflineStoreConstants.CaptureRecordsPermission,
            OfflineStoreConstants.EditRecordsPermission,
            "ViewRecords"
        ];

        await sessions.UpsertUxSnapshotAsync(
            userId,
            userId + "@test",
            role,
            permissions,
            "http://localhost:5080",
            ttl ?? TimeSpan.FromHours(72));

        db.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = 7 });
        await db.SaveChangesAsync();
        return (capture, sessions, engine, api);
    }

    private static async Task<LocalNepRecord> SeedSyncedOwnedAsync(
        OfflineCaptureService capture,
        LocalSyncDbContext db,
        string ownerUserId,
        string telar = "100",
        double neps = 18,
        string stamp = "stamp-v1")
    {
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = telar,
            Neps = neps
        });
        var record = await db.LocalNepRecords.FirstAsync(r => r.Id == created.Record.Id);
        record.UserId = ownerUserId;
        record.ServerRecordId = Guid.NewGuid();
        record.ConcurrencyStamp = stamp;
        record.SyncStatus = LocalSyncStatus.Synced;
        (await db.PendingOperations.FirstAsync(o => o.Id == created.Operation.Id)).Status =
            PendingOperationStatus.Synced;
        await db.SaveChangesAsync();
        return record;
    }

    [Fact]
    public async Task NotOwner_Without_SeesAll_Cannot_Edit()
    {
        await using var db = CreateContext();
        var (capture, sessions, _, _) = await BootAsync(db, userId: "owner");
        var record = await SeedSyncedOwnedAsync(capture, db, "owner");

        await sessions.ClearUxSnapshotAsync();
        await sessions.UpsertUxSnapshotAsync(
            "stranger",
            "stranger@test",
            "Operario",
            [
                OfflineStoreConstants.CaptureRecordsPermission,
                OfflineStoreConstants.EditRecordsPermission
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        var elig = await capture.GetEditEligibilityAsync(record.Id);
        Assert.False(elig.CanEdit);
        Assert.Equal(OfflineEditBlockReason.NotOwner, elig.Reason);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
            {
                LocalRecordId = record.Id,
                Telar = "X",
                Neps = 19
            }));
    }

    [Fact]
    public async Task SeesAll_Supervisor_Can_Edit_Other_Users_Synced_Record()
    {
        await using var db = CreateContext();
        var (capture, sessions, _, _) = await BootAsync(db, userId: "owner");
        var record = await SeedSyncedOwnedAsync(capture, db, "owner", telar: "OWN");

        await sessions.ClearUxSnapshotAsync();
        await sessions.UpsertUxSnapshotAsync(
            "boss",
            "boss@test",
            "Supervisor",
            [
                OfflineStoreConstants.CaptureRecordsPermission,
                OfflineStoreConstants.EditRecordsPermission,
                "ViewRecords"
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        var elig = await capture.GetEditEligibilityAsync(record.Id);
        Assert.True(elig.CanEdit);

        var updated = await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "BOSS",
            Neps = 19
        });
        Assert.Equal(OfflineOperationType.UpdateRecord, updated.Operation.OperationType);
        Assert.Equal("boss", updated.Operation.UserId);
        Assert.Equal("BOSS", (await db.LocalNepRecords.SingleAsync()).Telar);
    }

    [Fact]
    public async Task Second_Update_Blocked_Even_If_Pending_Belongs_To_Other_User()
    {
        await using var db = CreateContext();
        var (capture, sessions, _, _) = await BootAsync(db, userId: "user-a");
        var record = await SeedSyncedOwnedAsync(capture, db, "user-a");

        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "A1",
            Neps = 20
        });

        await sessions.ClearUxSnapshotAsync();
        await sessions.UpsertUxSnapshotAsync(
            "boss",
            "boss@test",
            "Supervisor",
            [
                OfflineStoreConstants.CaptureRecordsPermission,
                OfflineStoreConstants.EditRecordsPermission
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        var elig = await capture.GetEditEligibilityAsync(record.Id);
        Assert.False(elig.CanEdit);
        Assert.Equal(OfflineEditBlockReason.UpdateAlreadyPending, elig.Reason);
        Assert.Equal(1, await db.PendingOperations.CountAsync(o =>
            o.OperationType == OfflineOperationType.UpdateRecord));
    }

    [Fact]
    public async Task Deleted_Local_Record_Cannot_Be_Edited()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var record = await SeedSyncedOwnedAsync(capture, db, "user-a");
        record.IsDeleted = true;
        await db.SaveChangesAsync();

        var elig = await capture.GetEditEligibilityAsync(record.Id);
        Assert.False(elig.CanEdit);
        Assert.Equal(OfflineEditBlockReason.Deleted, elig.Reason);
    }

    [Fact]
    public async Task Update_Push_EntityDeleted_Marks_Conflict_Does_Not_Recreate()
    {
        await using var db = CreateContext();
        var (capture, _, engine, api) = await BootAsync(db);
        var record = await SeedSyncedOwnedAsync(capture, db, "user-a", stamp: "s0");
        var serverId = record.ServerRecordId!.Value;

        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "GONE",
            Neps = 22
        });

        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results =
            [
                new ClientSyncOperationResultDto
                {
                    ClientOperationId = req.Operations[0].ClientOperationId,
                    Result = ClientSyncResultNames.Invalid,
                    ErrorCode = "ENTITY_DELETED",
                    EntityId = serverId
                }
            ]
        };
        api.PullHandler = _ => new ClientSyncPullResponse
        {
            NextCursor = 0,
            HasMore = false,
            Changes = []
        };

        await engine.SyncAsync();
        var op = await db.PendingOperations.SingleAsync(o => o.OperationType == OfflineOperationType.UpdateRecord);
        Assert.Equal(PendingOperationStatus.Conflict, op.Status);
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.True(local.IsDeleted);
        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
        Assert.Equal("GONE", local.Telar); // no recrea ni limpia la edición
    }

    [Fact]
    public async Task Relogin_Same_User_Pushes_Pending_Update()
    {
        await using var db = CreateContext();
        var (capture, sessions, engine, api) = await BootAsync(db, userId: "user-a");
        var record = await SeedSyncedOwnedAsync(capture, db, "user-a", stamp: "s0");
        var serverId = record.ServerRecordId!.Value;

        var updated = await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "REL",
            Neps = 19
        });

        await sessions.ClearUxSnapshotAsync();
        Assert.Null(await sessions.GetValidSessionAsync());

        await sessions.UpsertUxSnapshotAsync(
            "user-a",
            "alice",
            "Operario",
            [
                OfflineStoreConstants.CaptureRecordsPermission,
                OfflineStoreConstants.EditRecordsPermission
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        api.PushHandler = req =>
        {
            Assert.Single(req.Operations);
            Assert.Equal(updated.Operation.ClientOperationId, req.Operations[0].ClientOperationId);
            Assert.Equal(SyncConstants.OperationUpdateRecord, req.Operations[0].OperationType);
            return new ClientSyncPushResponse
            {
                Results =
                [
                    new ClientSyncOperationResultDto
                    {
                        ClientOperationId = req.Operations[0].ClientOperationId,
                        Result = ClientSyncResultNames.Accepted,
                        EntityId = serverId,
                        ConcurrencyStamp = "s1"
                    }
                ]
            };
        };
        api.PullHandler = _ => new ClientSyncPullResponse
        {
            NextCursor = 0,
            HasMore = false,
            Changes = []
        };

        var run = await engine.SyncAsync();
        Assert.Equal(1, run.PushedAccepted);
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations
            .SingleAsync(o => o.ClientOperationId == updated.Operation.ClientOperationId)).Status);
    }

    [Fact]
    public async Task Expired_LocalSession_Blocks_Update_And_Is_Not_Valid_Auth()
    {
        await using var db = CreateContext();
        var (capture, sessions, engine, api) = await BootAsync(db);
        var record = await SeedSyncedOwnedAsync(capture, db, "user-a");

        var sessionRow = await db.LocalSessions.SingleAsync();
        sessionRow.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        Assert.Null(await sessions.GetValidSessionAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
            {
                LocalRecordId = record.Id,
                Telar = "E",
                Neps = 19
            }));

        var run = await engine.SyncAsync();
        Assert.True(run.SessionMissingOrExpired);
        Assert.Equal(0, api.PushCalls);
    }

    [Fact]
    public async Task Restart_Preserves_SyncState_Cursor()
    {
        await using (var db = CreateContext())
        {
            await BootAsync(db);
            Assert.Equal(7, (await db.SyncStates.SingleAsync()).LastPulledSequence);
        }

        await using var reopen = CreateContext();
        Assert.Equal(7, (await reopen.SyncStates.SingleAsync()).LastPulledSequence);
    }

    [Fact]
    public void AlertasActivas_Does_Not_Change_Quality_Classification()
    {
        var active = new AlertConfig { AlertasActivas = true };
        var inactive = new AlertConfig { AlertasActivas = false };

        Assert.Equal(AlertLevel.Ok, AlertEvaluator.GetLevel(18, active));
        Assert.Equal(AlertLevel.Ok, AlertEvaluator.GetLevel(18, inactive));
        Assert.Equal(AlertLevel.Mention, AlertEvaluator.GetLevel(19, active));
        Assert.Equal(AlertLevel.Mention, AlertEvaluator.GetLevel(19, inactive));
        Assert.Equal(AlertLevel.CriticalAdjustment, AlertEvaluator.GetLevel(46, active));
        Assert.Equal(AlertLevel.CriticalAdjustment, AlertEvaluator.GetLevel(46, inactive));
        Assert.Equal(AlertLevel.SecondQuality, AlertEvaluator.GetLevel(55, active));
        Assert.Equal(AlertLevel.SecondQuality, AlertEvaluator.GetLevel(55, inactive));
    }

    [Fact]
    public async Task Update_ClientOperationReused_Is_Permanent_SyncError()
    {
        await using var db = CreateContext();
        var (capture, _, engine, api) = await BootAsync(db);
        var record = await SeedSyncedOwnedAsync(capture, db, "user-a");

        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "R",
            Neps = 20
        });

        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results =
            [
                new ClientSyncOperationResultDto
                {
                    ClientOperationId = req.Operations[0].ClientOperationId,
                    Result = ClientSyncResultNames.Invalid,
                    ErrorCode = "CLIENT_OPERATION_REUSED",
                    Message = "reused for different op"
                }
            ]
        };
        api.PullHandler = _ => new ClientSyncPullResponse
        {
            NextCursor = 0,
            HasMore = false,
            Changes = []
        };

        await engine.SyncAsync();
        var op = await db.PendingOperations.SingleAsync(o => o.OperationType == OfflineOperationType.UpdateRecord);
        Assert.Equal(PendingOperationStatus.SyncError, op.Status);
        Assert.Equal("R", (await db.LocalNepRecords.SingleAsync()).Telar);
    }

    private sealed class StaticCookie : ISyncAuthCookieProvider
    {
        private readonly string? _cookie;
        public StaticCookie(string? cookie) => _cookie = cookie;
        public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default) =>
            Task.FromResult(_cookie);
    }

    private sealed class FakeApi : ISyncApiClient
    {
        public int PushCalls;
        public Func<ClientSyncPushRequest, ClientSyncPushResponse>? PushHandler;
        public Func<ClientSyncPullRequest, ClientSyncPullResponse>? PullHandler;

        public Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl,
            string? cookieHeader,
            ClientSyncPushRequest request,
            CancellationToken ct = default)
        {
            PushCalls++;
            return Task.FromResult(PushHandler?.Invoke(request) ?? new ClientSyncPushResponse());
        }

        public Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl,
            string? cookieHeader,
            ClientSyncPullRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(PullHandler?.Invoke(request)
                            ?? new ClientSyncPullResponse { NextCursor = request.Cursor, HasMore = false, Changes = [] });
    }
}
