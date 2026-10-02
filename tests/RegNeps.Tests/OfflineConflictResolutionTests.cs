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

/// <summary>FASE 2D.7 — Resolución explícita de Conflict (sin LWW / Restore).</summary>
public sealed class OfflineConflictResolutionTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-conflict-2d7-" + Guid.NewGuid().ToString("N"));
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

    private async Task<(
            OfflineCaptureService Capture,
            OfflineSessionService Sessions,
            ConflictResolutionService Resolver,
            SyncEngine Engine,
            FakeApi Api)>
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
        var resolver = new ConflictResolutionService(db, sessions, devices);
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
            userId,
            "alice",
            role,
            permissions,
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        db.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = 0 });
        await db.SaveChangesAsync();
        return (capture, sessions, resolver, engine, api);
    }

    private static async Task<(LocalNepRecord Record, Guid ServerId)> SeedSyncedAsync(
        OfflineCaptureService capture,
        LocalSyncDbContext db,
        string telar = "100",
        double neps = 18,
        string stamp = "stamp-x")
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
        return (record, serverId);
    }

    private static async Task<PendingOperation> ForceUpdateConflictAsync(
        OfflineCaptureService capture,
        SyncEngine engine,
        FakeApi api,
        LocalSyncDbContext db,
        Guid localId,
        Guid serverId,
        string localTelar,
        double localNeps,
        string serverTelar,
        double serverNeps,
        string serverStamp)
    {
        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = localId,
            Telar = localTelar,
            Neps = localNeps,
            Tela = "Denim",
            LoteTrama = "63E26401",
            Turno = "A"
        });

        var snap = new ClientNepRecordSnapshot
        {
            Id = serverId,
            Telar = serverTelar,
            Neps = serverNeps,
            Tela = "Denim",
            LoteTrama = "63E26401",
            Turno = "B",
            ConcurrencyStamp = serverStamp
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
                    ServerConcurrencyStamp = serverStamp,
                    ServerSnapshot = StableJson(snap)
                }
            ]
        };
        api.PullHandler = _ => EmptyPull(0);
        await engine.SyncAsync();
        return await db.PendingOperations.SingleAsync(o =>
            o.OperationType == OfflineOperationType.UpdateRecord
            && o.Status == PendingOperationStatus.Conflict);
    }

    [Fact]
    public async Task UpdateUpdate_KeepServer_Adopts_Snapshot_Closes_Op_No_Push()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
        var conflictOp = await ForceUpdateConflictAsync(
            capture, engine, api, db, record.Id, serverId,
            "LOCAL", 55, "SERVER", 30, "Y");
        var originalOpId = conflictOp.ClientOperationId;

        api.PushCalls = 0;
        var result = await resolver.KeepServerAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Success, result.Outcome);
        Assert.Equal(ConflictResolutionDecision.KeepServer, result.Decision);

        var closed = await db.PendingOperations.SingleAsync(o => o.Id == conflictOp.Id);
        Assert.Equal(PendingOperationStatus.Cancelled, closed.Status);
        Assert.StartsWith(ConflictResolutionService.ResolutionMarkerKeepServer, closed.LastError);
        Assert.Equal(originalOpId, closed.ClientOperationId);

        var local = await db.LocalNepRecords.SingleAsync();
        Assert.Equal("SERVER", local.Telar);
        Assert.Equal(30, local.Neps);
        Assert.Equal("Y", local.ConcurrencyStamp);
        Assert.Equal(LocalSyncStatus.Synced, local.SyncStatus);
        Assert.False(local.IsDeleted);

        await engine.SyncAsync();
        Assert.Equal(0, api.PushCalls);
    }

    [Fact]
    public async Task UpdateUpdate_KeepLocal_New_Op_ExpectedStamp_Y()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
        var conflictOp = await ForceUpdateConflictAsync(
            capture, engine, api, db, record.Id, serverId,
            "LOCAL", 55, "SERVER", 30, "Y");
        var conflictClientOp = conflictOp.ClientOperationId;

        var result = await resolver.KeepLocalAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Success, result.Outcome);
        Assert.NotNull(result.NewClientOperationId);
        Assert.NotEqual(conflictClientOp, result.NewClientOperationId);
        Assert.Equal("Y", result.ExpectedConcurrencyStamp);

        Assert.Equal(PendingOperationStatus.Cancelled,
            (await db.PendingOperations.SingleAsync(o => o.Id == conflictOp.Id)).Status);

        var newOp = await db.PendingOperations.SingleAsync(o => o.Id == result.NewOperationId);
        Assert.Equal(PendingOperationStatus.Pending, newOp.Status);
        Assert.Equal(OfflineOperationType.UpdateRecord, newOp.OperationType);
        Assert.Equal("Y", newOp.ExpectedConcurrencyStamp);
        Assert.Equal("LOCAL", (await db.LocalNepRecords.SingleAsync()).Telar);
        Assert.Equal(55, (await db.LocalNepRecords.SingleAsync()).Neps);
    }

    [Fact]
    public async Task UpdateUpdate_EditAndRetry_Uses_Edited_Fields_And_Quality_From_Neps()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
        var conflictOp = await ForceUpdateConflictAsync(
            capture, engine, api, db, record.Id, serverId,
            "LOCAL", 20, "SERVER", 30, "Y");

        // Neps≈54 → NEPS/m≈600 → Crítico - Realizar Ajuste (criterio oficial).
        var result = await resolver.EditAndRetryAsync(conflictOp.Id, new ConflictEditFields
        {
            Telar = "EDITED",
            Neps = 54,
            Tela = "Denim",
            LoteTrama = "63E26401",
            Turno = "C"
        });

        Assert.Equal(ConflictResolutionOutcome.Success, result.Outcome);
        Assert.Equal(AlertLevel.CriticalAdjustment.ToDisplayLabel(), result.QualityLabel);

        var local = await db.LocalNepRecords.SingleAsync();
        Assert.Equal("EDITED", local.Telar);
        Assert.Equal(54, local.Neps);
        Assert.Equal(AlertLevel.CriticalAdjustment, AlertEvaluator.GetLevel(local.Neps));
        Assert.Equal("Y", (await db.PendingOperations.SingleAsync(o => o.Id == result.NewOperationId))
            .ExpectedConcurrencyStamp);
    }

    [Fact]
    public async Task UpdateUpdate_KeepLocal_Second_Conflict_When_Server_Moves_To_Z()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
        var conflictOp = await ForceUpdateConflictAsync(
            capture, engine, api, db, record.Id, serverId,
            "LOCAL", 55, "SERVER", 30, "Y");

        var keep = await resolver.KeepLocalAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Success, keep.Outcome);

        var snapZ = new ClientNepRecordSnapshot
        {
            Id = serverId,
            Telar = "Z-TELAR",
            Neps = 40,
            ConcurrencyStamp = "Z"
        };
        api.PushHandler = req =>
        {
            Assert.Equal("Y", req.Operations[0].ExpectedConcurrencyStamp);
            return new ClientSyncPushResponse
            {
                Results =
                [
                    new ClientSyncOperationResultDto
                    {
                        ClientOperationId = req.Operations[0].ClientOperationId,
                        Result = ClientSyncResultNames.Conflict,
                        ErrorCode = "CONFLICT",
                        EntityId = serverId,
                        ServerConcurrencyStamp = "Z",
                        ServerSnapshot = StableJson(snapZ)
                    }
                ]
            };
        };

        await engine.SyncAsync();
        var newConflict = await db.PendingOperations.SingleAsync(o =>
            o.Id == keep.NewOperationId);
        Assert.Equal(PendingOperationStatus.Conflict, newConflict.Status);
        Assert.Equal("Z", newConflict.ConflictServerConcurrencyStamp);
        Assert.Equal("LOCAL", (await db.LocalNepRecords.SingleAsync()).Telar);
    }

    [Fact]
    public async Task UpdateDelete_KeepServer_Tombstone_No_Restore()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db);

        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = record.Id,
            Telar = "KEEP-ME",
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
        api.PullHandler = _ => EmptyPull(0);
        await engine.SyncAsync();

        var conflictOp = await db.PendingOperations.SingleAsync(o =>
            o.OperationType == OfflineOperationType.UpdateRecord);
        Assert.Equal(PendingOperationStatus.Conflict, conflictOp.Status);

        var view = await resolver.GetViewAsync(conflictOp.Id);
        Assert.NotNull(view);
        Assert.Equal(OfflineConflictKind.UpdateDelete, view!.Kind);
        Assert.True(view.AllowedActions.HasFlag(ConflictResolutionActions.KeepServer));
        Assert.False(view.AllowedActions.HasFlag(ConflictResolutionActions.KeepLocal));
        Assert.False(view.AllowedActions.HasFlag(ConflictResolutionActions.EditAndRetry));

        var keepLocal = await resolver.KeepLocalAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Rejected, keepLocal.Outcome);

        var keepServer = await resolver.KeepServerAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Success, keepServer.Outcome);
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.True(local.IsDeleted);
        Assert.Equal(LocalSyncStatus.Synced, local.SyncStatus);
        Assert.Equal(PendingOperationStatus.Cancelled,
            (await db.PendingOperations.SingleAsync(o => o.Id == conflictOp.Id)).Status);
    }

    [Fact]
    public async Task DeleteUpdate_KeepServer_And_KeepLocal_New_Delete()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");

        await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = record.Id });
        var snap = new ClientNepRecordSnapshot
        {
            Id = serverId,
            Telar = "STILL-ALIVE",
            Neps = 25,
            ConcurrencyStamp = "Y"
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
                    ServerConcurrencyStamp = "Y",
                    ServerSnapshot = StableJson(snap)
                }
            ]
        };
        api.PullHandler = _ => EmptyPull(0);
        await engine.SyncAsync();

        var conflictOp = await db.PendingOperations.SingleAsync(o =>
            o.OperationType == OfflineOperationType.DeleteRecord
            && o.Status == PendingOperationStatus.Conflict);
        Assert.Equal(OfflineConflictKind.DeleteUpdate, ConflictKindClassifier.Classify(conflictOp));

        // Keep Server path en DB limpia aparte: aquí Keep Local.
        var keepLocal = await resolver.KeepLocalAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Success, keepLocal.Outcome);
        Assert.NotEqual(conflictOp.ClientOperationId, keepLocal.NewClientOperationId);
        var newDel = await db.PendingOperations.SingleAsync(o => o.Id == keepLocal.NewOperationId);
        Assert.Equal(OfflineOperationType.DeleteRecord, newDel.OperationType);
        Assert.Equal("Y", newDel.ExpectedConcurrencyStamp);
        Assert.True((await db.LocalNepRecords.SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task DeleteUpdate_KeepServer_Adopts_Alive_Record()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");

        await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = record.Id });
        var snap = new ClientNepRecordSnapshot
        {
            Id = serverId,
            Telar = "STILL-ALIVE",
            Neps = 25,
            ConcurrencyStamp = "Y"
        };
        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results =
            [
                new ClientSyncOperationResultDto
                {
                    ClientOperationId = req.Operations[0].ClientOperationId,
                    Result = ClientSyncResultNames.Conflict,
                    ServerConcurrencyStamp = "Y",
                    ServerSnapshot = StableJson(snap)
                }
            ]
        };
        api.PullHandler = _ => EmptyPull(0);
        await engine.SyncAsync();

        var conflictOp = await db.PendingOperations.SingleAsync(o =>
            o.Status == PendingOperationStatus.Conflict);
        var result = await resolver.KeepServerAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Success, result.Outcome);
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.False(local.IsDeleted);
        Assert.Equal("STILL-ALIVE", local.Telar);
        Assert.Equal("Y", local.ConcurrencyStamp);
        Assert.Equal(LocalSyncStatus.Synced, local.SyncStatus);
    }

    [Fact]
    public async Task DeleteDelete_EntityDeleted_KeepServer_No_Second_Tombstone_Push()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db);

        await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = record.Id });
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
        api.PullHandler = _ => EmptyPull(0);
        await engine.SyncAsync();

        var conflictOp = await db.PendingOperations.SingleAsync(o =>
            o.OperationType == OfflineOperationType.DeleteRecord);
        Assert.Equal(OfflineConflictKind.DeleteDelete, ConflictKindClassifier.Classify(
            await db.PendingOperations.Include(o => o.LocalNepRecord).SingleAsync(o => o.Id == conflictOp.Id)));

        api.PushCalls = 0;
        await resolver.KeepServerAsync(conflictOp.Id);
        await engine.SyncAsync();
        Assert.Equal(0, api.PushCalls);
        Assert.True((await db.LocalNepRecords.SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task KeepLocal_Lost_EditPermission_Rejected_Conflict_Preserved()
    {
        await using var db = CreateContext();
        var (capture, sessions, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
        var conflictOp = await ForceUpdateConflictAsync(
            capture, engine, api, db, record.Id, serverId,
            "LOCAL", 55, "SERVER", 30, "Y");

        await sessions.UpsertUxSnapshotAsync(
            "user-a",
            "alice",
            "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission, "ViewRecords"],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        var result = await resolver.KeepLocalAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Unauthorized, result.Outcome);
        Assert.Equal(PendingOperationStatus.Conflict,
            (await db.PendingOperations.SingleAsync(o => o.Id == conflictOp.Id)).Status);
        Assert.Equal("LOCAL", (await db.LocalNepRecords.SingleAsync()).Telar);
    }

    [Fact]
    public async Task SeesAll_Admin_Can_KeepServer_Other_Users_Conflict()
    {
        await using var db = CreateContext();
        var (capture, sessions, resolver, engine, api) = await BootAsync(db, userId: "user-a");
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
        var conflictOp = await ForceUpdateConflictAsync(
            capture, engine, api, db, record.Id, serverId,
            "LOCAL", 55, "SERVER", 30, "Y");

        await sessions.UpsertUxSnapshotAsync(
            "admin-1",
            "admin",
            "Admin",
            [
                OfflineStoreConstants.EditRecordsPermission,
                OfflineStoreConstants.DeleteRecordsPermission,
                "ViewRecords"
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        var view = await resolver.GetViewAsync(conflictOp.Id);
        Assert.NotNull(view);
        var result = await resolver.KeepServerAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Success, result.Outcome);
        Assert.Contains("admin-1", (await db.PendingOperations.SingleAsync(o => o.Id == conflictOp.Id)).LastError);
    }

    [Fact]
    public async Task DoubleClick_KeepServer_Second_Is_AlreadyResolved()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
        var conflictOp = await ForceUpdateConflictAsync(
            capture, engine, api, db, record.Id, serverId,
            "LOCAL", 55, "SERVER", 30, "Y");

        var first = await resolver.KeepServerAsync(conflictOp.Id);
        var second = await resolver.KeepServerAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Success, first.Outcome);
        Assert.Equal(ConflictResolutionOutcome.AlreadyResolved, second.Outcome);
        Assert.Equal(0, await db.PendingOperations.CountAsync(o =>
            o.Status == PendingOperationStatus.Pending));
    }

    [Fact]
    public async Task DoubleClick_KeepLocal_Only_One_Pending_Effective()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
        var conflictOp = await ForceUpdateConflictAsync(
            capture, engine, api, db, record.Id, serverId,
            "LOCAL", 55, "SERVER", 30, "Y");

        var first = await resolver.KeepLocalAsync(conflictOp.Id);
        var second = await resolver.KeepLocalAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Success, first.Outcome);
        Assert.Equal(ConflictResolutionOutcome.AlreadyResolved, second.Outcome);
        Assert.Equal(1, await db.PendingOperations.CountAsync(o =>
            o.Status == PendingOperationStatus.Pending
            && o.OperationType == OfflineOperationType.UpdateRecord));
    }

    [Fact]
    public async Task KeepLocal_Push_Duplicate_Same_ClientOpId_Is_Idempotent()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
        var conflictOp = await ForceUpdateConflictAsync(
            capture, engine, api, db, record.Id, serverId,
            "LOCAL", 55, "SERVER", 30, "Y");
        var keep = await resolver.KeepLocalAsync(conflictOp.Id);
        var clientOp = keep.NewClientOperationId!;

        var acceptCount = 0;
        api.PushHandler = req =>
        {
            acceptCount++;
            var op = req.Operations[0];
            Assert.Equal(clientOp, op.ClientOperationId);
            return new ClientSyncPushResponse
            {
                Results =
                [
                    new ClientSyncOperationResultDto
                    {
                        ClientOperationId = op.ClientOperationId,
                        Result = acceptCount == 1
                            ? ClientSyncResultNames.Accepted
                            : ClientSyncResultNames.Duplicate,
                        EntityId = serverId,
                        ConcurrencyStamp = "Y2"
                    }
                ]
            };
        };

        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.Synced,
            (await db.PendingOperations.SingleAsync(o => o.Id == keep.NewOperationId)).Status);

        // Simula timeout tras commit: reabrir como Pending y reenviar mismo OpId → Duplicate.
        var synced = await db.PendingOperations.SingleAsync(o => o.Id == keep.NewOperationId);
        synced.Status = PendingOperationStatus.Pending;
        (await db.LocalNepRecords.SingleAsync()).SyncStatus = LocalSyncStatus.PendingSync;
        await db.SaveChangesAsync();

        await engine.SyncAsync();
        Assert.Equal(2, acceptCount);
        Assert.Equal(PendingOperationStatus.Synced,
            (await db.PendingOperations.SingleAsync(o => o.Id == keep.NewOperationId)).Status);
    }

    [Fact]
    public async Task KeepLocal_Forbidden_Leaves_New_Op_SyncError_No_Silent_Resolve()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
        var conflictOp = await ForceUpdateConflictAsync(
            capture, engine, api, db, record.Id, serverId,
            "LOCAL", 55, "SERVER", 30, "Y");
        var keep = await resolver.KeepLocalAsync(conflictOp.Id);

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
        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.SyncError,
            (await db.PendingOperations.SingleAsync(o => o.Id == keep.NewOperationId)).Status);
        Assert.Equal("LOCAL", (await db.LocalNepRecords.SingleAsync()).Telar);
    }

    [Fact]
    public async Task Persistence_Across_Context_Restart()
    {
        Guid conflictId;
        await using (var db = CreateContext())
        {
            var (capture, _, resolver, engine, api) = await BootAsync(db);
            var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
            var conflictOp = await ForceUpdateConflictAsync(
                capture, engine, api, db, record.Id, serverId,
                "LOCAL", 55, "SERVER", 30, "Y");
            conflictId = conflictOp.Id;
            await resolver.KeepServerAsync(conflictId);
        }

        await using (var db2 = CreateContext())
        {
            var op = await db2.PendingOperations.SingleAsync(o => o.Id == conflictId);
            Assert.Equal(PendingOperationStatus.Cancelled, op.Status);
            Assert.StartsWith(ConflictResolutionService.ResolutionMarkerKeepServer, op.LastError);
            var local = await db2.LocalNepRecords.SingleAsync();
            Assert.Equal("SERVER", local.Telar);
            Assert.Equal(LocalSyncStatus.Synced, local.SyncStatus);
        }
    }

    [Fact]
    public async Task View_Classifies_Kinds_And_Actions()
    {
        await using var db = CreateContext();
        var (capture, _, resolver, engine, api) = await BootAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");
        var conflictOp = await ForceUpdateConflictAsync(
            capture, engine, api, db, record.Id, serverId,
            "LOCAL", 55, "SERVER", 30, "Y");
        var view = await resolver.GetViewAsync(conflictOp.Id);
        Assert.NotNull(view);
        Assert.Equal(OfflineConflictKind.UpdateUpdate, view!.Kind);
        Assert.Contains(view.Differences, d => d.FieldName == "Telar");
        Assert.True(view.AllowedActions.HasFlag(ConflictResolutionActions.KeepServer));
        Assert.True(view.AllowedActions.HasFlag(ConflictResolutionActions.KeepLocal));
        Assert.True(view.AllowedActions.HasFlag(ConflictResolutionActions.EditAndRetry));
    }

    private static JsonElement StableJson(object value)
    {
        var json = JsonSerializer.Serialize(value, ClientSyncJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static ClientSyncPullResponse EmptyPull(long cursor) => new()
    {
        Changes = [],
        NextCursor = cursor,
        ServerTimeUtc = DateTime.UtcNow,
        HasMore = false
    };

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
}
