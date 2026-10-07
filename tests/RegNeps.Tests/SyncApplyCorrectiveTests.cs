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
using RegNeps.Domain.Services;
using RegNeps.Domain.Sync;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;
using RegNeps.Infrastructure.Sync;

namespace RegNeps.Tests;

/// <summary>FASE 2D.10 — ApplyCorrective Push/Pull (servidor): dominio, idempotencia, conflicto, seguridad.</summary>
public sealed class SyncApplyCorrectiveTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;
    private Guid _adminId;
    private Guid _operarioId;
    private Guid _supervisorId;

    public SyncApplyCorrectiveTests()
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

        var hash = BCrypt.Net.BCrypt.HashPassword("Sync2D10!");
        var admin = new AppUser
        {
            Username = "corr_admin",
            DisplayName = "Admin Corr",
            PasswordHash = hash,
            Role = AppUserRole.Admin,
            RoleCode = "Admin",
            IsActive = true
        };
        var op = new AppUser
        {
            Username = "corr_op",
            DisplayName = "Op Corr",
            PasswordHash = hash,
            Role = AppUserRole.Operario,
            RoleCode = "Operario",
            IsActive = true
        };
        var supervisor = new AppUser
        {
            Username = "corr_sup",
            DisplayName = "Sup Corr",
            PasswordHash = hash,
            Role = AppUserRole.Supervisor,
            RoleCode = "Supervisor",
            IsActive = true
        };
        db.Users.AddRange(admin, op, supervisor);
        await db.SaveChangesAsync();
        _adminId = admin.Id;
        _operarioId = op.Id;
        _supervisorId = supervisor.Id;

        await new SyncPersistence(_factory, new AtomicNepRecordCreateStore(_factory))
            .EnsureCatalogBaselineAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
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
        RecordActor.Create(_adminId.ToString(), "corr_admin", "Admin Corr", AppUserRole.Admin, false, null, "Admin", true);

    private RecordActor ActorOperario() =>
        RecordActor.Create(_operarioId.ToString(), "corr_op", "Op Corr", AppUserRole.Operario, false, null, "Operario", false);

    /// <summary>Supervisor con permiso ApplyCorrective pero sin SeesAll (para ownership).</summary>
    private RecordActor ActorSupervisorNoSeesAll() =>
        RecordActor.Create(_supervisorId.ToString(), "corr_sup", "Sup Corr", AppUserRole.Supervisor, false, null, "Supervisor", false);

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
                    CaptureSessionId = "sess-corr",
                    Payload = JsonSerializer.SerializeToElement(new SyncCreateRecordPayload
                    {
                        Telar = "T-CORR",
                        Neps = neps,
                        Tela = "Denim",
                        LoteTrama = "LCORR",
                        Turno = "A",
                        Operario = "op",
                        LineaProduccion = "L1"
                    })
                }
            ]
        };

    private static SyncPushRequest PushCorrective(
        string deviceId,
        string clientOpId,
        Guid entityId,
        string stamp,
        string accion = "Ajuste trama",
        string responsable = "Sup A",
        bool marcarRevisado = true) =>
        new()
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = deviceId,
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = clientOpId,
                    OperationType = SyncConstants.OperationApplyCorrective,
                    ExpectedConcurrencyStamp = stamp,
                    CaptureSessionId = "sess-corr",
                    Payload = JsonSerializer.SerializeToElement(new SyncApplyCorrectivePayload
                    {
                        EntityId = entityId,
                        Accion = accion,
                        Responsable = responsable,
                        MarcarRevisado = marcarRevisado
                    })
                }
            ]
        };

    private static SyncPushRequest PushUpdate(
        string deviceId,
        string clientOpId,
        Guid entityId,
        string stamp,
        double neps) =>
        new()
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = deviceId,
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = clientOpId,
                    OperationType = SyncConstants.OperationUpdateRecord,
                    ExpectedConcurrencyStamp = stamp,
                    Payload = JsonSerializer.SerializeToElement(new SyncUpdateRecordPayload
                    {
                        EntityId = entityId,
                        Telar = "T-CORR",
                        Neps = neps,
                        Tela = "Denim",
                        LoteTrama = "LCORR",
                        Turno = "B",
                        Operario = "op2",
                        LineaProduccion = "L1"
                    })
                }
            ]
        };

    private static SyncPushRequest PushDelete(
        string deviceId, string clientOpId, Guid entityId, string stamp) =>
        new()
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = deviceId,
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = clientOpId,
                    OperationType = SyncConstants.OperationDeleteRecord,
                    ExpectedConcurrencyStamp = stamp,
                    Payload = JsonSerializer.SerializeToElement(new SyncDeleteRecordPayload { EntityId = entityId })
                }
            ]
        };

    private async Task<(Guid Id, string Stamp, double Neps)> CreateViaPushAsync(
        SyncAppService sync, RecordActor actor, double neps = 12)
    {
        var opId = Guid.NewGuid().ToString("N");
        var response = await sync.PushAsync(PushCreate("dev-corr", opId, neps), actor, "c");
        Assert.Equal(nameof(SyncOperationResult.Accepted), response.Results[0].Result);
        return (response.Results[0].EntityId!.Value, response.Results[0].ConcurrencyStamp!, neps);
    }

    [Theory]
    [InlineData(12, AlertLevel.Ok)]
    [InlineData(30, AlertLevel.Mention)]
    [InlineData(50, AlertLevel.CriticalAdjustment)]
    [InlineData(70, AlertLevel.SecondQuality)]
    public async Task ApplyCorrective_Accepted_Preserves_Neps_And_Quality(
        double neps, AlertLevel expected)
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp, _) = await CreateViaPushAsync(sync, actor, neps);

        var corr = await sync.PushAsync(
            PushCorrective("dev-corr", Guid.NewGuid().ToString("N"), id, stamp),
            actor, "corr");
        Assert.Equal(nameof(SyncOperationResult.Accepted), corr.Results[0].Result);
        Assert.Equal(expected.ToDisplayLabel(), corr.Results[0].QualityLabel);

        await using var db = _factory.CreateDbContext();
        var entity = await db.NepRecords.Include(r => r.HistorialAcciones).SingleAsync(r => r.Id == id);
        Assert.Equal(neps, entity.Neps);
        Assert.Equal("T-CORR", entity.Telar);
        Assert.Equal("Ajuste trama", entity.AccionCorrectiva);
        Assert.Equal("Sup A", entity.ResponsableRevision);
        Assert.True(entity.RevisadoPorSupervisor);
        Assert.NotNull(entity.FechaRevision);
        Assert.NotEqual(stamp, entity.ConcurrencyStamp);
        Assert.NotNull(entity.UpdatedAt);
        Assert.Single(entity.HistorialAcciones);
        Assert.Equal(expected, AlertEvaluator.GetLevel(entity.Neps));
    }

    [Fact]
    public async Task ApplyCorrective_Does_Not_Mutate_Capture_Fields()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp, _) = await CreateViaPushAsync(sync, actor, 18);

        await using (var db = _factory.CreateDbContext())
        {
            var e = await db.NepRecords.SingleAsync(r => r.Id == id);
            e.Observacion = "obs-original";
            e.Turno = "A";
            await db.SaveChangesAsync();
            stamp = e.ConcurrencyStamp;
        }

        var corr = await sync.PushAsync(
            PushCorrective("dev-corr", Guid.NewGuid().ToString("N"), id, stamp!, "Nueva acción", "R1"),
            actor, "corr2");
        Assert.Equal(nameof(SyncOperationResult.Accepted), corr.Results[0].Result);

        await using var read = _factory.CreateDbContext();
        var entity = await read.NepRecords.SingleAsync(r => r.Id == id);
        Assert.Equal(18, entity.Neps);
        Assert.Equal("T-CORR", entity.Telar);
        Assert.Equal("Denim", entity.Tela);
        Assert.Equal("LCORR", entity.LoteTrama);
        Assert.Equal("A", entity.Turno);
        Assert.Equal("obs-original", entity.Observacion);
        Assert.Equal("Nueva acción", entity.AccionCorrectiva);
    }

    [Fact]
    public async Task ApplyCorrective_Invalid_Empty_Accion()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp, _) = await CreateViaPushAsync(sync, actor);

        var corr = await sync.PushAsync(
            PushCorrective("dev-corr", Guid.NewGuid().ToString("N"), id, stamp, accion: "  "),
            actor, "inv");
        Assert.Equal(nameof(SyncOperationResult.Invalid), corr.Results[0].Result);
        Assert.Equal("VALIDATION", corr.Results[0].ErrorCode);
    }

    [Fact]
    public async Task ApplyCorrective_Invalid_Missing_Stamp()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, _, _) = await CreateViaPushAsync(sync, actor);

        var request = new SyncPushRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-corr",
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = Guid.NewGuid().ToString("N"),
                    OperationType = SyncConstants.OperationApplyCorrective,
                    Payload = JsonSerializer.SerializeToElement(new SyncApplyCorrectivePayload
                    {
                        EntityId = id,
                        Accion = "X",
                        Responsable = "Y"
                    })
                }
            ]
        };
        var corr = await sync.PushAsync(request, actor, "nostamp");
        Assert.Equal(nameof(SyncOperationResult.Invalid), corr.Results[0].Result);
        Assert.Equal("CONCURRENCY_STAMP", corr.Results[0].ErrorCode);
    }

    [Fact]
    public async Task ApplyCorrective_Conflict_Stale_Stamp_No_Mutation()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp, _) = await CreateViaPushAsync(sync, actor, 12);

        var upd = await sync.PushAsync(
            PushUpdate("dev-corr", Guid.NewGuid().ToString("N"), id, stamp, 30),
            actor, "u");
        Assert.Equal(nameof(SyncOperationResult.Accepted), upd.Results[0].Result);

        var corr = await sync.PushAsync(
            PushCorrective("dev-corr", Guid.NewGuid().ToString("N"), id, stamp, "Stale"),
            actor, "stale");
        Assert.Equal(nameof(SyncOperationResult.Conflict), corr.Results[0].Result);
        Assert.NotNull(corr.Results[0].ServerSnapshot);
        Assert.Equal(upd.Results[0].ConcurrencyStamp, corr.Results[0].ServerConcurrencyStamp);

        await using var db = _factory.CreateDbContext();
        var entity = await db.NepRecords.SingleAsync(r => r.Id == id);
        Assert.NotEqual("Stale", entity.AccionCorrectiva);
        Assert.Equal(30, entity.Neps);
    }

    [Fact]
    public async Task ApplyCorrective_Duplicate_Retry_Single_ChangeLog()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp, _) = await CreateViaPushAsync(sync, actor);
        var opId = Guid.NewGuid().ToString("N");

        var first = await sync.PushAsync(PushCorrective("dev-corr", opId, id, stamp), actor, "r1");
        Assert.Equal(nameof(SyncOperationResult.Accepted), first.Results[0].Result);

        var retry = await sync.PushAsync(PushCorrective("dev-corr", opId, id, stamp), actor, "r2");
        Assert.Equal(nameof(SyncOperationResult.Duplicate), retry.Results[0].Result);
        Assert.Equal(first.Results[0].EntityId, retry.Results[0].EntityId);
        Assert.Equal(first.Results[0].ConcurrencyStamp, retry.Results[0].ConcurrencyStamp);

        await using var db = _factory.CreateDbContext();
        var logs = await db.SyncChangeLogs.CountAsync(c =>
            c.EntityType == SyncConstants.EntityNepRecord
            && c.EntityId == id
            && c.ChangeType == SyncConstants.ChangeRecordUpserted
            && c.ClientOperationId == opId);
        Assert.Equal(1, logs);
        Assert.Equal(1, await db.CorrectiveActions.CountAsync(a => a.NepRecordId == id));
    }

    [Fact]
    public async Task ApplyCorrective_Deleted_Entity_Is_Invalid()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp, _) = await CreateViaPushAsync(sync, actor);
        var del = await sync.PushAsync(
            PushDelete("dev-corr", Guid.NewGuid().ToString("N"), id, stamp), actor, "d");
        Assert.Equal(nameof(SyncOperationResult.Accepted), del.Results[0].Result);

        var corr = await sync.PushAsync(
            PushCorrective("dev-corr", Guid.NewGuid().ToString("N"), id, stamp),
            actor, "del");
        Assert.Equal(nameof(SyncOperationResult.Invalid), corr.Results[0].Result);
        Assert.Equal("ENTITY_DELETED", corr.Results[0].ErrorCode);
    }

    [Fact]
    public async Task ApplyCorrective_Forbidden_No_Permission()
    {
        // Operario puede crear pero no tiene ApplyCorrectiveAction en matriz sembrada.
        var sync = CreateSync();
        var owner = ActorOperario();
        var (id, stamp, _) = await CreateViaPushAsync(sync, owner);

        var corr = await sync.PushAsync(
            PushCorrective("dev-corr", Guid.NewGuid().ToString("N"), id, stamp),
            owner, "noperm");
        Assert.Equal(nameof(SyncOperationResult.Forbidden), corr.Results[0].Result);
        Assert.Equal("CORRECTIVE_FORBIDDEN", corr.Results[0].ErrorCode);
    }

    [Fact]
    public async Task ApplyCorrective_Forbidden_Not_Owner_Without_SeesAll()
    {
        var sync = CreateSync();
        var owner = ActorOperario();
        var (id, stamp, _) = await CreateViaPushAsync(sync, owner);

        // Supervisor con permiso correctiva pero SeesAll=false y no propietario.
        var corr = await sync.PushAsync(
            PushCorrective("dev-corr", Guid.NewGuid().ToString("N"), id, stamp),
            ActorSupervisorNoSeesAll(), "own");
        Assert.Equal(nameof(SyncOperationResult.Forbidden), corr.Results[0].Result);
        Assert.Equal("OWNERSHIP", corr.Results[0].ErrorCode);
    }

    [Fact]
    public async Task ApplyCorrective_SeesAll_Admin_Can_Correct_Others()
    {
        var sync = CreateSync();
        var owner = ActorOperario();
        var admin = ActorAdmin();
        var (id, stamp, _) = await CreateViaPushAsync(sync, owner);

        var corr = await sync.PushAsync(
            PushCorrective("dev-corr", Guid.NewGuid().ToString("N"), id, stamp, "Admin fix"),
            admin, "sees");
        Assert.Equal(nameof(SyncOperationResult.Accepted), corr.Results[0].Result);

        await using var db = _factory.CreateDbContext();
        Assert.Equal("Admin fix", (await db.NepRecords.SingleAsync(r => r.Id == id)).AccionCorrectiva);
    }

    [Fact]
    public async Task ApplyCorrective_Pull_Propagates_RecordUpserted_Without_Loop()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp, _) = await CreateViaPushAsync(sync, actor);
        var corr = await sync.PushAsync(
            PushCorrective("dev-corr", Guid.NewGuid().ToString("N"), id, stamp, "Pull me"),
            actor, "p");
        Assert.Equal(nameof(SyncOperationResult.Accepted), corr.Results[0].Result);

        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-b",
            Cursor = 0,
            PageSize = 500
        }, ActorAdmin(), "pull");

        var upserts = pull.Changes
            .Where(c => c.EntityType == SyncConstants.EntityNepRecord
                        && c.EntityId == id
                        && c.ChangeType == SyncConstants.ChangeRecordUpserted)
            .ToList();
        Assert.True(upserts.Count >= 2); // create + corrective
        var last = upserts.OrderBy(c => c.Sequence).Last();
        using var doc = JsonDocument.Parse(last.Payload.GetRawText());
        Assert.Equal("Pull me", doc.RootElement.GetProperty("accionCorrectiva").GetString());
        Assert.Equal(12, doc.RootElement.GetProperty("neps").GetDouble());
    }

    [Fact]
    public async Task ApplyCorrective_Atomic_With_ChangeLog()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp, _) = await CreateViaPushAsync(sync, actor);

        await using (var setup = _factory.CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER IF NOT EXISTS trg_reject_corr_changelog
                BEFORE INSERT ON SyncChangeLogs
                BEGIN
                    SELECT RAISE(ABORT, 'forced changelog reject');
                END;
                """);
        }

        var before = await CountNepRecordsAsync();
        var corr = await sync.PushAsync(
            PushCorrective("dev-corr", Guid.NewGuid().ToString("N"), id, stamp),
            actor, "atom");
        Assert.NotEqual(nameof(SyncOperationResult.Accepted), corr.Results[0].Result);

        await using (var cleanup = _factory.CreateDbContext())
        {
            await cleanup.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS trg_reject_corr_changelog");
        }

        await using var db = _factory.CreateDbContext();
        var entity = await db.NepRecords.SingleAsync(r => r.Id == id);
        Assert.True(string.IsNullOrEmpty(entity.AccionCorrectiva));
        Assert.Equal(stamp, entity.ConcurrencyStamp);
        Assert.Equal(before, await CountNepRecordsAsync());
    }

    private async Task<int> CountNepRecordsAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.NepRecords.CountAsync();
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
