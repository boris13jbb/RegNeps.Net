using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

/// <summary>FASE 2D.6 — Delete offline UI/Outbox/SyncEngine.</summary>
public sealed class OfflineOfflineDeleteTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-offline-delete-" + Guid.NewGuid().ToString("N"));
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

    private LocalSyncDbContext CreateContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<LocalSyncDbContext>()
            .UseSqlite($"Data Source={_dbPath}");
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new LocalSyncDbContext(builder.Options);
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
            string? cookie = "RegNeps.Auth=t")
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
            OfflineStoreConstants.DeleteRecordsPermission,
            "ViewRecords"
        ];

        await sessions.UpsertUxSnapshotAsync(
            userId, userId + "@test", role, permissions, "http://localhost:5080", TimeSpan.FromHours(72));

        db.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = 0 });
        await db.SaveChangesAsync();
        return (capture, sessions, engine, api);
    }

    private static async Task<(LocalNepRecord Record, string CreateClientOpId)> SeedSyncedAsync(
        OfflineCaptureService capture,
        LocalSyncDbContext db,
        string telar = "100",
        double neps = 18,
        string stamp = "stamp-v1",
        string? ownerUserId = null)
    {
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = telar,
            Neps = neps
        });
        var record = await db.LocalNepRecords.FirstAsync(r => r.Id == created.Record.Id);
        var serverId = Guid.NewGuid();
        record.ServerRecordId = serverId;
        record.ConcurrencyStamp = stamp;
        record.SyncStatus = LocalSyncStatus.Synced;
        if (!string.IsNullOrWhiteSpace(ownerUserId))
        {
            record.UserId = ownerUserId;
        }

        (await db.PendingOperations.FirstAsync(o => o.Id == created.Operation.Id)).Status =
            PendingOperationStatus.Synced;
        await db.SaveChangesAsync();
        return (record, created.Operation.ClientOperationId);
    }

    [Fact]
    public async Task Delete_Persists_Outbox_With_New_ClientOp_And_ExpectedStamp()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var (record, createOp) = await SeedSyncedAsync(capture, db);

        var result = await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest
        {
            LocalRecordId = record.Id
        });

        Assert.Equal(OfflineOperationType.DeleteRecord, result.Operation.OperationType);
        Assert.Equal(PendingOperationStatus.Pending, result.Operation.Status);
        Assert.NotEqual(createOp, result.Operation.ClientOperationId);
        Assert.Equal("stamp-v1", result.Operation.ExpectedConcurrencyStamp);
        Assert.Equal(record.ServerRecordId, result.Operation.TargetServerRecordId);
        Assert.True(result.Record.IsDeleted);
        Assert.Equal(LocalSyncStatus.PendingSync, result.Record.SyncStatus);
        Assert.Equal("stamp-v1", (await db.LocalNepRecords.SingleAsync()).ConcurrencyStamp);

        var payload = JsonSerializer.Deserialize<DeleteRecordPayload>(
            result.Operation.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal(record.ServerRecordId, payload!.EntityId);
    }

    [Fact]
    public async Task Delete_Atomic_Rollback_Leaves_Neither()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var (record, _) = await SeedSyncedAsync(capture, db, telar: "KEEP");
        var localId = record.Id;

        await using (var failDb = CreateContext(new FailOnPendingOperationInsertInterceptor()))
        {
            var sessions = new OfflineSessionService(failDb, new MemorySecureAuthMaterialStore());
            var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-fail"));
            var failCapture = new OfflineCaptureService(failDb, sessions, devices);
            await sessions.UpsertUxSnapshotAsync(
                "user-a", "a", "Operario",
                [
                    OfflineStoreConstants.CaptureRecordsPermission,
                    OfflineStoreConstants.DeleteRecordsPermission
                ],
                "http://localhost:5080",
                TimeSpan.FromHours(72));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                failCapture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = localId }));
        }

        await using var verify = CreateContext();
        var local = await verify.LocalNepRecords.SingleAsync(r => r.Id == localId);
        Assert.False(local.IsDeleted);
        Assert.Equal("KEEP", local.Telar);
        Assert.Equal(0, await verify.PendingOperations.CountAsync(o =>
            o.OperationType == OfflineOperationType.DeleteRecord));
    }

    [Fact]
    public async Task Double_Delete_Blocked_By_EntityId()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var (record, _) = await SeedSyncedAsync(capture, db);

        await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = record.Id });
        var elig = await capture.GetDeleteEligibilityAsync(record.Id);
        Assert.False(elig.CanDelete);
        Assert.Equal(OfflineDeleteBlockReason.Deleted, elig.Reason);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = record.Id }));
        Assert.Equal(1, await db.PendingOperations.CountAsync(o =>
            o.OperationType == OfflineOperationType.DeleteRecord));
    }

    [Fact]
    public async Task Create_Pending_And_Update_Pending_Block_Delete()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 18 });
        Assert.Equal(OfflineDeleteBlockReason.CreateStillPending,
            (await capture.GetDeleteEligibilityAsync(created.Record.Id)).Reason);

        var (synced, _) = await SeedSyncedAsync(capture, db, telar: "2");
        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = synced.Id,
            Telar = "2",
            Neps = 19
        });
        Assert.Equal(OfflineDeleteBlockReason.MutationAlreadyPending,
            (await capture.GetDeleteEligibilityAsync(synced.Id)).Reason);
    }

    [Fact]
    public async Task Conflict_Record_Cannot_Be_Deleted()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var (record, _) = await SeedSyncedAsync(capture, db);
        record.SyncStatus = LocalSyncStatus.Conflict;
        await db.SaveChangesAsync();

        var elig = await capture.GetDeleteEligibilityAsync(record.Id);
        Assert.False(elig.CanDelete);
        Assert.Equal(OfflineDeleteBlockReason.ConflictRequiresReview, elig.Reason);
    }

    [Fact]
    public async Task Delete_Accepted_Then_Pull_Tombstone_No_Resurrection()
    {
        await using var db = CreateContext();
        var (capture, _, engine, api) = await BootAsync(db);
        var (record, _) = await SeedSyncedAsync(capture, db, stamp: "s0");
        var serverId = record.ServerRecordId!.Value;

        var deleted = await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest
        {
            LocalRecordId = record.Id
        });

        api.PushHandler = req =>
        {
            Assert.Single(req.Operations);
            Assert.Equal(SyncConstants.OperationDeleteRecord, req.Operations[0].OperationType);
            Assert.Equal("s0", req.Operations[0].ExpectedConcurrencyStamp);
            return AcceptedPush(req, serverId, "s0");
        };
        api.PullHandler = req => req.Cursor > 0
            ? EmptyPull(req.Cursor)
            : new ClientSyncPullResponse
            {
                NextCursor = 1,
                HasMore = false,
                Changes =
                [
                    new ClientSyncChangeDto
                    {
                        Sequence = 1,
                        EntityId = serverId,
                        ChangeType = SyncConstants.ChangeRecordDeleted,
                        EntityType = SyncConstants.EntityNepRecord,
                        OccurredAtUtc = DateTime.UtcNow,
                        Payload = StableJson(new ClientNepRecordDeletedSnapshot
                        {
                            Id = serverId,
                            OwnerUserId = "user-a",
                            DeletedAtUtc = DateTime.UtcNow,
                            LastConcurrencyStamp = "s0"
                        })
                    }
                ]
            };

        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations
            .SingleAsync(o => o.ClientOperationId == deleted.Operation.ClientOperationId)).Status);
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.True(local.IsDeleted);
        Assert.Equal(LocalSyncStatus.Synced, local.SyncStatus);
        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
    }

    [Fact]
    public async Task Delete_Duplicate_Is_Idempotent()
    {
        await using var db = CreateContext();
        var (capture, _, engine, api) = await BootAsync(db);
        var (record, _) = await SeedSyncedAsync(capture, db);
        var serverId = record.ServerRecordId!.Value;
        var deleted = await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest
        {
            LocalRecordId = record.Id
        });

        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results =
            [
                new ClientSyncOperationResultDto
                {
                    ClientOperationId = deleted.Operation.ClientOperationId,
                    Result = ClientSyncResultNames.Duplicate,
                    EntityId = serverId,
                    ConcurrencyStamp = "s0"
                }
            ]
        };
        api.PullHandler = _ => EmptyPull(0);

        var run = await engine.SyncAsync();
        Assert.Equal(1, run.PushedDuplicate);
        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
        Assert.True((await db.LocalNepRecords.SingleAsync()).IsDeleted);
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations
            .SingleAsync(o => o.ClientOperationId == deleted.Operation.ClientOperationId)).Status);
    }

    [Fact]
    public async Task Delete_Stale_Stamp_Conflict_Preserves_Local_Delete_Intent()
    {
        await using var db = CreateContext();
        var (capture, _, engine, api) = await BootAsync(db);
        var (record, _) = await SeedSyncedAsync(capture, db, stamp: "old");
        var serverId = record.ServerRecordId!.Value;

        await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = record.Id });

        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results =
            [
                new ClientSyncOperationResultDto
                {
                    ClientOperationId = req.Operations[0].ClientOperationId,
                    Result = ClientSyncResultNames.Conflict,
                    ErrorCode = "CONFLICT",
                    EntityId = serverId,
                    ServerConcurrencyStamp = "new",
                    ServerSnapshot = StableJson(new ClientNepRecordSnapshot
                    {
                        Id = serverId,
                        Telar = "SERVER",
                        Neps = 10,
                        ConcurrencyStamp = "new"
                    })
                }
            ]
        };
        api.PullHandler = _ => EmptyPull(0);

        await engine.SyncAsync();
        var op = await db.PendingOperations.SingleAsync(o => o.OperationType == OfflineOperationType.DeleteRecord);
        Assert.Equal(PendingOperationStatus.Conflict, op.Status);
        Assert.Equal("new", op.ConflictServerConcurrencyStamp);
        Assert.Contains("SERVER", op.ConflictServerSnapshotJson);
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.True(local.IsDeleted);
        Assert.Equal(LocalSyncStatus.Conflict, local.SyncStatus);

        api.PushCalls = 0;
        await engine.SyncAsync();
        Assert.Equal(0, api.PushCalls);
    }

    [Fact]
    public async Task Delete_Forbidden_Is_SyncError()
    {
        await using var db = CreateContext();
        var (capture, _, engine, api) = await BootAsync(db);
        var (record, _) = await SeedSyncedAsync(capture, db);
        await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = record.Id });

        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results =
            [
                new ClientSyncOperationResultDto
                {
                    ClientOperationId = req.Operations[0].ClientOperationId,
                    Result = ClientSyncResultNames.Forbidden,
                    ErrorCode = "DELETE_FORBIDDEN"
                }
            ]
        };
        api.PullHandler = _ => EmptyPull(0);

        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.SyncError, (await db.PendingOperations
            .SingleAsync(o => o.OperationType == OfflineOperationType.DeleteRecord)).Status);
        Assert.True((await db.LocalNepRecords.SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task SeesAll_Can_Delete_Other_Users_Record_But_Not_Second_Delete()
    {
        await using var db = CreateContext();
        var (capture, sessions, _, _) = await BootAsync(db, userId: "owner");
        var (record, _) = await SeedSyncedAsync(capture, db, ownerUserId: "owner");

        await sessions.ClearUxSnapshotAsync();
        await sessions.UpsertUxSnapshotAsync(
            "boss", "boss@test", "Supervisor",
            [
                OfflineStoreConstants.CaptureRecordsPermission,
                OfflineStoreConstants.DeleteRecordsPermission
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        Assert.True((await capture.GetDeleteEligibilityAsync(record.Id)).CanDelete);
        await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = record.Id });

        await sessions.ClearUxSnapshotAsync();
        await sessions.UpsertUxSnapshotAsync(
            "admin2", "a2@test", "Admin",
            [
                OfflineStoreConstants.DeleteRecordsPermission
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        var elig = await capture.GetDeleteEligibilityAsync(record.Id);
        Assert.False(elig.CanDelete);
        Assert.Equal(1, await db.PendingOperations.CountAsync(o =>
            o.OperationType == OfflineOperationType.DeleteRecord));
    }

    [Fact]
    public async Task Other_User_Without_SeesAll_Cannot_Delete()
    {
        await using var db = CreateContext();
        var (capture, sessions, _, _) = await BootAsync(db, userId: "owner");
        var (record, _) = await SeedSyncedAsync(capture, db, ownerUserId: "owner");

        await sessions.ClearUxSnapshotAsync();
        await sessions.UpsertUxSnapshotAsync(
            "stranger", "s@test", "Operario",
            [
                OfflineStoreConstants.DeleteRecordsPermission
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        Assert.Equal(OfflineDeleteBlockReason.NotOwner,
            (await capture.GetDeleteEligibilityAsync(record.Id)).Reason);
    }

    [Fact]
    public async Task Restart_Persists_Pending_Delete()
    {
        Guid localId;
        Guid opId;
        string clientOp;
        string stamp;
        Guid serverId;

        await using (var db = CreateContext())
        {
            var (capture, _, _, _) = await BootAsync(db);
            var (record, _) = await SeedSyncedAsync(capture, db, stamp: "persist");
            localId = record.Id;
            serverId = record.ServerRecordId!.Value;
            var deleted = await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest
            {
                LocalRecordId = record.Id
            });
            opId = deleted.Operation.Id;
            clientOp = deleted.Operation.ClientOperationId;
            stamp = deleted.Operation.ExpectedConcurrencyStamp!;
        }

        await using var reopen = CreateContext();
        var local = await reopen.LocalNepRecords.SingleAsync(r => r.Id == localId);
        var op = await reopen.PendingOperations.SingleAsync(o => o.Id == opId);
        Assert.True(local.IsDeleted);
        Assert.Equal(PendingOperationStatus.Pending, op.Status);
        Assert.Equal(clientOp, op.ClientOperationId);
        Assert.Equal(stamp, op.ExpectedConcurrencyStamp);
        Assert.Equal(serverId, op.TargetServerRecordId);
    }

    [Fact]
    public async Task Logout_Keeps_Pending_Delete()
    {
        await using var db = CreateContext();
        var (capture, sessions, engine, api) = await BootAsync(db);
        var (record, _) = await SeedSyncedAsync(capture, db);
        await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = record.Id });
        await sessions.ClearUxSnapshotAsync();

        Assert.Equal(1, await db.PendingOperations.CountAsync(o =>
            o.OperationType == OfflineOperationType.DeleteRecord
            && o.Status == PendingOperationStatus.Pending));
        var run = await engine.SyncAsync();
        Assert.True(run.SessionMissingOrExpired);
        Assert.Equal(0, api.PushCalls);
    }

    [Fact]
    public async Task No_DeleteRecords_Permission_Blocks()
    {
        await using var db = CreateContext();
        await ClearAsync(db);
        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "noperm"));
        var capture = new OfflineCaptureService(db, sessions, devices);
        await sessions.UpsertUxSnapshotAsync(
            "user-a", "a", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission, OfflineStoreConstants.EditRecordsPermission],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 18 });
        var rec = await db.LocalNepRecords.FirstAsync();
        rec.ServerRecordId = Guid.NewGuid();
        rec.ConcurrencyStamp = "s";
        rec.SyncStatus = LocalSyncStatus.Synced;
        (await db.PendingOperations.SingleAsync()).Status = PendingOperationStatus.Synced;
        await db.SaveChangesAsync();

        Assert.Equal(OfflineDeleteBlockReason.NoDeletePermission,
            (await capture.GetDeleteEligibilityAsync(created.Record.Id)).Reason);
    }

    [Fact]
    public async Task Update_After_Local_Delete_Is_Blocked()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var (record, _) = await SeedSyncedAsync(capture, db);
        await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = record.Id });

        var elig = await capture.GetEditEligibilityAsync(record.Id);
        Assert.False(elig.CanEdit);
        Assert.Equal(OfflineEditBlockReason.Deleted, elig.Reason);
    }

    private static ClientSyncPushResponse AcceptedPush(ClientSyncPushRequest req, Guid entityId, string stamp) =>
        new()
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.Accepted,
                EntityId = entityId,
                ConcurrencyStamp = stamp
            }).ToList()
        };

    private static ClientSyncPullResponse EmptyPull(long cursor) =>
        new() { NextCursor = cursor, HasMore = false, Changes = [] };

    private static JsonElement StableJson<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, ClientSyncJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
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
            string serverBaseUrl, string? cookieHeader, ClientSyncPushRequest request, CancellationToken ct = default)
        {
            PushCalls++;
            return Task.FromResult(PushHandler?.Invoke(request) ?? new ClientSyncPushResponse());
        }

        public Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPullRequest request, CancellationToken ct = default) =>
            Task.FromResult(PullHandler?.Invoke(request) ?? EmptyPull(request.Cursor));
    }

    private sealed class FailOnPendingOperationInsertInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (HasAddedPending(eventData))
            {
                throw new InvalidOperationException("forced outbox insert failure");
            }

            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (HasAddedPending(eventData))
            {
                throw new InvalidOperationException("forced outbox insert failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static bool HasAddedPending(DbContextEventData eventData) =>
            eventData.Context?.ChangeTracker.Entries()
                .Any(e => e.Entity is PendingOperation && e.State == EntityState.Added) == true;
    }
}
