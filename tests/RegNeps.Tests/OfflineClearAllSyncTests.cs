using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RegNeps.Application.Permissions;
using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
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

/// <summary>FASE 2D.12 — ClearAll sync-safe vs Outbox/Pull offline.</summary>
public sealed class OfflineClearAllSyncTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _offlineDbPath = null!;
    private readonly SqliteConnection _serverConn;
    private readonly DbContextOptions<RegNepsDbContext> _serverOptions;
    private TestDbFactory _serverFactory = null!;
    private Guid _adminId;

    public OfflineClearAllSyncTests()
    {
        _serverConn = new SqliteConnection("Data Source=:memory:");
        _serverConn.Open();
        _serverOptions = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(_serverConn)
            .Options;
    }

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-2d12-clr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _offlineDbPath = Path.Combine(_dir, "offline.db");
        await using var offline = CreateOfflineDb();
        await offline.Database.MigrateAsync();

        _serverFactory = new TestDbFactory(_serverOptions);
        await using var db = _serverFactory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitializer.ApplySchemaPatchesAsync(db);
        await DbSeeder.SeedAsync(db);

        var admin = new AppUser
        {
            Username = "clr_off_admin",
            DisplayName = "Admin Off",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("2D12!"),
            Role = AppUserRole.Admin,
            RoleCode = "Admin",
            IsActive = true
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
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

    private LocalSyncDbContext CreateOfflineDb() =>
        new(new DbContextOptionsBuilder<LocalSyncDbContext>()
            .UseSqlite($"Data Source={_offlineDbPath}")
            .Options);

    private NepRecordService CreateOnlineService()
    {
        var db = _serverFactory.CreateDbContext();
        var atomic = new AtomicNepRecordCreateStore(_serverFactory);
        return new NepRecordService(
            new NepRecordRepository(_serverFactory),
            new AlertConfigRepository(db),
            permissions: new PermissionService(
                new RolePermissionRepository(db),
                new RoleRepository(db),
                new PermissionMatrix()),
            atomicCreate: atomic);
    }

    private SyncAppService CreateSync()
    {
        var db = _serverFactory.CreateDbContext();
        var atomic = new AtomicNepRecordCreateStore(_serverFactory);
        return new SyncAppService(
            new SyncPersistence(_serverFactory, atomic),
            new PermissionService(
                new RolePermissionRepository(db),
                new RoleRepository(db),
                new PermissionMatrix()),
            NullLogger<SyncAppService>.Instance);
    }

    private RecordActor ActorAdmin() =>
        RecordActor.Create(_adminId.ToString(), "clr_off_admin", "Admin Off",
            AppUserRole.Admin, false, null, "Admin", true);

    private async Task<(
            SyncEngine Engine,
            OfflineCaptureService Capture,
            LocalSyncDbContext Offline,
            BridgeApi Api)>
        BootOfflineAsync()
    {
        var offline = CreateOfflineDb();
        await offline.Database.ExecuteSqlRawAsync("DELETE FROM PendingOperations");
        await offline.Database.ExecuteSqlRawAsync("DELETE FROM LocalNepRecords");
        await offline.Database.ExecuteSqlRawAsync("DELETE FROM LocalSessions");
        await offline.Database.ExecuteSqlRawAsync("DELETE FROM SyncStates");
        offline.ChangeTracker.Clear();

        var sessions = new OfflineSessionService(offline, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-" + Guid.NewGuid().ToString("N")[..8]));
        var capture = new OfflineCaptureService(offline, sessions, devices);
        var sync = CreateSync();
        var api = new BridgeApi(sync, ActorAdmin);
        var engine = new SyncEngine(offline, sessions, devices, api, new StaticCookie("RegNeps.Auth=t"));

        await sessions.UpsertUxSnapshotAsync(
            _adminId.ToString(),
            "clr_off_admin",
            "Admin",
            [
                OfflineStoreConstants.CaptureRecordsPermission,
                OfflineStoreConstants.EditRecordsPermission,
                OfflineStoreConstants.DeleteRecordsPermission,
                OfflineStoreConstants.ApplyCorrectiveActionPermission,
                "ViewRecords"
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));
        offline.SyncStates.Add(new SyncState { Id = 1, DeviceId = "clr-dev", LastPulledSequence = 0 });
        await offline.SaveChangesAsync();
        return (engine, capture, offline, api);
    }

    [Fact]
    public async Task Pending_Create_Survives_ClearAll_And_Can_Push_After()
    {
        // Servidor tiene registros; cliente tiene Create Pending sin ServerId.
        var online = CreateOnlineService();
        await online.ClearAllAsync(ActorAdmin());
        await online.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "SRV",
            Neps = 10,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, ActorAdmin());

        var (engine, capture, offline, _) = await BootOfflineAsync();
        await engine.SyncAsync(); // pull servidor

        var pendingCreate = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = "LOCAL-NEW",
            Neps = 18,
            Tela = "Denim",
            LoteTrama = "L1",
            Turno = "A"
        });
        Assert.Null(pendingCreate.Record.ServerRecordId);
        Assert.Equal(PendingOperationStatus.Pending, pendingCreate.Operation.Status);

        await online.ClearAllAsync(ActorAdmin());
        // Sync = Push (Create Pending → Accepted) luego Pull (tombstones del ClearAll).
        var syncAfterClear = await engine.SyncAsync();
        Assert.True(syncAfterClear.PushedAccepted + syncAfterClear.PushedDuplicate >= 1);
        Assert.True(syncAfterClear.PulledDeletes >= 1);

        var localCreate = await offline.LocalNepRecords.SingleAsync(r => r.Id == pendingCreate.Record.Id);
        Assert.False(localCreate.IsDeleted);
        Assert.NotNull(localCreate.ServerRecordId);
        Assert.Equal(PendingOperationStatus.Synced,
            (await offline.PendingOperations.SingleAsync(o => o.Id == pendingCreate.Operation.Id)).Status);

        await using var server = _serverFactory.CreateDbContext();
        Assert.Equal(1, await server.NepRecords.CountAsync());
        Assert.Equal("LOCAL-NEW", (await server.NepRecords.SingleAsync()).Telar);
    }

    [Fact]
    public async Task Pending_Update_Becomes_Conflict_After_ClearAll_Pull()
    {
        var online = CreateOnlineService();
        await online.ClearAllAsync(ActorAdmin());
        var created = await online.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "U1",
            Neps = 12,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, ActorAdmin());

        var (engine, capture, offline, _) = await BootOfflineAsync();
        await engine.SyncAsync();

        var local = await offline.LocalNepRecords.SingleAsync(r => r.ServerRecordId == created.Id);
        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = local.Id,
            Telar = "U1-EDIT",
            Neps = 30,
            Tela = "Denim",
            LoteTrama = "L1",
            Turno = "B"
        });

        await online.ClearAllAsync(ActorAdmin());
        await engine.SyncAsync();

        var op = await offline.PendingOperations.SingleAsync(o =>
            o.OperationType == OfflineOperationType.UpdateRecord);
        Assert.Equal(PendingOperationStatus.Conflict, op.Status);
        Assert.Equal("ENTITY_DELETED", op.LastServerErrorCode);
        Assert.True((await offline.LocalNepRecords.SingleAsync(r => r.Id == local.Id)).IsDeleted);
    }

    [Fact]
    public async Task Pending_ApplyCorrective_Becomes_Conflict_After_ClearAll()
    {
        var online = CreateOnlineService();
        await online.ClearAllAsync(ActorAdmin());
        var created = await online.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "C1",
            Neps = 45,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, ActorAdmin());

        var (engine, capture, offline, _) = await BootOfflineAsync();
        await engine.SyncAsync();
        var local = await offline.LocalNepRecords.SingleAsync(r => r.ServerRecordId == created.Id);

        await capture.ApplyCorrectiveAsync(new OfflineApplyCorrectiveRequest
        {
            LocalRecordId = local.Id,
            Accion = "Fix",
            Responsable = "Sup",
            MarcarRevisado = true
        });

        await online.ClearAllAsync(ActorAdmin());
        await engine.SyncAsync();

        var op = await offline.PendingOperations.SingleAsync(o =>
            o.OperationType == OfflineOperationType.ApplyCorrective);
        Assert.Equal(PendingOperationStatus.Conflict, op.Status);
        Assert.Equal("ENTITY_DELETED", op.LastServerErrorCode);
    }

    [Fact]
    public async Task Pending_Delete_Is_Idempotent_Or_Conflict_Resolved_As_Deleted()
    {
        var online = CreateOnlineService();
        await online.ClearAllAsync(ActorAdmin());
        var created = await online.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "D1",
            Neps = 12,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, ActorAdmin());

        var (engine, capture, offline, _) = await BootOfflineAsync();
        await engine.SyncAsync();
        var local = await offline.LocalNepRecords.SingleAsync(r => r.ServerRecordId == created.Id);

        await capture.DeleteRecordAsync(new OfflineDeleteRecordRequest { LocalRecordId = local.Id });
        await online.ClearAllAsync(ActorAdmin());
        var result = await engine.SyncAsync();

        // Push Delete puede Accepted/Duplicate/Conflict; réplica local queda eliminada.
        var delOp = await offline.PendingOperations.SingleAsync(o =>
            o.OperationType == OfflineOperationType.DeleteRecord);
        Assert.True(
            delOp.Status is PendingOperationStatus.Synced
                or PendingOperationStatus.Conflict
                or PendingOperationStatus.Cancelled);
        Assert.True((await offline.LocalNepRecords.SingleAsync(r => r.Id == local.Id)).IsDeleted
                    || delOp.Status == PendingOperationStatus.Synced);
        _ = result;
    }

    [Fact]
    public async Task Existing_Conflict_Is_Not_Auto_Cancelled_By_ClearAll_Pull()
    {
        var online = CreateOnlineService();
        await online.ClearAllAsync(ActorAdmin());
        var created = await online.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "K1",
            Neps = 12,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, ActorAdmin());

        var (engine, capture, offline, api) = await BootOfflineAsync();
        await engine.SyncAsync();
        var local = await offline.LocalNepRecords.SingleAsync(r => r.ServerRecordId == created.Id);

        await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
        {
            LocalRecordId = local.Id,
            Telar = "K1-L",
            Neps = 40,
            Tela = "Denim",
            LoteTrama = "L1",
            Turno = "A"
        });

        // Forzar Conflict de UpdateUpdate (stamp obsoleto) antes del ClearAll.
        api.ForceConflictOnce = true;
        api.ConflictEntityId = created.Id;
        api.ConflictStamp = "forced-Y";
        await engine.SyncAsync();

        var conflictOp = await offline.PendingOperations.SingleAsync(o =>
            o.Status == PendingOperationStatus.Conflict);
        var conflictId = conflictOp.Id;

        await online.ClearAllAsync(ActorAdmin());
        api.ForceConflictOnce = false;
        await engine.SyncAsync();

        var still = await offline.PendingOperations.SingleAsync(o => o.Id == conflictId);
        Assert.Equal(PendingOperationStatus.Conflict, still.Status);
        Assert.NotEqual(PendingOperationStatus.Cancelled, still.Status);
    }

    [Fact]
    public async Task ClearAll_Then_Multiple_Creates_Pull_Preserves_New()
    {
        var online = CreateOnlineService();
        var sync = CreateSync();
        await online.ClearAllAsync(ActorAdmin());
        await online.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "OLD",
            Neps = 5,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, ActorAdmin());

        await using var peek = _serverFactory.CreateDbContext();
        var cursor = await peek.SyncChangeLogs.MaxAsync(c => c.Sequence);

        await online.ClearAllAsync(ActorAdmin());
        for (var i = 0; i < 3; i++)
        {
            var push = await sync.PushAsync(new SyncPushRequest
            {
                ProtocolVersion = SyncProtocol.Version,
                DeviceId = "dev",
                Operations =
                [
                    new SyncOperationDto
                    {
                        ClientOperationId = Guid.NewGuid().ToString("N"),
                        OperationType = SyncConstants.OperationCreateRecord,
                        Payload = JsonSerializer.SerializeToElement(new SyncCreateRecordPayload
                        {
                            Telar = $"N{i}",
                            Neps = 10 + i,
                            Tela = "D",
                            LoteTrama = "L",
                            Turno = "A"
                        })
                    }
                ]
            }, ActorAdmin(), $"c{i}");
            Assert.Equal(nameof(SyncOperationResult.Accepted), push.Results[0].Result);
        }

        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-b",
            Cursor = cursor,
            PageSize = 100
        }, ActorAdmin(), "p");

        Assert.Equal(1, pull.Changes.Count(c => c.ChangeType == SyncConstants.ChangeRecordDeleted));
        Assert.Equal(3, pull.Changes.Count(c => c.ChangeType == SyncConstants.ChangeRecordUpserted));
        await using var check = _serverFactory.CreateDbContext();
        Assert.Equal(3, await check.NepRecords.CountAsync());
    }

    private sealed class BridgeApi : ISyncApiClient
    {
        private readonly SyncAppService _sync;
        private readonly Func<RecordActor> _actor;
        public bool ForceConflictOnce { get; set; }
        public Guid ConflictEntityId { get; set; }
        public string? ConflictStamp { get; set; }

        public BridgeApi(SyncAppService sync, Func<RecordActor> actor)
        {
            _sync = sync;
            _actor = actor;
        }

        public async Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPushRequest request, CancellationToken ct = default)
        {
            if (ForceConflictOnce
                && request.Operations.Count > 0
                && string.Equals(request.Operations[0].OperationType, SyncConstants.OperationUpdateRecord,
                    StringComparison.OrdinalIgnoreCase))
            {
                ForceConflictOnce = false;
                var snap = new ClientNepRecordSnapshot
                {
                    Id = ConflictEntityId,
                    Telar = "SERVER",
                    Neps = 12,
                    ConcurrencyStamp = ConflictStamp ?? "Y"
                };
                return new ClientSyncPushResponse
                {
                    Results =
                    [
                        new ClientSyncOperationResultDto
                        {
                            ClientOperationId = request.Operations[0].ClientOperationId,
                            Result = ClientSyncResultNames.Conflict,
                            ErrorCode = "CONFLICT",
                            EntityId = ConflictEntityId,
                            ServerConcurrencyStamp = ConflictStamp,
                            ServerSnapshot = StableJson(snap)
                        }
                    ]
                };
            }

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
            }, _actor(), "2d12", ct);

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
            }, _actor(), "2d12-p", ct);

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

        private static JsonElement StableJson(object value)
        {
            var json = JsonSerializer.Serialize(value, ClientSyncJson.Options);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }

    private sealed class StaticCookie : ISyncAuthCookieProvider
    {
        private readonly string? _c;
        public StaticCookie(string? c) => _c = c;
        public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default) =>
            Task.FromResult(_c);
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
