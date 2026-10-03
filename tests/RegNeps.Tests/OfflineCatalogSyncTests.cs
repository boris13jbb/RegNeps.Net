using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RegNeps.Application.Permissions;
using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
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

/// <summary>FASE 2D.9 — Catálogos mínimos offline (Tela/Lote) server→Pull→SQLite.</summary>
[Collection("OfflineCatalogSync")]
public sealed class OfflineCatalogSyncTests : IAsyncLifetime
{
    private readonly SqliteConnection _serverConn;
    private readonly DbContextOptions<RegNepsDbContext> _serverOptions;
    private TestDbFactory _serverFactory = null!;
    private Guid _userId;
    private string _dir = null!;

    public OfflineCatalogSyncTests()
    {
        _serverConn = new SqliteConnection("Data Source=:memory:");
        _serverConn.Open();
        _serverOptions = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(_serverConn)
            .Options;
    }

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-cat-2d9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _serverFactory = new TestDbFactory(_serverOptions);
        await using var db = _serverFactory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitializer.ApplySchemaPatchesAsync(db);
        await DbSeeder.SeedAsync(db);

        var hash = BCrypt.Net.BCrypt.HashPassword("Cat2D9!");
        var user = new AppUser
        {
            Username = "cat_user",
            DisplayName = "Cat User",
            PasswordHash = hash,
            Role = AppUserRole.Operario,
            RoleCode = "Operario",
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _userId = user.Id;

        var persistence = new SyncPersistence(
            _serverFactory, new AtomicNepRecordCreateStore(_serverFactory));
        await persistence.EnsureCatalogBaselineAsync();
    }

    public Task DisposeAsync()
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

        return Task.CompletedTask;
    }

    private LocalSyncDbContext CreateOfflineDb(string? dbPath = null)
    {
        var path = dbPath ?? Path.Combine(_dir, "offline-" + Guid.NewGuid().ToString("N") + ".db");
        var opts = new DbContextOptionsBuilder<LocalSyncDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var db = new LocalSyncDbContext(opts);
        db.Database.Migrate();
        return db;
    }

    private SyncAppService CreateServerSync()
    {
        var db = _serverFactory.CreateDbContext();
        var permissions = new PermissionService(
            new RolePermissionRepository(db),
            new RoleRepository(db),
            new PermissionMatrix());
        return new SyncAppService(
            new SyncPersistence(_serverFactory, new AtomicNepRecordCreateStore(_serverFactory)),
            permissions,
            NullLogger<SyncAppService>.Instance);
    }

    private RecordActor Actor() =>
        RecordActor.Create(
            _userId.ToString(),
            "cat_user",
            "Cat User",
            AppUserRole.Operario,
            isSuperAdmin: false,
            roleCode: "Operario",
            seesAllRecords: false);

    private async Task<(SyncEngine Engine, OfflineCatalogService Catalogs, OfflineCaptureService Capture)>
        BootOfflineAsync(LocalSyncDbContext offline)
    {
        var sessions = new OfflineSessionService(offline, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-" + Guid.NewGuid().ToString("N")[..8]));
        var catalogs = new OfflineCatalogService(offline);
        var capture = new OfflineCaptureService(offline, sessions, devices);
        var server = CreateServerSync();
        var api = new BridgeApi(server, Actor);
        var engine = new SyncEngine(offline, sessions, devices, api, new StaticCookie("RegNeps.Auth=x"));

        await sessions.UpsertUxSnapshotAsync(
            _userId.ToString(),
            "cat_user",
            "Operario",
            [
                OfflineStoreConstants.CaptureRecordsPermission,
                OfflineStoreConstants.EditRecordsPermission,
                "ViewRecords"
            ],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        if (!await offline.SyncStates.AnyAsync())
        {
            offline.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = 0 });
            await offline.SaveChangesAsync();
        }

        return (engine, catalogs, capture);
    }

    [Fact]
    public async Task Pull_Initial_Loads_Fabric_And_Lote_With_Stable_Ids()
    {
        await using var offline = CreateOfflineDb();
        var (engine, catalogs, _) = await BootOfflineAsync(offline);

        Assert.Equal(0, await catalogs.CountAsync());
        await engine.SyncAsync();

        var avail = await catalogs.GetAvailabilityAsync();
        Assert.True(avail.HasAnyCatalog);
        Assert.True(avail.ActiveFabrics > 0);
        Assert.True(avail.ActiveLotes > 0);

        await using var server = _serverFactory.CreateDbContext();
        var serverFabric = await server.Fabrics.AsNoTracking().FirstAsync(f => f.IsActive);
        var local = await offline.LocalCatalogItems.SingleAsync(c => c.Id == serverFabric.Id);
        Assert.Equal(LocalCatalogKind.Fabric, local.Kind);
        Assert.True(local.IsActive);
        Assert.Equal(serverFabric.Id, local.Id);
    }

    [Fact]
    public async Task Pull_Incremental_Receives_New_And_Updated_Catalog()
    {
        await using var offline = CreateOfflineDb();
        var (engine, catalogs, _) = await BootOfflineAsync(offline);
        var first = await engine.SyncAsync();
        Assert.True(first.PullCompleted, first.Message);
        var before = await catalogs.CountActiveAsync(LocalCatalogKind.Fabric);

        var fabrics = new FabricRepository(_serverFactory);
        var created = await fabrics.EnsureActiveByNameAsync("Tela-2D9-Nueva");
        Assert.NotEqual(Guid.Empty, created.Id);
        await fabrics.UpdateAsync(new Fabric
        {
            Id = created.Id,
            Name = "Tela-2D9-Renombrada",
            Code = "T29",
            IsActive = true
        });

        var second = await engine.SyncAsync();
        Assert.True(second.PullCompleted, second.Message);
        Assert.True(await catalogs.CountActiveAsync(LocalCatalogKind.Fabric) >= before);
        var local = await offline.LocalCatalogItems.SingleAsync(c => c.Id == created.Id);
        Assert.Equal("Tela-2D9-Renombrada", local.Name);
        Assert.True(local.IsActive);
    }

    [Fact]
    public async Task Deactivate_And_Delete_Mark_Inactive_Locally_Not_In_Active_List()
    {
        await using var offline = CreateOfflineDb();
        var (engine, catalogs, _) = await BootOfflineAsync(offline);
        var fabrics = new FabricRepository(_serverFactory);
        var f = await fabrics.EnsureActiveByNameAsync("Tela-Deact");
        await engine.SyncAsync();
        Assert.Contains(await catalogs.ListActiveAsync(LocalCatalogKind.Fabric), x => x.Id == f.Id);

        await fabrics.UpdateAsync(new Fabric { Id = f.Id, Name = "Tela-Deact", IsActive = false });
        await engine.SyncAsync();
        Assert.DoesNotContain(await catalogs.ListActiveAsync(LocalCatalogKind.Fabric), x => x.Id == f.Id);
        Assert.False((await offline.LocalCatalogItems.SingleAsync(c => c.Id == f.Id)).IsActive);

        var f2 = await fabrics.EnsureActiveByNameAsync("Tela-Delete");
        await engine.SyncAsync();
        await fabrics.DeleteAsync(f2.Id);
        await engine.SyncAsync();
        var deletedLocal = await offline.LocalCatalogItems.SingleAsync(c => c.Id == f2.Id);
        Assert.False(deletedLocal.IsActive);
    }

    [Fact]
    public async Task Absent_Catalog_Allows_Free_Text_Capture_Atomic()
    {
        await using var offline = CreateOfflineDb();
        var (_, catalogs, capture) = await BootOfflineAsync(offline);
        Assert.False((await catalogs.GetAvailabilityAsync()).HasAnyCatalog);

        var result = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = "9",
            Neps = 18,
            Tela = "Libre",
            LoteTrama = "63E26401"
        });
        Assert.Equal(PendingOperationStatus.Pending, result.Operation.Status);
        Assert.Equal(1, await offline.PendingOperations.CountAsync());
        Assert.Equal(1, await offline.LocalNepRecords.CountAsync());
    }

    [Fact]
    public async Task Push_Does_Not_Accept_Catalog_Mutation_Operations()
    {
        var sync = CreateServerSync();
        var response = await sync.PushAsync(new SyncPushRequest
        {
            ProtocolVersion = SyncConstants.ProtocolVersion,
            DeviceId = "dev-cat",
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = Guid.NewGuid().ToString("N"),
                    OperationType = "UpsertCatalog",
                    Payload = JsonSerializer.SerializeToElement(new { kind = "Fabric", name = "X" })
                }
            ]
        }, Actor(), "corr-cat");

        Assert.Equal(nameof(SyncOperationResult.Invalid), response.Results[0].Result);
        Assert.Equal("OPERATION_TYPE_UNSUPPORTED", response.Results[0].ErrorCode);
    }

    [Fact]
    public async Task Catalog_Pull_Does_Not_Create_NepRecord_Conflict_Or_Full_Replica()
    {
        await using var offline = CreateOfflineDb();
        var (engine, catalogs, capture) = await BootOfflineAsync(offline);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 18 });
        var op = await offline.PendingOperations.SingleAsync();
        op.Status = PendingOperationStatus.Synced;
        var rec = await offline.LocalNepRecords.SingleAsync();
        rec.SyncStatus = LocalSyncStatus.Synced;
        rec.ServerRecordId = Guid.NewGuid();
        rec.ConcurrencyStamp = "s1";
        await offline.SaveChangesAsync();

        var cursorBefore = (await offline.SyncStates.SingleAsync()).LastPulledSequence;
        await engine.SyncAsync();
        var cursorAfter = (await offline.SyncStates.SingleAsync()).LastPulledSequence;
        Assert.True(cursorAfter >= cursorBefore);
        Assert.Equal(1, await offline.LocalNepRecords.CountAsync());
        Assert.Equal(0, await offline.PendingOperations.CountAsync(o => o.Status == PendingOperationStatus.Conflict));
        Assert.True(await catalogs.CountAsync() > 0);
        Assert.All(await offline.LocalCatalogItems.ToListAsync(),
            c => Assert.True(c.Kind is LocalCatalogKind.Fabric or LocalCatalogKind.Lote));
    }

    [Fact]
    public async Task Persistence_Across_Restart_And_Baseline_Idempotent()
    {
        var path = Path.Combine(_dir, "persist-" + Guid.NewGuid().ToString("N") + ".db");
        Guid fabricId;
        await using (var offline = CreateOfflineDb(path))
        {
            var (engine, _, _) = await BootOfflineAsync(offline);
            await engine.SyncAsync();
            fabricId = (await offline.LocalCatalogItems.FirstAsync(c => c.Kind == LocalCatalogKind.Fabric)).Id;
        }

        await using var offline2 = CreateOfflineDb(path);
        Assert.Equal(fabricId, (await offline2.LocalCatalogItems.SingleAsync(c => c.Id == fabricId)).Id);

        await using var server = _serverFactory.CreateDbContext();
        var before = await server.SyncChangeLogs.CountAsync(c =>
            c.EntityType == SyncConstants.EntityCatalogItem);
        var persistence = new SyncPersistence(
            _serverFactory, new AtomicNepRecordCreateStore(_serverFactory));
        await persistence.EnsureCatalogBaselineAsync();
        var after = await server.SyncChangeLogs.CountAsync(c =>
            c.EntityType == SyncConstants.EntityCatalogItem);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task NepRecord_Create_Push_Still_Idempotent_With_Catalog_Logs()
    {
        await using var offline = CreateOfflineDb();
        var (engine, _, capture) = await BootOfflineAsync(offline);
        await engine.SyncAsync();
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "77", Neps = 18 });
        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.Synced, (await offline.PendingOperations
            .SingleAsync(o => o.OperationType == OfflineOperationType.CreateRecord)).Status);

        var createOp = await offline.PendingOperations.SingleAsync(o =>
            o.OperationType == OfflineOperationType.CreateRecord);
        createOp.Status = PendingOperationStatus.Pending;
        (await offline.LocalNepRecords.SingleAsync()).SyncStatus = LocalSyncStatus.PendingSync;
        await offline.SaveChangesAsync();
        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.Synced, (await offline.PendingOperations
            .SingleAsync(o => o.Id == createOp.Id)).Status);
        await using var server = _serverFactory.CreateDbContext();
        Assert.Equal(1, await server.NepRecords.CountAsync(r => r.Telar == "77"));
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }

    private sealed class StaticCookie : ISyncAuthCookieProvider
    {
        private readonly string? _cookie;
        public StaticCookie(string? cookie) => _cookie = cookie;
        public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default) =>
            Task.FromResult(_cookie);
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
            string serverBaseUrl,
            string? cookieHeader,
            ClientSyncPushRequest request,
            CancellationToken ct = default)
        {
            var mapped = new SyncPushRequest
            {
                ProtocolVersion = request.ProtocolVersion,
                DeviceId = request.DeviceId,
                Operations = request.Operations.Select(o => new SyncOperationDto
                {
                    ClientOperationId = o.ClientOperationId,
                    OperationType = o.OperationType,
                    CaptureSessionId = o.CaptureSessionId,
                    ExpectedConcurrencyStamp = o.ExpectedConcurrencyStamp,
                    ClientCreatedAtUtc = o.ClientCreatedAtUtc,
                    Payload = o.Payload ?? default
                }).ToList()
            };
            var response = await _sync.PushAsync(mapped, _actor(), "gate-2d9", ct);
            return new ClientSyncPushResponse
            {
                ProtocolVersion = response.ProtocolVersion,
                ServerTimeUtc = response.ServerTimeUtc,
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
            string serverBaseUrl,
            string? cookieHeader,
            ClientSyncPullRequest request,
            CancellationToken ct = default)
        {
            var response = await _sync.PullAsync(new SyncPullRequest
            {
                ProtocolVersion = request.ProtocolVersion,
                DeviceId = request.DeviceId,
                Cursor = request.Cursor,
                PageSize = request.PageSize
            }, _actor(), "gate-2d9-pull", ct);

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
}
