using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

/// <summary>FASE 2D.5 — Update offline: Outbox, atomicidad, SyncEngine, NEPS, permisos.</summary>
public sealed class OfflineOfflineUpdateTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-offline-update-" + Guid.NewGuid().ToString("N"));
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
        BootAsync(LocalSyncDbContext db, string userId = "user-a", string? cookie = "RegNeps.Auth=t")
    {
        await ClearAsync(db);
        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-" + Guid.NewGuid().ToString("N")[..8]));
        var capture = new OfflineCaptureService(db, sessions, devices);
        var api = new FakeApi();
        var engine = new SyncEngine(db, sessions, devices, api, new StaticCookie(cookie));

        await sessions.UpsertUxSnapshotAsync(
            userId,
            "alice",
            "Operario",
            [
                OfflineStoreConstants.CaptureRecordsPermission,
                OfflineStoreConstants.EditRecordsPermission,
                "ViewRecords"
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        db.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = 0 });
        await db.SaveChangesAsync();
        return (capture, sessions, engine, api);
    }

    /// <summary>Create + marca Synced con ServerRecordId/ConcurrencyStamp (listo para Update).</summary>
    private static async Task<(LocalNepRecord Record, string CreateClientOpId)> SeedSyncedRecordAsync(
        OfflineCaptureService capture,
        LocalSyncDbContext db,
        string telar = "100",
        double neps = 18,
        string stamp = "stamp-v1")
    {
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = telar,
            Neps = neps,
            Tela = "Denim",
            LoteTrama = "63E26401",
            Turno = "A"
        });

        var serverId = Guid.NewGuid();
        var record = await db.LocalNepRecords.FirstAsync(r => r.Id == created.Record.Id);
        record.ServerRecordId = serverId;
        record.ConcurrencyStamp = stamp;
        record.SyncStatus = LocalSyncStatus.Synced;
        var op = await db.PendingOperations.FirstAsync(o => o.Id == created.Operation.Id);
        op.Status = PendingOperationStatus.Synced;
        op.TargetServerRecordId = serverId;
        await db.SaveChangesAsync();
        return (record, created.Operation.ClientOperationId);
    }

    [Fact]
    public async Task Update_Creates_Pending_With_New_ClientOperationId_And_ExpectedStamp()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var (record, createOpId) = await SeedSyncedRecordAsync(capture, db);

        var result = await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "101",
            Neps = 19,
            Tela = "Denim",
            LoteTrama = "63E26401",
            Turno = "B",
            Observacion = "edit offline"
        });

        Assert.Equal(OfflineOperationType.UpdateRecord, result.Operation.OperationType);
        Assert.Equal(PendingOperationStatus.Pending, result.Operation.Status);
        Assert.NotEqual(createOpId, result.Operation.ClientOperationId);
        Assert.Equal("stamp-v1", result.Operation.ExpectedConcurrencyStamp);
        Assert.Equal(record.ServerRecordId, result.Operation.TargetServerRecordId);
        Assert.Equal(LocalSyncStatus.PendingSync, result.Record.SyncStatus);
        Assert.Equal("101", result.Record.Telar);
        Assert.Equal(19, result.Record.Neps);
        Assert.Equal(AlertLevel.Mention, result.QualityLevel);
        // Stamp local intacto hasta Accepted.
        Assert.Equal("stamp-v1", (await db.LocalNepRecords.SingleAsync()).ConcurrencyStamp);

        var payload = JsonSerializer.Deserialize<UpdateRecordPayload>(
            result.Operation.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(payload);
        Assert.Equal(record.ServerRecordId, payload!.EntityId);
        Assert.Equal("101", payload.Telar);
    }

    [Fact]
    public async Task Update_Atomic_Rollback_Leaves_Neither_Changed_Nor_Outbox()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var (record, _) = await SeedSyncedRecordAsync(capture, db, telar: "OLD", neps: 18);
        var localId = record.Id;

        await using (var failDb = CreateContext(new FailOnPendingOperationInsertInterceptor()))
        {
            var sessions = new OfflineSessionService(failDb, new MemorySecureAuthMaterialStore());
            var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-fail"));
            var failCapture = new OfflineCaptureService(failDb, sessions, devices);
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

            await Assert.ThrowsAnyAsync<Exception>(() =>
                failCapture.UpdateRecordAsync(new OfflineUpdateRecordRequest
                {
                    LocalRecordId = localId,
                    Telar = "NEW",
                    Neps = 55
                }));
        }

        await using var verify = CreateContext();
        var local = await verify.LocalNepRecords.SingleAsync(r => r.Id == localId);
        Assert.Equal("OLD", local.Telar);
        Assert.Equal(18, local.Neps);
        Assert.Equal(0, await verify.PendingOperations.CountAsync(o =>
            o.OperationType == OfflineOperationType.UpdateRecord));
    }

    [Fact]
    public async Task Second_Update_Blocked_While_First_Pending()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var (record, _) = await SeedSyncedRecordAsync(capture, db);

        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "1",
            Neps = 20
        });

        var elig = await capture.GetEditEligibilityAsync(record.Id);
        Assert.False(elig.CanEdit);
        Assert.Equal(OfflineEditBlockReason.UpdateAlreadyPending, elig.Reason);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
            {
                LocalRecordId = record.Id,
                Telar = "2",
                Neps = 21
            }));

        Assert.Equal(1, await db.PendingOperations.CountAsync(o =>
            o.OperationType == OfflineOperationType.UpdateRecord));
    }

    [Fact]
    public async Task Create_Pending_Blocks_Update()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = "9",
            Neps = 18
        });

        var elig = await capture.GetEditEligibilityAsync(created.Record.Id);
        Assert.False(elig.CanEdit);
        Assert.Equal(OfflineEditBlockReason.CreateStillPending, elig.Reason);
    }

    [Fact]
    public async Task Update_Accepted_Then_Pull_Keeps_Local_Edit()
    {
        await using var db = CreateContext();
        var (capture, _, engine, api) = await BootAsync(db);
        var (record, _) = await SeedSyncedRecordAsync(capture, db, telar: "ORIG", neps: 18, stamp: "s0");
        var serverId = record.ServerRecordId!.Value;

        var updated = await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "EDIT",
            Neps = 46,
            Tela = "Denim"
        });

        api.PushHandler = req =>
        {
            Assert.Single(req.Operations);
            var op = req.Operations[0];
            Assert.Equal(SyncConstants.OperationUpdateRecord, op.OperationType);
            Assert.Equal("s0", op.ExpectedConcurrencyStamp);
            Assert.Equal(updated.Operation.ClientOperationId, op.ClientOperationId);
            return AcceptedPush(req, serverId, "s1");
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
                        ChangeType = SyncConstants.ChangeRecordUpserted,
                        EntityType = SyncConstants.EntityNepRecord,
                        OccurredAtUtc = DateTime.UtcNow,
                        Payload = StableJson(new ClientNepRecordSnapshot
                        {
                            Id = serverId,
                            Telar = "EDIT",
                            Neps = 46,
                            ConcurrencyStamp = "s1",
                            ClientOperationId = updated.Operation.ClientOperationId,
                            OwnerUserId = "user-a"
                        })
                    }
                ]
            };

        var run = await engine.SyncAsync();
        Assert.Equal(1, run.PushedAccepted);
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations
            .SingleAsync(o => o.OperationType == OfflineOperationType.UpdateRecord)).Status);

        var local = await db.LocalNepRecords.SingleAsync();
        Assert.Equal("EDIT", local.Telar);
        Assert.Equal(46, local.Neps);
        Assert.Equal("s1", local.ConcurrencyStamp);
        Assert.Equal(LocalSyncStatus.Synced, local.SyncStatus);
        Assert.Equal(AlertLevel.CriticalAdjustment, AlertEvaluator.GetLevel(local.Neps));
    }

    [Fact]
    public async Task Update_Duplicate_Recovers_Without_Duplicating()
    {
        await using var db = CreateContext();
        var (capture, _, engine, api) = await BootAsync(db);
        var (record, _) = await SeedSyncedRecordAsync(capture, db, stamp: "s0");
        var serverId = record.ServerRecordId!.Value;

        var updated = await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "DUP",
            Neps = 22
        });

        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results =
            [
                new ClientSyncOperationResultDto
                {
                    ClientOperationId = updated.Operation.ClientOperationId,
                    Result = ClientSyncResultNames.Duplicate,
                    EntityId = serverId,
                    ConcurrencyStamp = "s1"
                }
            ]
        };
        api.PullHandler = _ => EmptyPull(0);

        var run = await engine.SyncAsync();
        Assert.Equal(1, run.PushedDuplicate);
        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations
            .SingleAsync(o => o.ClientOperationId == updated.Operation.ClientOperationId)).Status);
        Assert.Equal(serverId, (await db.LocalNepRecords.SingleAsync()).ServerRecordId);
    }

    [Fact]
    public async Task Update_Conflict_Preserves_Local_And_Server_Snapshot()
    {
        await using var db = CreateContext();
        var (capture, _, engine, api) = await BootAsync(db);
        var (record, _) = await SeedSyncedRecordAsync(capture, db, telar: "L", neps: 18, stamp: "old");
        var serverId = record.ServerRecordId!.Value;

        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "LOCAL",
            Neps = 55
        });

        var snap = new ClientNepRecordSnapshot
        {
            Id = serverId,
            Telar = "SERVER",
            Neps = 30,
            ConcurrencyStamp = "server-stamp"
        };
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
                    ServerConcurrencyStamp = "server-stamp",
                    ServerSnapshot = StableJson(snap)
                }
            ]
        };
        api.PullHandler = _ => EmptyPull(0);

        await engine.SyncAsync();
        var op = await db.PendingOperations.SingleAsync(o => o.OperationType == OfflineOperationType.UpdateRecord);
        Assert.Equal(PendingOperationStatus.Conflict, op.Status);
        Assert.Equal("server-stamp", op.ConflictServerConcurrencyStamp);
        Assert.Contains("SERVER", op.ConflictServerSnapshotJson);
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.Equal("LOCAL", local.Telar);
        Assert.Equal(55, local.Neps);
        Assert.Equal(AlertLevel.SecondQuality, AlertEvaluator.GetLevel(local.Neps));

        api.PushCalls = 0;
        await engine.SyncAsync();
        Assert.Equal(0, api.PushCalls);
    }

    [Fact]
    public async Task Update_Forbidden_Becomes_SyncError()
    {
        await using var db = CreateContext();
        var (capture, _, engine, api) = await BootAsync(db);
        var (record, _) = await SeedSyncedRecordAsync(capture, db);

        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "F",
            Neps = 18
        });

        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results =
            [
                new ClientSyncOperationResultDto
                {
                    ClientOperationId = req.Operations[0].ClientOperationId,
                    Result = ClientSyncResultNames.Forbidden,
                    ErrorCode = "FORBIDDEN"
                }
            ]
        };
        api.PullHandler = _ => EmptyPull(0);

        await engine.SyncAsync();
        var op = await db.PendingOperations.SingleAsync(o => o.OperationType == OfflineOperationType.UpdateRecord);
        Assert.Equal(PendingOperationStatus.SyncError, op.Status);
        Assert.Equal("F", (await db.LocalNepRecords.SingleAsync()).Telar);
    }

    [Fact]
    public async Task Logout_Keeps_Pending_Update_And_Sync_Requires_Auth()
    {
        await using var db = CreateContext();
        var (capture, sessions, engine, api) = await BootAsync(db);
        var (record, _) = await SeedSyncedRecordAsync(capture, db);

        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "KEEP",
            Neps = 19
        });

        await sessions.ClearUxSnapshotAsync();
        Assert.Null(await sessions.GetValidSessionAsync());
        Assert.Equal(1, await db.PendingOperations.CountAsync(o =>
            o.OperationType == OfflineOperationType.UpdateRecord
            && o.Status == PendingOperationStatus.Pending));
        Assert.Equal("KEEP", (await db.LocalNepRecords.SingleAsync()).Telar);

        var run = await engine.SyncAsync();
        Assert.True(run.SessionMissingOrExpired);
        Assert.Equal(0, api.PushCalls);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations
            .SingleAsync(o => o.OperationType == OfflineOperationType.UpdateRecord)).Status);
    }

    [Fact]
    public async Task Other_User_Does_Not_Push_Foreign_Update()
    {
        await using var db = CreateContext();
        var (capture, sessions, engine, api) = await BootAsync(db, userId: "user-a");
        var (record, _) = await SeedSyncedRecordAsync(capture, db);

        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "A-EDIT",
            Neps = 20
        });

        await sessions.ClearUxSnapshotAsync();
        await sessions.UpsertUxSnapshotAsync(
            "user-b",
            "bob",
            "Operario",
            [
                OfflineStoreConstants.CaptureRecordsPermission,
                OfflineStoreConstants.EditRecordsPermission
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        api.PushHandler = _ => new ClientSyncPushResponse();
        api.PullHandler = _ => EmptyPull(0);
        await engine.SyncAsync();
        Assert.Equal(0, api.PushCalls);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations
            .SingleAsync(o => o.UserId == "user-a" && o.OperationType == OfflineOperationType.UpdateRecord)).Status);
    }

    [Fact]
    public async Task Restart_Persists_Pending_Update_Fields()
    {
        Guid localId;
        Guid opId;
        string clientOpId;
        string expectedStamp;
        Guid serverId;

        await using (var db = CreateContext())
        {
            var (capture, _, _, _) = await BootAsync(db);
            var (record, _) = await SeedSyncedRecordAsync(capture, db, stamp: "persist-stamp");
            localId = record.Id;
            serverId = record.ServerRecordId!.Value;
            var updated = await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
            {
                LocalRecordId = record.Id,
                Telar = "REOPEN",
                Neps = 45
            });
            opId = updated.Operation.Id;
            clientOpId = updated.Operation.ClientOperationId;
            expectedStamp = updated.Operation.ExpectedConcurrencyStamp!;
        }

        await using var reopen = CreateContext();
        var local = await reopen.LocalNepRecords.SingleAsync(r => r.Id == localId);
        var op = await reopen.PendingOperations.SingleAsync(o => o.Id == opId);
        Assert.Equal("REOPEN", local.Telar);
        Assert.Equal(45, local.Neps);
        Assert.Equal(PendingOperationStatus.Pending, op.Status);
        Assert.Equal(clientOpId, op.ClientOperationId);
        Assert.Equal(expectedStamp, op.ExpectedConcurrencyStamp);
        Assert.Equal(serverId, op.TargetServerRecordId);
        Assert.Equal(serverId, local.ServerRecordId);
    }

    [Fact]
    public async Task Restart_Persists_Conflict_State()
    {
        Guid opId;
        await using (var db = CreateContext())
        {
            var (capture, _, engine, api) = await BootAsync(db);
            var (record, _) = await SeedSyncedRecordAsync(capture, db, stamp: "old");
            var serverId = record.ServerRecordId!.Value;
            var updated = await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
            {
                LocalRecordId = record.Id,
                Telar = "LOC",
                Neps = 54
            });
            opId = updated.Operation.Id;
            api.PushHandler = req => new ClientSyncPushResponse
            {
                Results =
                [
                    new ClientSyncOperationResultDto
                    {
                        ClientOperationId = req.Operations[0].ClientOperationId,
                        Result = ClientSyncResultNames.Conflict,
                        ServerConcurrencyStamp = "srv",
                        ServerSnapshot = StableJson(new ClientNepRecordSnapshot
                        {
                            Id = serverId,
                            Telar = "SRV",
                            Neps = 10,
                            ConcurrencyStamp = "srv"
                        })
                    }
                ]
            };
            api.PullHandler = _ => EmptyPull(0);
            await engine.SyncAsync();
        }

        await using var reopen = CreateContext();
        var op = await reopen.PendingOperations.SingleAsync(o => o.Id == opId);
        var local = await reopen.LocalNepRecords.SingleAsync();
        Assert.Equal(PendingOperationStatus.Conflict, op.Status);
        Assert.Equal("LOC", local.Telar);
        Assert.Equal("srv", op.ConflictServerConcurrencyStamp);
        Assert.False(string.IsNullOrWhiteSpace(op.ConflictServerSnapshotJson));
    }

    [Fact]
    public async Task Neps_Quality_Official_Thresholds_On_Update()
    {
        await using var db = CreateContext();
        var (capture, _, _, _) = await BootAsync(db);
        var (record, _) = await SeedSyncedRecordAsync(capture, db);

        async Task AssertLevel(double neps, AlertLevel expected)
        {
            // Liberar Update pendiente previo.
            var pending = await db.PendingOperations
                .Where(o => o.OperationType == OfflineOperationType.UpdateRecord
                            && o.Status != PendingOperationStatus.Synced)
                .ToListAsync();
            foreach (var p in pending)
            {
                p.Status = PendingOperationStatus.Synced;
            }

            var local = await db.LocalNepRecords.FirstAsync(r => r.Id == record.Id);
            local.SyncStatus = LocalSyncStatus.Synced;
            await db.SaveChangesAsync();

            var r = await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
            {
                LocalRecordId = record.Id,
                Telar = "Q",
                Neps = neps
            });
            Assert.Equal(expected, r.QualityLevel);
            Assert.Equal(expected.ToDisplayLabel(), r.QualityLabel);
        }

        await AssertLevel(18, AlertLevel.Ok);
        await AssertLevel(19, AlertLevel.Mention);
        await AssertLevel(45, AlertLevel.Mention);
        await AssertLevel(46, AlertLevel.CriticalAdjustment);
        await AssertLevel(54, AlertLevel.CriticalAdjustment);
        await AssertLevel(55, AlertLevel.SecondQuality);
    }

    [Fact]
    public async Task No_EditRecords_Permission_Blocks_Ux()
    {
        await using var db = CreateContext();
        await ClearAsync(db);
        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-noperm"));
        var capture = new OfflineCaptureService(db, sessions, devices);
        await sessions.UpsertUxSnapshotAsync(
            "user-a",
            "alice",
            "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 18 });
        var rec = await db.LocalNepRecords.FirstAsync();
        rec.ServerRecordId = Guid.NewGuid();
        rec.ConcurrencyStamp = "s";
        rec.SyncStatus = LocalSyncStatus.Synced;
        (await db.PendingOperations.SingleAsync()).Status = PendingOperationStatus.Synced;
        await db.SaveChangesAsync();

        var elig = await capture.GetEditEligibilityAsync(created.Record.Id);
        Assert.False(elig.CanEdit);
        Assert.Equal(OfflineEditBlockReason.NoEditPermission, elig.Reason);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
            {
                LocalRecordId = created.Record.Id,
                Telar = "1",
                Neps = 19
            }));
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

    private sealed class FailOnPendingOperationInsertInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            if (HasAddedPending(eventData))
            {
                throw new InvalidOperationException("forced outbox insert failure");
            }

            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
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
