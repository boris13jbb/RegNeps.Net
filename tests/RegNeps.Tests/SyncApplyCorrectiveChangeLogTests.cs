using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RegNeps.Application.Permissions;
using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Sync;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;
using RegNeps.Infrastructure.Sync;

namespace RegNeps.Tests;

/// <summary>FASE 2C.1: ApplyCorrective atómico + contrato ClearAll fuera de sync.</summary>
public sealed class SyncApplyCorrectiveChangeLogTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;
    private Guid _adminId;

    public SyncApplyCorrectiveChangeLogTests()
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

        var admin = new AppUser
        {
            Username = "corr_admin",
            DisplayName = "Corr Admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Corr2C1!"),
            Role = AppUserRole.Admin,
            RoleCode = "Admin",
            IsActive = true
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        _adminId = admin.Id;
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private NepRecordService CreateOnlineService()
    {
        var atomic = new AtomicNepRecordCreateStore(_factory);
        var syncPersistence = new SyncPersistence(_factory, atomic);
        return new NepRecordService(
            new NepRecordRepository(_factory),
            new AlertConfigRepository(_factory.CreateDbContext()),
            atomicCreate: atomic,
            syncPersistence: syncPersistence);
    }

    private SyncAppService CreateSync()
    {
        var db = _factory.CreateDbContext();
        var permissions = new PermissionService(
            new RolePermissionRepository(db),
            new RoleRepository(db),
            new PermissionMatrix());
        var atomic = new AtomicNepRecordCreateStore(_factory);
        return new SyncAppService(
            new SyncPersistence(_factory, atomic),
            permissions,
            NullLogger<SyncAppService>.Instance);
    }

    private RecordActor ActorAdmin() =>
        RecordActor.Create(_adminId.ToString(), "corr_admin", "Corr Admin", AppUserRole.Admin, false, null, "Admin", true);

    [Fact]
    public async Task ApplyCorrective_Writes_Stamp_UpdatedAt_And_Exactly_One_ChangeLog()
    {
        var service = CreateOnlineService();
        var actor = ActorAdmin();
        var created = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T-COR",
            Neps = 50,
            Tela = "X",
            LoteTrama = "L",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);

        var stampBefore = created.ConcurrencyStamp;
        Assert.Null(created.UpdatedAt);

        await service.ApplyCorrectiveAsync(new CorrectiveActionRequest
        {
            RecordId = created.Id,
            Accion = "Ajuste tensión",
            Responsable = "Sup A",
            MarcarRevisado = true
        }, actor);

        await using var db = _factory.CreateDbContext();
        var record = await db.NepRecords
            .Include(r => r.HistorialAcciones)
            .SingleAsync(r => r.Id == created.Id);

        Assert.Equal("Ajuste tensión", record.AccionCorrectiva);
        Assert.Equal("Sup A", record.ResponsableRevision);
        Assert.True(record.RevisadoPorSupervisor);
        Assert.NotNull(record.FechaRevision);
        Assert.NotNull(record.UpdatedAt);
        Assert.NotEqual(stampBefore, record.ConcurrencyStamp);
        Assert.Single(record.HistorialAcciones);

        Assert.Equal(2, await db.SyncChangeLogs.CountAsync()); // Create + Corrective
        var correctiveLog = await db.SyncChangeLogs
            .OrderByDescending(c => c.Sequence)
            .FirstAsync();
        Assert.Equal(SyncConstants.ChangeRecordUpserted, correctiveLog.ChangeType);
        Assert.Equal(created.Id, correctiveLog.EntityId);
        Assert.Null(correctiveLog.ClientOperationId);

        var payload = JsonSerializer.Deserialize<SyncNepRecordPayload>(
            correctiveLog.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(payload);
        Assert.Equal("Ajuste tensión", payload!.AccionCorrectiva);
        Assert.Equal("Sup A", payload.ResponsableRevision);
        Assert.True(payload.RevisadoPorSupervisor);
        Assert.Equal(record.ConcurrencyStamp, payload.ConcurrencyStamp);
        Assert.Equal(record.UpdatedAt, payload.UpdatedAtUtc);
    }

    [Fact]
    public async Task ApplyCorrective_ChangeLog_Failure_Rolls_Back_Mutation()
    {
        var service = CreateOnlineService();
        var actor = ActorAdmin();
        var created = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T-RB",
            Neps = 40,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);
        var stampBefore = created.ConcurrencyStamp;

        try
        {
            await using (var setup = _factory.CreateDbContext())
            {
                await setup.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TRIGGER IF NOT EXISTS trg_reject_corrective_changelog
                    BEFORE INSERT ON SyncChangeLogs
                    BEGIN
                        SELECT RAISE(ABORT, 'forced corrective changelog reject');
                    END;
                    """);
            }

            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.ApplyCorrectiveAsync(new CorrectiveActionRequest
                {
                    RecordId = created.Id,
                    Accion = "No debe persistir",
                    Responsable = "X",
                    MarcarRevisado = true
                }, actor));

            await using var verify = _factory.CreateDbContext();
            var record = await verify.NepRecords
                .Include(r => r.HistorialAcciones)
                .SingleAsync(r => r.Id == created.Id);
            Assert.Equal(stampBefore, record.ConcurrencyStamp);
            Assert.True(string.IsNullOrEmpty(record.AccionCorrectiva));
            Assert.False(record.RevisadoPorSupervisor);
            Assert.Empty(record.HistorialAcciones);
            // Solo el ChangeLog del Create.
            Assert.Equal(1, await verify.SyncChangeLogs.CountAsync());
        }
        finally
        {
            await using var cleanup = _factory.CreateDbContext();
            await cleanup.Database.ExecuteSqlRawAsync(
                """DROP TRIGGER IF EXISTS trg_reject_corrective_changelog""");
        }
    }

    [Fact]
    public async Task ApplyCorrective_Then_Pull_Returns_Corrective_Payload()
    {
        var service = CreateOnlineService();
        var sync = CreateSync();
        var actor = ActorAdmin();

        var created = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T-PULL",
            Neps = 35,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);

        await service.ApplyCorrectiveAsync(new CorrectiveActionRequest
        {
            RecordId = created.Id,
            Accion = "Limpieza peines",
            Responsable = "Sup B",
            MarcarRevisado = true
        }, actor);

        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-corr",
            Cursor = 0
        }, actor, "corr-pull");

        var last = pull.Changes
            .Where(c => c.EntityId == created.Id && c.ChangeType == SyncConstants.ChangeRecordUpserted)
            .OrderByDescending(c => c.Sequence)
            .First();
        Assert.Equal("Limpieza peines", last.Payload.GetProperty("accionCorrectiva").GetString());
        Assert.True(last.Payload.GetProperty("revisadoPorSupervisor").GetBoolean());
        Assert.False(last.Payload.TryGetProperty("historialAcciones", out _));
    }

    [Fact]
    public async Task Second_ApplyCorrective_Creates_New_Sequence()
    {
        var service = CreateOnlineService();
        var actor = ActorAdmin();
        var created = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T-2",
            Neps = 33,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);

        await service.ApplyCorrectiveAsync(new CorrectiveActionRequest
        {
            RecordId = created.Id,
            Accion = "Primera",
            Responsable = "A",
            MarcarRevisado = true
        }, actor);

        await using var mid = _factory.CreateDbContext();
        var seq1 = await mid.SyncChangeLogs.MaxAsync(c => c.Sequence);
        var stamp1 = (await mid.NepRecords.SingleAsync(r => r.Id == created.Id)).ConcurrencyStamp;

        await service.ApplyCorrectiveAsync(new CorrectiveActionRequest
        {
            RecordId = created.Id,
            Accion = "Segunda",
            Responsable = "B",
            MarcarRevisado = true
        }, actor);

        await using var db = _factory.CreateDbContext();
        var seq2 = await db.SyncChangeLogs.MaxAsync(c => c.Sequence);
        Assert.True(seq2 > seq1);
        Assert.Equal(3, await db.SyncChangeLogs.CountAsync()); // create + 2 correctives
        var record = await db.NepRecords.SingleAsync(r => r.Id == created.Id);
        Assert.Equal("Segunda", record.AccionCorrectiva);
        Assert.NotEqual(stamp1, record.ConcurrencyStamp);
    }

    [Fact]
    public async Task ClearAll_Blocked_When_SyncChangeLogs_Exist()
    {
        var service = CreateOnlineService();
        var actor = ActorAdmin();
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T-CLR",
            Neps = 10,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ClearAllAsync(actor));
        Assert.Contains("offline-first", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync());
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync());
    }

    [Fact]
    public async Task ClearAll_Allowed_Without_SyncChangeLogs_And_Produces_No_Sync_Rows()
    {
        // Sin store atómico: Create no escribe ChangeLog → ClearAll permitido.
        var service = new NepRecordService(
            new NepRecordRepository(_factory),
            new AlertConfigRepository(_factory.CreateDbContext()),
            syncPersistence: new SyncPersistence(_factory, new AtomicNepRecordCreateStore(_factory)));

        var actor = ActorAdmin();
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T-EMPTY",
            Neps = 8
        }, actor);

        await using (var peek = _factory.CreateDbContext())
        {
            Assert.Equal(0, await peek.SyncChangeLogs.CountAsync());
            Assert.Equal(1, await peek.NepRecords.CountAsync());
        }

        await service.ClearAllAsync(actor);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.NepRecords.CountAsync());
        Assert.Equal(0, await db.SyncChangeLogs.CountAsync());
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
