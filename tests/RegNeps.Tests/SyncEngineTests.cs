using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

/// <summary>FASE 2D.2: SyncEngine Push/Pull/Conflict/seguridad con API falsa.</summary>
public sealed class SyncEngineTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-syncengine-" + Guid.NewGuid().ToString("N"));
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

    private async Task<(SyncEngine Engine, OfflineCaptureService Capture, OfflineSessionService Sessions, FakeSyncApiClient Api, StaticCookieProvider Cookies)>
        CreateEngineAsync(
            LocalSyncDbContext db,
            string userId = "user-a",
            bool withSession = true,
            TimeSpan? ttl = null,
            string? cookie = "RegNeps.Auth=test")
    {
        // Aislar estado entre tests (mismo archivo SQLite de clase).
        await db.Database.ExecuteSqlRawAsync("DELETE FROM PendingOperations");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalNepRecords");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalSessions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SyncStates");
        db.ChangeTracker.Clear();

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "device-" + Guid.NewGuid().ToString("N")[..8]));
        var capture = new OfflineCaptureService(db, sessions, devices);
        var api = new FakeSyncApiClient();
        var cookies = new StaticCookieProvider(cookie);
        var engine = new SyncEngine(db, sessions, devices, api, cookies);

        if (withSession)
        {
            await sessions.UpsertUxSnapshotAsync(
                userId,
                "alice",
                "Operario",
                [OfflineStoreConstants.CaptureRecordsPermission],
                "http://localhost:5080",
                ttl ?? TimeSpan.FromHours(72));
        }

        db.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = 0 });
        await db.SaveChangesAsync();

        return (engine, capture, sessions, api, cookies);
    }

    [Fact]
    public async Task Push_Create_Accepted_Becomes_Synced()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        var serverId = Guid.NewGuid();
        api.PushHandler = req => AcceptedPush(req, serverId, "stamp-1");

        var run = await engine.SyncAsync();
        Assert.True(run.Started);
        Assert.Equal(1, run.PushedAccepted);

        var op = await db.PendingOperations.SingleAsync();
        Assert.Equal(PendingOperationStatus.Synced, op.Status);
        var record = await db.LocalNepRecords.SingleAsync();
        Assert.Equal(serverId, record.ServerRecordId);
        Assert.Equal("stamp-1", record.ConcurrencyStamp);
        Assert.Equal(LocalSyncStatus.Synced, record.SyncStatus);
    }

    [Fact]
    public async Task Push_Retry_Duplicate_Becomes_Synced()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "2", Neps = 11 });
        var serverId = Guid.NewGuid();
        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.Duplicate,
                EntityId = serverId,
                ConcurrencyStamp = "stamp-d"
            }).ToList()
        };

        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Push_Transient_Remains_Pending()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "3", Neps = 12 });
        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.TransientError,
                ErrorCode = "TRANSIENT",
                Message = "db busy"
            }).ToList()
        };

        await engine.SyncAsync();
        var op = await db.PendingOperations.SingleAsync();
        Assert.Equal(PendingOperationStatus.Pending, op.Status);
        Assert.True(op.AttemptCount >= 1);
        Assert.NotNull(op.LastError);
    }

    [Fact]
    public async Task Push_Invalid_Is_Permanent_SyncError()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "4", Neps = 13 });
        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.Invalid,
                ErrorCode = "VALIDATION",
                Message = "bad"
            }).ToList()
        };

        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.SyncError, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Push_Forbidden_Not_Synced()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "5", Neps = 14 });
        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.Forbidden,
                ErrorCode = "CAPTURE_FORBIDDEN"
            }).ToList()
        };

        await engine.SyncAsync();
        var op = await db.PendingOperations.SingleAsync();
        Assert.Equal(PendingOperationStatus.SyncError, op.Status);
        Assert.NotEqual(PendingOperationStatus.Synced, op.Status);
    }

    [Fact]
    public async Task Push_ClientOperationReused_Is_Permanent()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "6", Neps = 15 });
        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.Invalid,
                ErrorCode = "CLIENT_OPERATION_REUSED",
                Message = "reused"
            }).ToList()
        };

        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.SyncError, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Push_Batch_Processes_Multiple_Pending()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "A", Neps = 10 });
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "B", Neps = 11 });
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "C", Neps = 12 });
        api.PushHandler = req => AcceptedPush(req, Guid.NewGuid(), "s");

        var run = await engine.SyncAsync();
        Assert.Equal(3, run.PushedAccepted);
        Assert.Equal(3, await db.PendingOperations.CountAsync(o => o.Status == PendingOperationStatus.Synced));
    }

    [Fact]
    public async Task Conflict_Preserves_Local_And_Server_Snapshot_No_Auto_Retry()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "OLD", Neps = 20 });
        // Simular update pendiente con stamp stale.
        var op = await db.PendingOperations.SingleAsync();
        op.OperationType = OfflineOperationType.UpdateRecord;
        op.ExpectedConcurrencyStamp = "stale";
        op.PayloadJson = JsonSerializer.Serialize(new
        {
            entityId = created.Record.Id,
            telar = "NEW",
            neps = 99
        }, ClientSyncJson.Options);
        created.Record.Telar = "NEW";
        created.Record.Neps = 99;
        await db.SaveChangesAsync();

        var serverSnap = new ClientNepRecordSnapshot
        {
            Id = created.Record.Id,
            Telar = "SERVER",
            Neps = 50,
            ConcurrencyStamp = "server-stamp"
        };
        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results =
            [
                new ClientSyncOperationResultDto
                {
                    ClientOperationId = op.ClientOperationId,
                    Result = ClientSyncResultNames.Conflict,
                    ErrorCode = "CONFLICT",
                    EntityId = created.Record.Id,
                    ServerConcurrencyStamp = "server-stamp",
                    ServerSnapshot = StableJson(serverSnap)
                }
            ]
        };
        api.PullHandler = _ => EmptyPull(0);

        await engine.SyncAsync();
        op = await db.PendingOperations.SingleAsync();
        Assert.Equal(PendingOperationStatus.Conflict, op.Status);
        Assert.Equal("server-stamp", op.ConflictServerConcurrencyStamp);
        Assert.Contains("SERVER", op.ConflictServerSnapshotJson);
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.Equal("NEW", local.Telar); // no LWW
        Assert.Equal(99, local.Neps);

        // Segundo sync no reenvía Conflict.
        api.PushCalls = 0;
        await engine.SyncAsync();
        Assert.Equal(0, api.PushCalls);
    }

    [Fact]
    public async Task Pull_Upsert_And_Delete_Advance_Cursor_Atomically()
    {
        await using var db = CreateContext();
        var (engine, _, _, api, _) = await CreateEngineAsync(db);
        api.PushHandler = _ => new ClientSyncPushResponse();
        var entityId = Guid.NewGuid();
        var upsertPayload = StableJson(new ClientNepRecordSnapshot
        {
            Id = entityId,
            Telar = "P1",
            Neps = 18,
            ConcurrencyStamp = "c1",
            OwnerUserId = "user-a",
            ClientOperationId = "op-pull-1"
        });
        var deletePayload = StableJson(new ClientNepRecordDeletedSnapshot
        {
            Id = entityId,
            OwnerUserId = "user-a",
            DeletedAtUtc = DateTime.UtcNow,
            LastConcurrencyStamp = "c1"
        });
        api.PullHandler = req => req.Cursor switch
        {
            0 => new ClientSyncPullResponse
            {
                NextCursor = 1,
                HasMore = true,
                Changes =
                [
                    new ClientSyncChangeDto
                    {
                        Sequence = 1,
                        EntityId = entityId,
                        ChangeType = SyncConstants.ChangeRecordUpserted,
                        EntityType = SyncConstants.EntityNepRecord,
                        OccurredAtUtc = DateTime.UtcNow,
                        Payload = upsertPayload
                    }
                ]
            },
            1 => new ClientSyncPullResponse
            {
                NextCursor = 2,
                HasMore = false,
                Changes =
                [
                    new ClientSyncChangeDto
                    {
                        Sequence = 2,
                        EntityId = entityId,
                        ChangeType = SyncConstants.ChangeRecordDeleted,
                        EntityType = SyncConstants.EntityNepRecord,
                        OccurredAtUtc = DateTime.UtcNow,
                        Payload = deletePayload
                    }
                ]
            },
            _ => EmptyPull(req.Cursor)
        };

        var run = await engine.SyncAsync();
        Assert.True(string.IsNullOrEmpty(run.Message), run.Message);
        Assert.Equal(1, run.PulledUpserts);
        Assert.Equal(1, run.PulledDeletes);
        Assert.Equal(2, (await db.SyncStates.SingleAsync()).LastPulledSequence);
        Assert.True((await db.LocalNepRecords.SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task Pull_Apply_Failure_Keeps_Cursor()
    {
        await using var db = CreateContext();
        var (engine, _, _, api, _) = await CreateEngineAsync(db);
        api.PushHandler = _ => new ClientSyncPushResponse();
        api.PullHandler = _ => new ClientSyncPullResponse
        {
            NextCursor = 5,
            HasMore = false,
            Changes =
            [
                new ClientSyncChangeDto
                {
                    Sequence = 5,
                    EntityId = Guid.NewGuid(),
                    ChangeType = SyncConstants.ChangeRecordUpserted,
                    EntityType = SyncConstants.EntityNepRecord,
                    OccurredAtUtc = DateTime.UtcNow,
                    Payload = JsonDocument.Parse("\"not-an-object\"").RootElement.Clone()
                }
            ]
        };

        await engine.SyncAsync();
        Assert.Equal(0, (await db.SyncStates.SingleAsync()).LastPulledSequence);
    }

    [Fact]
    public async Task Pull_Multiple_Pages_HasMore()
    {
        await using var db = CreateContext();
        var (engine, _, _, api, _) = await CreateEngineAsync(db);
        api.PushHandler = _ => new ClientSyncPushResponse();
        var page = 0;
        api.PullHandler = req =>
        {
            page++;
            var id = Guid.NewGuid();
            return new ClientSyncPullResponse
            {
                NextCursor = req.Cursor + 1,
                HasMore = page < 3,
                Changes =
                [
                    new ClientSyncChangeDto
                    {
                        Sequence = req.Cursor + 1,
                        EntityId = id,
                        ChangeType = SyncConstants.ChangeRecordUpserted,
                        EntityType = SyncConstants.EntityNepRecord,
                        OccurredAtUtc = DateTime.UtcNow,
                        Payload = StableJson(new ClientNepRecordSnapshot
                        {
                            Id = id,
                            Telar = "T" + page,
                            Neps = 10 + page,
                            ConcurrencyStamp = "s" + page,
                            OwnerUserId = "user-a",
                            ClientOperationId = "page-op-" + page
                        })
                    }
                ]
            };
        };

        var run = await engine.SyncAsync();
        Assert.Equal(3, run.PulledUpserts);
        Assert.Equal(3, (await db.SyncStates.SingleAsync()).LastPulledSequence);
        Assert.Equal(3, await db.LocalNepRecords.CountAsync());
    }

    [Fact]
    public async Task Push_Then_Pull_Create_Updates_Replica()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db);
        var local = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "X", Neps = 18 });
        var serverId = Guid.NewGuid();
        api.PushHandler = req => AcceptedPush(req, serverId, "stamp-x");
        api.PullHandler = req =>
        {
            if (req.Cursor > 0)
            {
                return EmptyPull(req.Cursor);
            }

            return new ClientSyncPullResponse
            {
                NextCursor = 1,
                HasMore = false,
                Changes =
                [
                    new ClientSyncChangeDto
                    {
                        Sequence = 1,
                        EntityId = serverId,
                        ChangeType = SyncConstants.ChangeRecordUpserted,
                        EntityType = SyncConstants.EntityNepRecord,
                        OccurredAtUtc = DateTime.UtcNow,
                        Payload = StableJson(new ClientNepRecordSnapshot
                        {
                            Id = serverId,
                            Telar = "X",
                            Neps = 18,
                            ConcurrencyStamp = "stamp-x",
                            ClientOperationId = local.Operation.ClientOperationId,
                            OwnerUserId = "user-a"
                        })
                    }
                ]
            };
        };

        await engine.SyncAsync();
        var record = await db.LocalNepRecords.SingleAsync();
        Assert.Equal(serverId, record.ServerRecordId);
        Assert.Equal("stamp-x", record.ConcurrencyStamp);
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Remote_Update_Refreshes_ExpectedStamp_For_Pending_Local_Edit()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "L", Neps = 10 });
        var serverId = created.Record.Id;
        created.Record.ServerRecordId = serverId;
        created.Record.ConcurrencyStamp = "old";
        var op = await db.PendingOperations.SingleAsync();
        op.Status = PendingOperationStatus.Synced;
        // Nueva operación Update pendiente distinta.
        var updateOp = new PendingOperation
        {
            Id = Guid.NewGuid(),
            ClientOperationId = Guid.NewGuid().ToString("N"),
            OperationType = OfflineOperationType.UpdateRecord,
            PayloadJson = JsonSerializer.Serialize(new { entityId = serverId, telar = "LOCAL", neps = 77 }, ClientSyncJson.Options),
            ProtocolVersion = 1,
            CreatedAtUtc = DateTime.UtcNow,
            Status = PendingOperationStatus.Pending,
            UserId = "user-a",
            DeviceId = "dev",
            LocalNepRecordId = created.Record.Id,
            TargetServerRecordId = serverId,
            ExpectedConcurrencyStamp = "old"
        };
        created.Record.Telar = "LOCAL";
        created.Record.Neps = 77;
        created.Record.SyncStatus = LocalSyncStatus.PendingSync;
        db.PendingOperations.Add(updateOp);
        await db.SaveChangesAsync();

        api.PushHandler = _ => new ClientSyncPushResponse(); // no enviar update aún en este test de pull
        // Forzar que no haya pending create — update se enviaría; vaciamos push aceptando nada al filtrar...
        // Mejor: marcar push para Transient del update y pull primero — Sync hace Push primero.
        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.TransientError,
                ErrorCode = "TRANSIENT"
            }).ToList()
        };
        api.PullHandler = req =>
        {
            if (req.Cursor > 0)
            {
                return EmptyPull(req.Cursor);
            }

            return new ClientSyncPullResponse
            {
                NextCursor = 1,
                HasMore = false,
                Changes =
                [
                    new ClientSyncChangeDto
                    {
                        Sequence = 1,
                        EntityId = serverId,
                        ChangeType = SyncConstants.ChangeRecordUpserted,
                        EntityType = SyncConstants.EntityNepRecord,
                        OccurredAtUtc = DateTime.UtcNow,
                        Payload = StableJson(new ClientNepRecordSnapshot
                        {
                            Id = serverId,
                            Telar = "REMOTE",
                            Neps = 5,
                            ConcurrencyStamp = "new-stamp",
                            OwnerUserId = "user-a",
                            ClientOperationId = "other-op"
                        })
                    }
                ]
            };
        };

        await engine.SyncAsync();
        updateOp = await db.PendingOperations.SingleAsync(o => o.Id == updateOp.Id);
        Assert.Equal(PendingOperationStatus.Pending, updateOp.Status);
        Assert.Equal("new-stamp", updateOp.ExpectedConcurrencyStamp);
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.Equal("LOCAL", local.Telar); // no LWW
        Assert.Equal("new-stamp", local.ConcurrencyStamp);
    }

    [Fact]
    public async Task No_Session_No_Push()
    {
        await using var db = CreateContext();
        var (engine, _, _, api, _) = await CreateEngineAsync(db, withSession: false);
        // SyncState was added; clear sessions
        var run = await engine.SyncAsync();
        Assert.False(run.Started);
        Assert.True(run.SessionMissingOrExpired);
        Assert.Equal(0, api.PushCalls);
    }

    [Fact]
    public async Task Expired_Session_Blocks_Sync_After_TTL()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db, ttl: TimeSpan.FromHours(1));
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 1 });
        var session = await db.LocalSessions.SingleAsync();
        session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var run = await engine.SyncAsync();
        Assert.False(run.Started);
        Assert.True(run.SessionMissingOrExpired);
        Assert.Equal(0, api.PushCalls);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Other_User_Outbox_Is_Not_Processed()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, _) = await CreateEngineAsync(db, userId: "user-a");
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "mine", Neps = 10 });
        db.PendingOperations.Add(new PendingOperation
        {
            Id = Guid.NewGuid(),
            ClientOperationId = Guid.NewGuid().ToString("N"),
            OperationType = OfflineOperationType.CreateRecord,
            PayloadJson = """{"telar":"other","neps":1}""",
            ProtocolVersion = 1,
            CreatedAtUtc = DateTime.UtcNow,
            Status = PendingOperationStatus.Pending,
            UserId = "user-b",
            DeviceId = "dev"
        });
        await db.SaveChangesAsync();
        api.PushHandler = req => AcceptedPush(req, Guid.NewGuid(), "s");

        var run = await engine.SyncAsync();
        Assert.Equal(1, run.SkippedOtherUser);
        Assert.Equal(1, run.PushedAccepted);
        Assert.Equal(PendingOperationStatus.Pending,
            (await db.PendingOperations.SingleAsync(o => o.UserId == "user-b")).Status);
    }

    [Fact]
    public async Task Logout_Clears_Session_Keeps_Outbox_Resume_Same_User()
    {
        await using var db = CreateContext();
        var (engine, capture, sessions, api, _) = await CreateEngineAsync(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        var clear = await sessions.ClearUxSnapshotAsync();
        Assert.Equal(1, clear.PendingOperationsRetained);
        Assert.Null(await sessions.GetValidSessionAsync());

        var blocked = await engine.SyncAsync();
        Assert.True(blocked.SessionMissingOrExpired);

        await sessions.UpsertUxSnapshotAsync(
            "user-a", "alice", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080");
        api.PushHandler = req => AcceptedPush(req, Guid.NewGuid(), "s");
        var run = await engine.SyncAsync();
        Assert.Equal(1, run.PushedAccepted);
    }

    [Fact]
    public async Task Missing_Cookie_Is_AuthRequired_Not_Transient()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api, cookies) = await CreateEngineAsync(db, cookie: null);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        var run = await engine.SyncAsync();
        Assert.True(run.AuthRequired);
        Assert.Equal(0, api.PushCalls);
    }

    private static ClientSyncPushResponse AcceptedPush(ClientSyncPushRequest req, Guid entityId, string stamp) =>
        new()
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.Accepted,
                EntityId = entityId == Guid.Empty ? Guid.NewGuid() : entityId,
                ConcurrencyStamp = stamp
            }).ToList()
        };

    private static ClientSyncPullResponse EmptyPull(long cursor) =>
        new()
        {
            NextCursor = cursor,
            HasMore = false,
            Changes = []
        };

    private static JsonElement StableJson<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, ClientSyncJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class StaticCookieProvider : ISyncAuthCookieProvider
    {
        private readonly string? _cookie;
        public StaticCookieProvider(string? cookie) => _cookie = cookie;
        public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default) =>
            Task.FromResult(_cookie);
    }

    private sealed class FakeSyncApiClient : ISyncApiClient
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
            Task.FromResult(PullHandler?.Invoke(request) ?? EmptyPull(request.Cursor));
    }
}
