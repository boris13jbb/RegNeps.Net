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

namespace RegNeps.Tests;

/// <summary>FASE 2D.12 — ClearAll sync-safe (N tombstones online/admin).</summary>
public sealed class SyncClearAllTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;
    private Guid _adminId;
    private Guid _superId;
    private Guid _operarioId;
    private Guid _supervisorId;

    public SyncClearAllTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbFactory(_options);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitializer.ApplySchemaPatchesAsync(db);
        await DbSeeder.SeedAsync(db);

        var hash = BCrypt.Net.BCrypt.HashPassword("Clr2D12!");
        var admin = new AppUser
        {
            Username = "clr_admin",
            DisplayName = "Admin Clear",
            PasswordHash = hash,
            Role = AppUserRole.Admin,
            RoleCode = "Admin",
            IsActive = true
        };
        var super = new AppUser
        {
            Username = "clr_super",
            DisplayName = "Super Clear",
            PasswordHash = hash,
            Role = AppUserRole.SuperAdmin,
            RoleCode = "SuperAdmin",
            IsActive = true,
            IsSuperAdmin = true
        };
        var op = new AppUser
        {
            Username = "clr_op",
            DisplayName = "Op Clear",
            PasswordHash = hash,
            Role = AppUserRole.Operario,
            RoleCode = "Operario",
            IsActive = true
        };
        var sup = new AppUser
        {
            Username = "clr_sup",
            DisplayName = "Sup Clear",
            PasswordHash = hash,
            Role = AppUserRole.Supervisor,
            RoleCode = "Supervisor",
            IsActive = true
        };
        db.Users.AddRange(admin, super, op, sup);
        await db.SaveChangesAsync();
        _adminId = admin.Id;
        _superId = super.Id;
        _operarioId = op.Id;
        _supervisorId = sup.Id;

        await new SyncPersistence(_factory, new AtomicNepRecordCreateStore(_factory))
            .EnsureCatalogBaselineAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private NepRecordService CreateService()
    {
        var db = _factory.CreateDbContext();
        var atomic = new AtomicNepRecordCreateStore(_factory);
        return new NepRecordService(
            new NepRecordRepository(_factory),
            new AlertConfigRepository(db),
            permissions: new PermissionService(
                new RolePermissionRepository(db),
                new RoleRepository(db),
                new PermissionMatrix()),
            atomicCreate: atomic,
            syncPersistence: new SyncPersistence(_factory, atomic));
    }

    private SyncAppService CreateSync()
    {
        var db = _factory.CreateDbContext();
        var atomic = new AtomicNepRecordCreateStore(_factory);
        return new SyncAppService(
            new SyncPersistence(_factory, atomic),
            new PermissionService(
                new RolePermissionRepository(db),
                new RoleRepository(db),
                new PermissionMatrix()),
            NullLogger<SyncAppService>.Instance);
    }

    private RecordActor ActorAdmin() =>
        RecordActor.Create(_adminId.ToString(), "clr_admin", "Admin Clear", AppUserRole.Admin, false, null, "Admin", true);

    private RecordActor ActorSuper() =>
        RecordActor.Create(_superId.ToString(), "clr_super", "Super Clear", AppUserRole.SuperAdmin, true, null, "SuperAdmin", true);

    private RecordActor ActorOperario() =>
        RecordActor.Create(_operarioId.ToString(), "clr_op", "Op Clear", AppUserRole.Operario, false, null, "Operario", false);

    private RecordActor ActorSupervisor() =>
        RecordActor.Create(_supervisorId.ToString(), "clr_sup", "Sup Clear", AppUserRole.Supervisor, false, null, "Supervisor", true);

    private static SyncPushRequest PushCreate(string deviceId, string clientOpId, double neps = 12) =>
        new()
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = deviceId,
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = clientOpId,
                    OperationType = SyncConstants.OperationCreateRecord,
                    CaptureSessionId = "sess-clr",
                    Payload = JsonSerializer.SerializeToElement(new SyncCreateRecordPayload
                    {
                        Telar = "T-CLR",
                        Neps = neps,
                        Tela = "Denim",
                        LoteTrama = "L1",
                        Turno = "A"
                    })
                }
            ]
        };

    private async Task ClearNepRecordsViaServiceAsync()
    {
        // Limpia NepRecords entre tests sin tocar catálogos (baseline).
        var service = CreateService();
        await service.ClearAllAsync(ActorAdmin());
    }

    [Fact]
    public async Task ClearAll_Empty_Table_Is_NoOp()
    {
        await ClearNepRecordsViaServiceAsync();
        var service = CreateService();
        await service.ClearAllAsync(ActorAdmin());

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.NepRecords.CountAsync());
        Assert.Equal(0, await db.SyncChangeLogs.CountAsync(c =>
            c.ChangeType == SyncConstants.ChangeRecordDeleted
            && c.ActorUserId == _adminId.ToString()));
    }

    [Fact]
    public async Task ClearAll_N_Records_Produces_N_Tombstones_And_Clears_Correctives()
    {
        var service = CreateService();
        var actor = ActorAdmin();
        await ClearNepRecordsViaServiceAsync();

        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var r = await service.CreateAsync(new CreateNepRecordRequest
            {
                Telar = $"T{i}",
                Neps = 10 + i,
                ClientOperationId = Guid.NewGuid().ToString("N")
            }, actor);
            ids.Add(r.Id);
            await service.ApplyCorrectiveAsync(new CorrectiveActionRequest
            {
                RecordId = r.Id,
                Accion = $"A{i}",
                Responsable = "R",
                MarcarRevisado = true
            }, actor);
        }

        await using (var before = _factory.CreateDbContext())
        {
            Assert.Equal(5, await before.NepRecords.CountAsync());
            Assert.Equal(5, await before.CorrectiveActions.CountAsync());
        }

        var deletedBefore = await CountTombstonesAsync();
        await service.ClearAllAsync(actor);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.NepRecords.CountAsync());
        Assert.Equal(0, await db.CorrectiveActions.CountAsync());
        Assert.Equal(deletedBefore + 5, await db.SyncChangeLogs.CountAsync(c =>
            c.ChangeType == SyncConstants.ChangeRecordDeleted));

        foreach (var id in ids)
        {
            var tomb = await db.SyncChangeLogs
                .Where(c => c.EntityId == id && c.ChangeType == SyncConstants.ChangeRecordDeleted)
                .OrderByDescending(c => c.Sequence)
                .FirstAsync();
            Assert.Equal(SyncConstants.EntityNepRecord, tomb.EntityType);
            Assert.Equal(_adminId.ToString(), tomb.ActorUserId);
            Assert.Null(tomb.ClientOperationId);
            using var doc = JsonDocument.Parse(tomb.PayloadJson);
            Assert.Equal(id, doc.RootElement.GetProperty("id").GetGuid());
            Assert.True(doc.RootElement.TryGetProperty("lastConcurrencyStamp", out _)
                        || doc.RootElement.TryGetProperty("LastConcurrencyStamp", out _));
        }
    }

    [Fact]
    public async Task ClearAll_Atomic_Rollback_Leaves_Records_And_No_Partial_Tombstones()
    {
        var service = CreateService();
        var actor = ActorAdmin();
        await ClearNepRecordsViaServiceAsync();
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T-RB",
            Neps = 11,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);

        var tombsBefore = await CountTombstonesAsync();
        await using (var setup = _factory.CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER IF NOT EXISTS trg_reject_clearall_tombstone
                BEFORE INSERT ON SyncChangeLogs
                WHEN NEW.ChangeType = 'RecordDeleted'
                BEGIN
                    SELECT RAISE(ABORT, 'forced clearall reject');
                END;
                """);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => service.ClearAllAsync(actor));

        await using (var cleanup = _factory.CreateDbContext())
        {
            await cleanup.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS trg_reject_clearall_tombstone");
        }

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync());
        Assert.Equal(tombsBefore, await CountTombstonesAsync());
    }

    [Fact]
    public async Task ClearAll_Pull_From_Prior_Cursor_Paginates_All_Tombstones()
    {
        var service = CreateService();
        var sync = CreateSync();
        var actor = ActorAdmin();
        await ClearNepRecordsViaServiceAsync();

        for (var i = 0; i < 3; i++)
        {
            await service.CreateAsync(new CreateNepRecordRequest
            {
                Telar = $"P{i}",
                Neps = 12,
                ClientOperationId = Guid.NewGuid().ToString("N")
            }, actor);
        }

        await using var peek = _factory.CreateDbContext();
        var cursorBefore = await peek.SyncChangeLogs.MaxAsync(c => c.Sequence);

        await service.ClearAllAsync(actor);

        var page1 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-clr",
            Cursor = cursorBefore,
            PageSize = 2
        }, actor, "p1");

        var deleted1 = page1.Changes.Count(c => c.ChangeType == SyncConstants.ChangeRecordDeleted);
        Assert.Equal(2, deleted1);
        Assert.True(page1.HasMore);

        var page2 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-clr",
            Cursor = page1.NextCursor,
            PageSize = 2
        }, actor, "p2");

        var deleted2 = page2.Changes.Count(c => c.ChangeType == SyncConstants.ChangeRecordDeleted);
        Assert.Equal(1, deleted2);
        Assert.False(page2.HasMore || page2.Changes.Any(c =>
            c.ChangeType == SyncConstants.ChangeRecordDeleted
            && c.Sequence > page2.NextCursor));

        Assert.Equal(3, deleted1 + deleted2);
        Assert.True(page1.NextCursor < page2.NextCursor || deleted2 == 0);
    }

    [Fact]
    public async Task ClearAll_Then_Create_Pull_Keeps_New_Record()
    {
        var service = CreateService();
        var sync = CreateSync();
        var actor = ActorAdmin();
        await ClearNepRecordsViaServiceAsync();

        var old = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "OLD",
            Neps = 10,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);

        await using var peek = _factory.CreateDbContext();
        var cursorX = await peek.SyncChangeLogs.AsNoTracking()
            .Where(c => c.EntityId == old.Id && c.ChangeType == SyncConstants.ChangeRecordUpserted)
            .Select(c => c.Sequence)
            .FirstAsync() - 1; // antes del create, para forzar drenar todo; usamos cursor justo antes del clear

        var maxBeforeClear = await peek.SyncChangeLogs.MaxAsync(c => c.Sequence);
        await service.ClearAllAsync(actor);

        var created = await sync.PushAsync(
            PushCreate("dev-clr", Guid.NewGuid().ToString("N"), 40),
            actor, "new");
        Assert.Equal(nameof(SyncOperationResult.Accepted), created.Results[0].Result);
        var newId = created.Results[0].EntityId!.Value;

        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-b",
            Cursor = maxBeforeClear,
            PageSize = 500
        }, actor, "pull");

        Assert.Contains(pull.Changes, c =>
            c.ChangeType == SyncConstants.ChangeRecordDeleted && c.EntityId == old.Id);
        Assert.Contains(pull.Changes, c =>
            c.ChangeType == SyncConstants.ChangeRecordUpserted && c.EntityId == newId);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync());
        Assert.Equal(newId, (await db.NepRecords.SingleAsync()).Id);
        _ = cursorX;
    }

    [Fact]
    public async Task ClearAll_Twice_Second_Is_NoOp_No_Duplicate_Tombstones_For_Missing()
    {
        var service = CreateService();
        var actor = ActorAdmin();
        await ClearNepRecordsViaServiceAsync();
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "ONCE",
            Neps = 9,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);

        await service.ClearAllAsync(actor);
        var tombs = await CountTombstonesAsync();
        await service.ClearAllAsync(actor);
        Assert.Equal(tombs, await CountTombstonesAsync());
        Assert.Equal(0, await CountRecordsAsync());
    }

    [Fact]
    public async Task ClearAll_Forbidden_Without_Permission()
    {
        var service = CreateService();
        await ClearNepRecordsViaServiceAsync();
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "X",
            Neps = 8,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, ActorAdmin());

        await Assert.ThrowsAsync<UnauthorizedRecordAccessException>(() =>
            service.ClearAllAsync(ActorOperario()));

        // Supervisor: SeesAll típico pero sin ClearAllRecords en matriz sembrada.
        await Assert.ThrowsAsync<UnauthorizedRecordAccessException>(() =>
            service.ClearAllAsync(ActorSupervisor()));

        Assert.Equal(1, await CountRecordsAsync());
    }

    [Fact]
    public async Task ClearAll_Allowed_For_Admin_And_SuperAdmin()
    {
        var service = CreateService();
        await ClearNepRecordsViaServiceAsync();
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "A1",
            Neps = 8,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, ActorAdmin());
        await service.ClearAllAsync(ActorAdmin());
        Assert.Equal(0, await CountRecordsAsync());

        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "S1",
            Neps = 8,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, ActorSuper());
        await service.ClearAllAsync(ActorSuper());
        Assert.Equal(0, await CountRecordsAsync());
    }

    [Fact]
    public async Task ClearAll_Then_Create_Then_ClearAll_Again()
    {
        var service = CreateService();
        var actor = ActorAdmin();
        await ClearNepRecordsViaServiceAsync();
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "C1",
            Neps = 10,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);
        await service.ClearAllAsync(actor);
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "C2",
            Neps = 20,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);
        Assert.Equal(1, await CountRecordsAsync());
        await service.ClearAllAsync(actor);
        Assert.Equal(0, await CountRecordsAsync());
        Assert.True(await CountTombstonesAsync() >= 2);
    }

    [Fact]
    public async Task ClearAll_Does_Not_Emit_RecordsCleared_ChangeType()
    {
        var service = CreateService();
        await ClearNepRecordsViaServiceAsync();
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "Z",
            Neps = 7,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, ActorAdmin());
        await service.ClearAllAsync(ActorAdmin());

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.SyncChangeLogs.CountAsync(c =>
            c.ChangeType == "RecordsCleared"));
    }

    private async Task<int> CountRecordsAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.NepRecords.CountAsync();
    }

    private async Task<int> CountTombstonesAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.SyncChangeLogs.CountAsync(c =>
            c.ChangeType == SyncConstants.ChangeRecordDeleted);
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
