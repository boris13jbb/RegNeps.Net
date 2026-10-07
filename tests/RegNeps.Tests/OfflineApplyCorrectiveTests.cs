using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RegNeps.Application.Permissions;
using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
using RegNeps.Domain.Services;
using RegNeps.Domain.Sync;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;
using RegNeps.Infrastructure.Sync;
using RegNeps.OfflineStore;
using RegNeps.OfflineStore.Device;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;
using RegNeps.OfflineStore.Sync.Contracts;

namespace RegNeps.Tests;

/// <summary>FASE 2D.10 — ApplyCorrective offline: Outbox, conflicto 2D.7, Push E2E.</summary>
public sealed class OfflineApplyCorrectiveTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;
    private readonly SqliteConnection _serverConn;
    private readonly DbContextOptions<RegNepsDbContext> _serverOptions;
    private TestDbFactory _serverFactory = null!;
    private Guid _userId;
    private Guid _adminId;

    public OfflineApplyCorrectiveTests()
    {
        _serverConn = new SqliteConnection("Data Source=:memory:");
        _serverConn.Open();
        _serverOptions = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(_serverConn)
            .Options;
    }

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-2d10-corr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "offline.db");
        await using var offline = CreateOfflineDb();
        await offline.Database.MigrateAsync();

        _serverFactory = new TestDbFactory(_serverOptions);
        await using var db = _serverFactory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitializer.ApplySchemaPatchesAsync(db);
        await DbSeeder.SeedAsync(db);

        var hash = BCrypt.Net.BCrypt.HashPassword("2D10!");
        var user = new AppUser
        {
            Username = "corr_offline",
            DisplayName = "Corr Offline",
            PasswordHash = hash,
            Role = AppUserRole.Operario,
            RoleCode = "Operario",
            IsActive = true
        };
        var admin = new AppUser
        {
            Username = "corr_offline_admin",
            DisplayName = "Admin Offline",
            PasswordHash = hash,
            Role = AppUserRole.Admin,
            RoleCode = "Admin",
            IsActive = true
        };
        db.Users.AddRange(user, admin);
        await db.SaveChangesAsync();
        _userId = user.Id;
        _adminId = admin.Id;

        await new SyncPersistence(_serverFactory, new AtomicNepRecordCreateStore(_serverFactory))
            .EnsureCatalogBaselineAsync();
    }

    public async Task DisposeAsync()
    {
        _serverConn.Dispose();
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

        await Task.CompletedTask;
    }

    private LocalSyncDbContext CreateOfflineDb(params IInterceptor[] interceptors)
    {
        var b = new DbContextOptionsBuilder<LocalSyncDbContext>()
            .UseSqlite($"Data Source={_dbPath}");
        if (interceptors.Length > 0)
        {
            b.AddInterceptors(interceptors);
        }

        return new LocalSyncDbContext(b.Options);
    }

    private SyncAppService CreateServerSync()
    {
        var db = _serverFactory.CreateDbContext();
        return new SyncAppService(
            new SyncPersistence(_serverFactory, new AtomicNepRecordCreateStore(_serverFactory)),
            new PermissionService(
                new RolePermissionRepository(db),
                new RoleRepository(db),
                new PermissionMatrix()),
            NullLogger<SyncAppService>.Instance);
    }

    private RecordActor ActorUser() =>
        RecordActor.Create(_userId.ToString(), "corr_offline", "Corr Offline",
            AppUserRole.Operario, false, null, "Operario", false);

    private RecordActor ActorAdmin() =>
        RecordActor.Create(_adminId.ToString(), "corr_offline_admin", "Admin Offline",
            AppUserRole.Admin, false, null, "Admin", true);

    private async Task<(
            OfflineCaptureService Capture,
            OfflineSessionService Sessions,
            ConflictResolutionService Resolver,
            SyncEngine Engine,
            FakeApi Api)>
        BootLocalAsync(
            LocalSyncDbContext db,
            string[]? permissions = null)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM PendingOperations");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalNepRecords");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalSessions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SyncStates");
        db.ChangeTracker.Clear();

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-" + Guid.NewGuid().ToString("N")[..8]));
        var capture = new OfflineCaptureService(db, sessions, devices);
        var resolver = new ConflictResolutionService(db, sessions, devices);
        var api = new FakeApi();
        var engine = new SyncEngine(db, sessions, devices, api, new StaticCookie("RegNeps.Auth=t"));

        permissions ??=
        [
            OfflineStoreConstants.CaptureRecordsPermission,
            OfflineStoreConstants.EditRecordsPermission,
            OfflineStoreConstants.DeleteRecordsPermission,
            OfflineStoreConstants.ApplyCorrectiveActionPermission,
            "ViewRecords"
        ];

        // Admin: tiene ApplyCorrectiveAction en servidor (Operario no).
        await sessions.UpsertUxSnapshotAsync(
            _adminId.ToString(),
            "corr_offline_admin",
            "Admin",
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
        double neps = 18,
        string stamp = "stamp-v1")
    {
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = "100",
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

    [Fact]
    public async Task Outbox_ApplyCorrective_Atomic_Persists_Payload_And_Stamp()
    {
        await using var db = CreateOfflineDb();
        var (capture, _, _, _, _) = await BootLocalAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db);

        var result = await capture.ApplyCorrectiveAsync(new OfflineApplyCorrectiveRequest
        {
            LocalRecordId = record.Id,
            Accion = "Ajuste offline",
            Responsable = "Sup",
            MarcarRevisado = true
        });

        Assert.Equal(OfflineOperationType.ApplyCorrective, result.Operation.OperationType);
        Assert.Equal(PendingOperationStatus.Pending, result.Operation.Status);
        Assert.Equal("stamp-v1", result.Operation.ExpectedConcurrencyStamp);
        Assert.Equal(serverId, result.Operation.TargetServerRecordId);
        Assert.Equal(record.CaptureSessionId, result.Operation.CaptureSessionId);
        Assert.False(string.IsNullOrWhiteSpace(result.Operation.ClientOperationId));
        Assert.Equal(LocalSyncStatus.PendingSync, result.Record.SyncStatus);
        Assert.Equal("Ajuste offline", result.Record.AccionCorrectiva);
        Assert.Equal("Sup", result.Record.ResponsableRevision);
        Assert.True(result.Record.RevisadoPorSupervisor);
        Assert.Equal(18, result.Record.Neps);
        Assert.Equal(AlertLevel.Ok, result.QualityLevel);

        var payload = JsonSerializer.Deserialize<ApplyCorrectivePayload>(
            result.Operation.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(payload);
        Assert.Equal(serverId, payload!.EntityId);
        Assert.Equal("Ajuste offline", payload.Accion);
        Assert.DoesNotContain("telar", result.Operation.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("neps", result.Operation.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Outbox_ApplyCorrective_Rollback_Leaves_Neither()
    {
        await using var db = CreateOfflineDb();
        var (capture, _, _, _, _) = await BootLocalAsync(db);
        var (record, _) = await SeedSyncedAsync(capture, db);

        await using (var failDb = CreateOfflineDb(new FailOnPendingOperationInsertInterceptor()))
        {
            var sessions = new OfflineSessionService(failDb, new MemorySecureAuthMaterialStore());
            var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-fail"));
            var failCapture = new OfflineCaptureService(failDb, sessions, devices);
            await sessions.UpsertUxSnapshotAsync(
                _adminId.ToString(),
                "corr_offline_admin",
                "Admin",
                [
                    OfflineStoreConstants.CaptureRecordsPermission,
                    OfflineStoreConstants.ApplyCorrectiveActionPermission
                ],
                "http://localhost:5080",
                TimeSpan.FromHours(72));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                failCapture.ApplyCorrectiveAsync(new OfflineApplyCorrectiveRequest
                {
                    LocalRecordId = record.Id,
                    Accion = "No debe persistir",
                    Responsable = "X"
                }));
        }

        await using var check = CreateOfflineDb();
        var local = await check.LocalNepRecords.SingleAsync(r => r.Id == record.Id);
        Assert.True(string.IsNullOrEmpty(local.AccionCorrectiva));
        Assert.Equal(LocalSyncStatus.Synced, local.SyncStatus);
        Assert.Equal(0, await check.PendingOperations.CountAsync(o =>
            o.OperationType == OfflineOperationType.ApplyCorrective));
    }

    [Fact]
    public async Task Eligibility_Blocks_Without_Permission_And_Pending_Mutation()
    {
        await using var db = CreateOfflineDb();
        var (capture, _, _, _, _) = await BootLocalAsync(db,
        [
            OfflineStoreConstants.CaptureRecordsPermission,
            "ViewRecords"
        ]);
        var (record, _) = await SeedSyncedAsync(capture, db);

        var elig = await capture.GetCorrectiveEligibilityAsync(record.Id);
        Assert.False(elig.CanApply);
        Assert.Equal(OfflineCorrectiveBlockReason.NoCorrectivePermission, elig.Reason);

        await using var db2 = CreateOfflineDb();
        var (capture2, _, _, _, _) = await BootLocalAsync(db2);
        var (record2, _) = await SeedSyncedAsync(capture2, db2);
        await capture2.ApplyCorrectiveAsync(new OfflineApplyCorrectiveRequest
        {
            LocalRecordId = record2.Id,
            Accion = "Primera",
            Responsable = "R"
        });
        var elig2 = await capture2.GetCorrectiveEligibilityAsync(record2.Id);
        Assert.False(elig2.CanApply);
        Assert.Equal(OfflineCorrectiveBlockReason.MutationAlreadyPending, elig2.Reason);
    }

    [Fact]
    public async Task Conflict_UpdateUpdate_KeepServer_KeepLocal_EditRetry()
    {
        await using var db = CreateOfflineDb();
        var (capture, _, resolver, engine, api) = await BootLocalAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db, stamp: "X");

        await capture.ApplyCorrectiveAsync(new OfflineApplyCorrectiveRequest
        {
            LocalRecordId = record.Id,
            Accion = "Local corr",
            Responsable = "Local R",
            MarcarRevisado = true
        });

        var snap = new ClientNepRecordSnapshot
        {
            Id = serverId,
            Telar = "100",
            Neps = 18,
            Tela = "Denim",
            LoteTrama = "63E26401",
            Turno = "A",
            ConcurrencyStamp = "Y",
            AccionCorrectiva = "Server corr",
            ResponsableRevision = "Server R"
        };

        api.PushHandler = req =>
        {
            Assert.Equal(SyncConstants.OperationApplyCorrective, req.Operations[0].OperationType);
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
                        ServerConcurrencyStamp = "Y",
                        ServerSnapshot = StableJson(snap)
                    }
                ]
            };
        };
        api.PullHandler = _ => new ClientSyncPullResponse
        {
            ProtocolVersion = SyncProtocol.Version,
            NextCursor = 0,
            HasMore = false,
            Changes = []
        };
        await engine.SyncAsync();

        var conflictOp = await db.PendingOperations.SingleAsync(o =>
            o.OperationType == OfflineOperationType.ApplyCorrective
            && o.Status == PendingOperationStatus.Conflict);

        var view = await resolver.GetViewAsync(conflictOp.Id);
        Assert.NotNull(view);
        Assert.Equal(OfflineConflictKind.UpdateUpdate, view!.Kind);
        Assert.True(view.AllowedActions.HasFlag(ConflictResolutionActions.KeepServer));
        Assert.True(view.AllowedActions.HasFlag(ConflictResolutionActions.KeepLocal));
        Assert.True(view.AllowedActions.HasFlag(ConflictResolutionActions.EditAndRetry));
        Assert.Contains("correctiva", view.KindLabel, StringComparison.OrdinalIgnoreCase);

        // Keep Local reencola ApplyCorrective (nunca UpdateRecord).
        var keepLocal = await resolver.KeepLocalAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Success, keepLocal.Outcome);
        var newOp = await db.PendingOperations.SingleAsync(o => o.Id == keepLocal.NewOperationId);
        Assert.Equal(OfflineOperationType.ApplyCorrective, newOp.OperationType);
        Assert.Equal("Y", newOp.ExpectedConcurrencyStamp);
        Assert.NotEqual(conflictOp.ClientOperationId, newOp.ClientOperationId);

        // Forzar segundo conflicto y Edit&Retry.
        var record2 = await db.LocalNepRecords.SingleAsync();
        record2.ConcurrencyStamp = "Y";
        record2.SyncStatus = LocalSyncStatus.Synced;
        newOp.Status = PendingOperationStatus.Synced;
        await db.SaveChangesAsync();

        await capture.ApplyCorrectiveAsync(new OfflineApplyCorrectiveRequest
        {
            LocalRecordId = record2.Id,
            Accion = "Retry base",
            Responsable = "R0"
        });
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
                    ServerConcurrencyStamp = "Z",
                    ServerSnapshot = StableJson(new ClientNepRecordSnapshot
                    {
                        Id = serverId,
                        Telar = "100",
                        Neps = 18,
                        ConcurrencyStamp = "Z",
                        AccionCorrectiva = "Srv2"
                    })
                }
            ]
        };
        await engine.SyncAsync();
        var conflict2 = await db.PendingOperations.SingleAsync(o =>
            o.OperationType == OfflineOperationType.ApplyCorrective
            && o.Status == PendingOperationStatus.Conflict);

        var edit = await resolver.EditAndRetryAsync(conflict2.Id, new ConflictEditFields
        {
            AccionCorrectiva = "Editada",
            ResponsableRevision = "R-edit",
            MarcarRevisado = true
        });
        Assert.Equal(ConflictResolutionOutcome.Success, edit.Outcome);
        var editedOp = await db.PendingOperations.SingleAsync(o => o.Id == edit.NewOperationId);
        Assert.Equal(OfflineOperationType.ApplyCorrective, editedOp.OperationType);
        Assert.Equal("Z", editedOp.ExpectedConcurrencyStamp);
        var editedPayload = JsonSerializer.Deserialize<ApplyCorrectivePayload>(
            editedOp.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal("Editada", editedPayload!.Accion);

        // Keep Server sobre un tercer conflicto.
        record2 = await db.LocalNepRecords.SingleAsync();
        record2.ConcurrencyStamp = "Z";
        record2.SyncStatus = LocalSyncStatus.Synced;
        editedOp.Status = PendingOperationStatus.Synced;
        await db.SaveChangesAsync();
        await capture.ApplyCorrectiveAsync(new OfflineApplyCorrectiveRequest
        {
            LocalRecordId = record2.Id,
            Accion = "Will discard",
            Responsable = "X"
        });
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
                    ServerConcurrencyStamp = "W",
                    ServerSnapshot = StableJson(new ClientNepRecordSnapshot
                    {
                        Id = serverId,
                        Telar = "100",
                        Neps = 18,
                        ConcurrencyStamp = "W",
                        AccionCorrectiva = "KeepServerVal"
                    })
                }
            ]
        };
        await engine.SyncAsync();
        var conflict3 = await db.PendingOperations.SingleAsync(o =>
            o.OperationType == OfflineOperationType.ApplyCorrective
            && o.Status == PendingOperationStatus.Conflict);
        var keepServer = await resolver.KeepServerAsync(conflict3.Id);
        Assert.Equal(ConflictResolutionOutcome.Success, keepServer.Outcome);
        var after = await db.LocalNepRecords.SingleAsync();
        Assert.Equal("KeepServerVal", after.AccionCorrectiva);
        Assert.Equal("W", after.ConcurrencyStamp);
        Assert.Equal(LocalSyncStatus.Synced, after.SyncStatus);
    }

    [Fact]
    public async Task Conflict_UpdateDelete_Only_KeepServer_Allowed()
    {
        await using var db = CreateOfflineDb();
        var (capture, _, resolver, engine, api) = await BootLocalAsync(db);
        var (record, serverId) = await SeedSyncedAsync(capture, db);

        await capture.ApplyCorrectiveAsync(new OfflineApplyCorrectiveRequest
        {
            LocalRecordId = record.Id,
            Accion = "Sobre eliminado",
            Responsable = "R"
        });

        api.PushHandler = req => new ClientSyncPushResponse
        {
            Results =
            [
                new ClientSyncOperationResultDto
                {
                    ClientOperationId = req.Operations[0].ClientOperationId,
                    Result = ClientSyncResultNames.Conflict,
                    ErrorCode = "ENTITY_DELETED",
                    EntityId = serverId,
                    ServerSnapshot = StableJson(new ClientNepRecordDeletedSnapshot
                    {
                        Id = serverId,
                        OwnerUserId = _adminId.ToString(),
                        DeletedAtUtc = DateTime.UtcNow,
                        LastConcurrencyStamp = "gone"
                    })
                }
            ]
        };
        api.PullHandler = _ => new ClientSyncPullResponse
        {
            ProtocolVersion = SyncProtocol.Version,
            NextCursor = 0,
            HasMore = false,
            Changes = []
        };
        await engine.SyncAsync();

        var conflictOp = await db.PendingOperations.SingleAsync(o =>
            o.Status == PendingOperationStatus.Conflict);
        var view = await resolver.GetViewAsync(conflictOp.Id);
        Assert.NotNull(view);
        Assert.Equal(OfflineConflictKind.UpdateDelete, view!.Kind);
        Assert.True(view.AllowedActions.HasFlag(ConflictResolutionActions.KeepServer));
        Assert.False(view.AllowedActions.HasFlag(ConflictResolutionActions.KeepLocal));
        Assert.False(view.AllowedActions.HasFlag(ConflictResolutionActions.EditAndRetry));

        var rejected = await resolver.KeepLocalAsync(conflictOp.Id);
        Assert.Equal(ConflictResolutionOutcome.Rejected, rejected.Outcome);
    }

    [Fact]
    public async Task E2E_Offline_ApplyCorrective_Push_Pull_No_Loop()
    {
        await using var offline = CreateOfflineDb();
        var sessions = new OfflineSessionService(offline, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "e2e-dev"));
        var capture = new OfflineCaptureService(offline, sessions, devices);
        var server = CreateServerSync();
        var actor = ActorAdmin();
        var api = new BridgeApi(server, () => actor);
        var engine = new SyncEngine(offline, sessions, devices, api, new StaticCookie("RegNeps.Auth=e2e"));

        await sessions.UpsertUxSnapshotAsync(
            _adminId.ToString(),
            "corr_offline_admin",
            "Admin",
            [
                OfflineStoreConstants.CaptureRecordsPermission,
                OfflineStoreConstants.EditRecordsPermission,
                OfflineStoreConstants.ApplyCorrectiveActionPermission,
                "ViewRecords"
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));
        offline.SyncStates.Add(new SyncState { Id = 1, DeviceId = "e2e", LastPulledSequence = 0 });
        await offline.SaveChangesAsync();

        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = "200",
            Neps = 45,
            Tela = "Denim",
            LoteTrama = "L1",
            Turno = "A"
        });
        var syncCreate = await engine.SyncAsync();
        Assert.True(syncCreate.PushedAccepted + syncCreate.PushedDuplicate >= 1);

        var local = await offline.LocalNepRecords.SingleAsync(r => r.Id == created.Record.Id);
        Assert.NotNull(local.ServerRecordId);
        Assert.False(string.IsNullOrWhiteSpace(local.ConcurrencyStamp));

        var corr = await capture.ApplyCorrectiveAsync(new OfflineApplyCorrectiveRequest
        {
            LocalRecordId = local.Id,
            Accion = "E2E correctiva",
            Responsable = "Sup E2E",
            MarcarRevisado = true
        });
        Assert.Equal(OfflineOperationType.ApplyCorrective, corr.Operation.OperationType);
        Assert.Equal(AlertLevel.Mention, corr.QualityLevel);

        var syncCorr = await engine.SyncAsync();
        Assert.True(syncCorr.PushedAccepted + syncCorr.PushedDuplicate >= 1);

        var pending = await offline.PendingOperations
            .Where(o => o.OperationType == OfflineOperationType.ApplyCorrective)
            .ToListAsync();
        Assert.All(pending, o => Assert.Equal(PendingOperationStatus.Synced, o.Status));

        await using var serverDb = _serverFactory.CreateDbContext();
        var entity = await serverDb.NepRecords.SingleAsync(r => r.Id == local.ServerRecordId);
        Assert.Equal("E2E correctiva", entity.AccionCorrectiva);
        Assert.Equal(45, entity.Neps);
        Assert.Equal(AlertLevel.Mention, AlertEvaluator.GetLevel(entity.Neps));

        // Re-pull: no crea Outbox ni loop.
        var opsBefore = await offline.PendingOperations.CountAsync();
        await engine.SyncAsync();
        Assert.Equal(opsBefore, await offline.PendingOperations.CountAsync());
        Assert.Equal(0, await offline.PendingOperations.CountAsync(o =>
            o.Status == PendingOperationStatus.Pending
            && o.OperationType == OfflineOperationType.ApplyCorrective));

        var localAfter = await offline.LocalNepRecords.SingleAsync(r => r.Id == local.Id);
        Assert.Equal("E2E correctiva", localAfter.AccionCorrectiva);
        Assert.Equal(entity.ConcurrencyStamp, localAfter.ConcurrencyStamp);
    }

    private static JsonElement StableJson(object value)
    {
        var json = JsonSerializer.Serialize(value, ClientSyncJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class FakeApi : ISyncApiClient
    {
        public int PushCalls { get; set; }
        public Func<ClientSyncPushRequest, ClientSyncPushResponse>? PushHandler { get; set; }
        public Func<ClientSyncPullRequest, ClientSyncPullResponse>? PullHandler { get; set; }

        public Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPushRequest request, CancellationToken ct = default)
        {
            PushCalls++;
            return Task.FromResult(PushHandler?.Invoke(request)
                                   ?? new ClientSyncPushResponse { Results = [] });
        }

        public Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPullRequest request, CancellationToken ct = default) =>
            Task.FromResult(PullHandler?.Invoke(request) ?? new ClientSyncPullResponse
            {
                ProtocolVersion = SyncProtocol.Version,
                NextCursor = request.Cursor,
                HasMore = false,
                Changes = []
            });
    }

    private sealed class BridgeApi : ISyncApiClient
    {
        private readonly SyncAppService _sync;
        private readonly Func<RecordActor> _actor;

        public BridgeApi(SyncAppService sync, Func<RecordActor> actor)
        {
            _sync = sync;
            _actor = actor;
        }

        public async Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPushRequest request, CancellationToken ct = default)
        {
            var response = await _sync.PushAsync(new SyncPushRequest
            {
                ProtocolVersion = request.ProtocolVersion,
                DeviceId = request.DeviceId,
                Operations = request.Operations.Select(o => new SyncOperationDto
                {
                    ClientOperationId = o.ClientOperationId,
                    OperationType = o.OperationType,
                    ExpectedConcurrencyStamp = o.ExpectedConcurrencyStamp,
                    CaptureSessionId = o.CaptureSessionId,
                    Payload = o.Payload
                }).ToList()
            }, _actor(), "2d10-e2e", ct);

            return new ClientSyncPushResponse
            {
                Results = response.Results.Select(r => new ClientSyncOperationResultDto
                {
                    ClientOperationId = r.ClientOperationId,
                    Result = r.Result,
                    ErrorCode = r.ErrorCode,
                    Message = r.Message,
                    EntityId = r.EntityId,
                    ConcurrencyStamp = r.ConcurrencyStamp,
                    QualityLabel = r.QualityLabel,
                    ChangeSequence = r.ChangeSequence,
                    ServerConcurrencyStamp = r.ServerConcurrencyStamp,
                    ServerSnapshot = r.ServerSnapshot
                }).ToList()
            };
        }

        public async Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPullRequest request, CancellationToken ct = default)
        {
            var response = await _sync.PullAsync(new SyncPullRequest
            {
                ProtocolVersion = request.ProtocolVersion,
                DeviceId = request.DeviceId,
                Cursor = request.Cursor,
                PageSize = request.PageSize
            }, _actor(), "2d10-pull", ct);

            return new ClientSyncPullResponse
            {
                ProtocolVersion = response.ProtocolVersion,
                ServerTimeUtc = response.ServerTimeUtc,
                NextCursor = response.NextCursor,
                HasMore = response.HasMore,
                Changes = response.Changes.Select(c => new ClientSyncChangeDto
                {
                    Sequence = c.Sequence,
                    EntityType = c.EntityType,
                    EntityId = c.EntityId,
                    ChangeType = c.ChangeType,
                    OccurredAtUtc = c.OccurredAtUtc,
                    Payload = c.Payload
                }).ToList()
            };
        }
    }

    private sealed class StaticCookie : ISyncAuthCookieProvider
    {
        private readonly string? _c;
        public StaticCookie(string? c) => _c = c;
        public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default) =>
            Task.FromResult(_c);
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

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
