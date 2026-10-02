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

/// <summary>FASE 2C: Update/Conflict/Delete/tombstones/idempotencia/Pull.</summary>
public sealed class SyncUpdateDeleteTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;
    private Guid _adminId;
    private Guid _operarioId;
    private Guid _adminBId;

    public SyncUpdateDeleteTests()
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

        var hash = BCrypt.Net.BCrypt.HashPassword("Sync2C!");
        var admin = new AppUser
        {
            Username = "sync2c_admin",
            DisplayName = "Admin 2C",
            PasswordHash = hash,
            Role = AppUserRole.Admin,
            RoleCode = "Admin",
            IsActive = true
        };
        var adminB = new AppUser
        {
            Username = "sync2c_admin_b",
            DisplayName = "Admin B 2C",
            PasswordHash = hash,
            Role = AppUserRole.Admin,
            RoleCode = "Admin",
            IsActive = true
        };
        var operario = new AppUser
        {
            Username = "sync2c_op",
            DisplayName = "Op 2C",
            PasswordHash = hash,
            Role = AppUserRole.Operario,
            RoleCode = "Operario",
            IsActive = true
        };
        db.Users.AddRange(admin, adminB, operario);
        await db.SaveChangesAsync();
        _adminId = admin.Id;
        _adminBId = adminB.Id;
        _operarioId = operario.Id;
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
        RecordActor.Create(_adminId.ToString(), "sync2c_admin", "Admin 2C", AppUserRole.Admin, false, null, "Admin", true);

    private RecordActor ActorAdminB() =>
        RecordActor.Create(_adminBId.ToString(), "sync2c_admin_b", "Admin B 2C", AppUserRole.Admin, false, null, "Admin", true);

    private RecordActor ActorOperario() =>
        RecordActor.Create(_operarioId.ToString(), "sync2c_op", "Op 2C", AppUserRole.Operario, false, null, "Operario", false);

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
                    CaptureSessionId = "sess-2c",
                    Payload = JsonSerializer.SerializeToElement(new SyncCreateRecordPayload
                    {
                        Telar = "T-2C",
                        Neps = neps,
                        Tela = "Denim",
                        LoteTrama = "L2C",
                        Turno = "A",
                        Operario = "op",
                        LineaProduccion = "L1"
                    })
                }
            ]
        };

    private static SyncPushRequest PushUpdate(
        string deviceId,
        string clientOpId,
        Guid entityId,
        string stamp,
        double neps,
        string telar = "T-2C-U") =>
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
                        Telar = telar,
                        Neps = neps,
                        Tela = "Denim",
                        LoteTrama = "L2C",
                        Turno = "B",
                        Operario = "op2",
                        LineaProduccion = "L1",
                        Observacion = "upd"
                    })
                }
            ]
        };

    private static SyncPushRequest PushDelete(
        string deviceId,
        string clientOpId,
        Guid entityId,
        string stamp) =>
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
                    Payload = JsonSerializer.SerializeToElement(new SyncDeleteRecordPayload
                    {
                        EntityId = entityId
                    })
                }
            ]
        };

    private static SyncPullRequest Pull(string deviceId, long cursor, int? pageSize = null) =>
        new()
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = deviceId,
            Cursor = cursor,
            PageSize = pageSize
        };

    private async Task<(Guid Id, string Stamp)> CreateViaPushAsync(SyncAppService sync, RecordActor actor)
    {
        var opId = Guid.NewGuid().ToString("N");
        var response = await sync.PushAsync(PushCreate("dev-2c", opId), actor, "c");
        Assert.Equal(nameof(SyncOperationResult.Accepted), response.Results[0].Result);
        return (response.Results[0].EntityId!.Value, response.Results[0].ConcurrencyStamp!);
    }

    [Fact]
    public async Task Update_Valid_Updates_Stamp_UpdatedAt_And_One_ChangeLog()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp) = await CreateViaPushAsync(sync, actor);

        await using (var db = _factory.CreateDbContext())
        {
            var before = await db.NepRecords.SingleAsync(r => r.Id == id);
            Assert.Null(before.UpdatedAt);
        }

        var updOp = Guid.NewGuid().ToString("N");
        var upd = await sync.PushAsync(PushUpdate("dev-2c", updOp, id, stamp, 40), actor, "u1");

        Assert.Equal(nameof(SyncOperationResult.Accepted), upd.Results[0].Result);
        Assert.Equal(id, upd.Results[0].EntityId);
        Assert.NotEqual(stamp, upd.Results[0].ConcurrencyStamp);
        Assert.Equal("Mención", upd.Results[0].QualityLabel);
        Assert.True(upd.Results[0].ChangeSequence > 0);

        await using var verify = _factory.CreateDbContext();
        var record = await verify.NepRecords.SingleAsync(r => r.Id == id);
        Assert.Equal(40, record.Neps);
        Assert.Equal("T-2C-U", record.Telar);
        Assert.NotNull(record.UpdatedAt);
        Assert.Equal(upd.Results[0].ConcurrencyStamp, record.ConcurrencyStamp);
        Assert.Equal(2, await verify.SyncChangeLogs.CountAsync());
        Assert.Equal(1, await verify.SyncChangeLogs.CountAsync(c =>
            c.ChangeType == SyncConstants.ChangeRecordUpserted && c.ClientOperationId == updOp));
    }

    [Fact]
    public async Task Update_Unauthorized_Is_Forbidden()
    {
        var sync = CreateSync();
        var admin = ActorAdmin();
        var (id, stamp) = await CreateViaPushAsync(sync, admin);

        var response = await sync.PushAsync(
            PushUpdate("dev-op", Guid.NewGuid().ToString("N"), id, stamp, 20),
            ActorOperario(),
            "forbid-upd");

        Assert.Equal(nameof(SyncOperationResult.Forbidden), response.Results[0].Result);
        await using var db = _factory.CreateDbContext();
        var record = await db.NepRecords.SingleAsync(r => r.Id == id);
        Assert.Equal(stamp, record.ConcurrencyStamp);
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync());
    }

    [Fact]
    public async Task Update_Missing_Entity_Is_Invalid()
    {
        var sync = CreateSync();
        var response = await sync.PushAsync(
            PushUpdate("dev-2c", Guid.NewGuid().ToString("N"), Guid.NewGuid(), "stamp", 10),
            ActorAdmin(),
            "missing");

        Assert.Equal(nameof(SyncOperationResult.Invalid), response.Results[0].Result);
        Assert.Equal("ENTITY_NOT_FOUND", response.Results[0].ErrorCode);
    }

    [Fact]
    public async Task Update_Wrong_Stamp_Is_Conflict_Without_Mutation()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp) = await CreateViaPushAsync(sync, actor);

        var response = await sync.PushAsync(
            PushUpdate("dev-2c", Guid.NewGuid().ToString("N"), id, "wrong-stamp", 400),
            actor,
            "conflict");

        Assert.Equal(nameof(SyncOperationResult.Conflict), response.Results[0].Result);
        Assert.Equal(stamp, response.Results[0].ServerConcurrencyStamp);
        Assert.NotNull(response.Results[0].ServerSnapshot);
        Assert.Equal(id, response.Results[0].EntityId);

        await using var db = _factory.CreateDbContext();
        var record = await db.NepRecords.SingleAsync(r => r.Id == id);
        Assert.Equal(12, record.Neps);
        Assert.Equal(stamp, record.ConcurrencyStamp);
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync());
    }

    [Fact]
    public async Task Update_Retry_Same_ClientOperationId_Is_Duplicate()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp) = await CreateViaPushAsync(sync, actor);
        var opId = Guid.NewGuid().ToString("N");

        var first = await sync.PushAsync(PushUpdate("dev-2c", opId, id, stamp, 180), actor, "r1");
        Assert.Equal(nameof(SyncOperationResult.Accepted), first.Results[0].Result);
        var newStamp = first.Results[0].ConcurrencyStamp!;
        var seq = first.Results[0].ChangeSequence;

        var retry = await sync.PushAsync(PushUpdate("dev-2c", opId, id, stamp, 180), actor, "r2");
        Assert.Equal(nameof(SyncOperationResult.Duplicate), retry.Results[0].Result);
        Assert.Equal(seq, retry.Results[0].ChangeSequence);
        Assert.Equal(newStamp, retry.Results[0].ConcurrencyStamp);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.SyncChangeLogs.CountAsync());
        Assert.Equal(newStamp, (await db.NepRecords.SingleAsync(r => r.Id == id)).ConcurrencyStamp);
    }

    [Fact]
    public async Task Conflict_Scenario_A_Pull_B_Update_A_Update_X()
    {
        var sync = CreateSync();
        var a = ActorAdmin();
        var b = ActorAdminB();

        // A crea (ownership A); B con SeesAll puede actualizar.
        var (id, stampX) = await CreateViaPushAsync(sync, a);

        var pullA = await sync.PullAsync(Pull("dev-a", 0), a, "pa");
        Assert.Contains(pullA.Changes, c => c.EntityId == id);

        var bUpd = await sync.PushAsync(
            PushUpdate("dev-b", Guid.NewGuid().ToString("N"), id, stampX, 300, "T-B"),
            b,
            "b-upd");
        Assert.Equal(nameof(SyncOperationResult.Accepted), bUpd.Results[0].Result);
        var stampY = bUpd.Results[0].ConcurrencyStamp!;
        Assert.NotEqual(stampX, stampY);

        var aConflict = await sync.PushAsync(
            PushUpdate("dev-a", Guid.NewGuid().ToString("N"), id, stampX, 999, "T-A"),
            a,
            "a-conflict");
        Assert.Equal(nameof(SyncOperationResult.Conflict), aConflict.Results[0].Result);
        Assert.Equal(stampY, aConflict.Results[0].ServerConcurrencyStamp);

        await using var db = _factory.CreateDbContext();
        var record = await db.NepRecords.SingleAsync(r => r.Id == id);
        Assert.Equal(stampY, record.ConcurrencyStamp);
        Assert.Equal(300, record.Neps);
        Assert.Equal("T-B", record.Telar);
        Assert.Equal(2, await db.SyncChangeLogs.CountAsync(c =>
            c.ChangeType == SyncConstants.ChangeRecordUpserted));
    }

    [Fact]
    public async Task Delete_Valid_Creates_Tombstone_Atomically()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp) = await CreateViaPushAsync(sync, actor);

        var del = await sync.PushAsync(
            PushDelete("dev-2c", Guid.NewGuid().ToString("N"), id, stamp),
            actor,
            "del");
        Assert.Equal(nameof(SyncOperationResult.Accepted), del.Results[0].Result);
        Assert.Equal(id, del.Results[0].EntityId);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.NepRecords.CountAsync(r => r.Id == id));
        var tombstone = await db.SyncChangeLogs.SingleAsync(c =>
            c.ChangeType == SyncConstants.ChangeRecordDeleted && c.EntityId == id);
        using var doc = JsonDocument.Parse(tombstone.PayloadJson);
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(_adminId.ToString(), doc.RootElement.GetProperty("ownerUserId").GetString());
        Assert.Equal(stamp, doc.RootElement.GetProperty("lastConcurrencyStamp").GetString());
        Assert.True(doc.RootElement.TryGetProperty("deletedAtUtc", out _));
    }

    [Fact]
    public async Task Delete_Unauthorized_Is_Forbidden()
    {
        var sync = CreateSync();
        var admin = ActorAdmin();
        var (id, stamp) = await CreateViaPushAsync(sync, admin);

        var response = await sync.PushAsync(
            PushDelete("dev-op", Guid.NewGuid().ToString("N"), id, stamp),
            ActorOperario(),
            "forbid-del");

        Assert.Equal(nameof(SyncOperationResult.Forbidden), response.Results[0].Result);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync(r => r.Id == id));
        Assert.Equal(0, await db.SyncChangeLogs.CountAsync(c =>
            c.ChangeType == SyncConstants.ChangeRecordDeleted));
    }

    [Fact]
    public async Task Delete_Wrong_Stamp_Is_Conflict()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp) = await CreateViaPushAsync(sync, actor);

        var response = await sync.PushAsync(
            PushDelete("dev-2c", Guid.NewGuid().ToString("N"), id, "bad"),
            actor,
            "del-conflict");

        Assert.Equal(nameof(SyncOperationResult.Conflict), response.Results[0].Result);
        Assert.Equal(stamp, response.Results[0].ServerConcurrencyStamp);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync(r => r.Id == id));
    }

    [Fact]
    public async Task Delete_Missing_Is_Invalid()
    {
        var sync = CreateSync();
        var response = await sync.PushAsync(
            PushDelete("dev-2c", Guid.NewGuid().ToString("N"), Guid.NewGuid(), "x"),
            ActorAdmin(),
            "del-miss");
        Assert.Equal(nameof(SyncOperationResult.Invalid), response.Results[0].Result);
        Assert.Equal("ENTITY_NOT_FOUND", response.Results[0].ErrorCode);
    }

    [Fact]
    public async Task Delete_Retry_Same_ClientOperationId_Is_Duplicate_No_Extra_Tombstone()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp) = await CreateViaPushAsync(sync, actor);
        var opId = Guid.NewGuid().ToString("N");

        var first = await sync.PushAsync(PushDelete("dev-2c", opId, id, stamp), actor, "d1");
        Assert.Equal(nameof(SyncOperationResult.Accepted), first.Results[0].Result);
        var seq = first.Results[0].ChangeSequence;

        var retry = await sync.PushAsync(PushDelete("dev-2c", opId, id, stamp), actor, "d2");
        Assert.Equal(nameof(SyncOperationResult.Duplicate), retry.Results[0].Result);
        Assert.Equal(seq, retry.Results[0].ChangeSequence);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c =>
            c.ChangeType == SyncConstants.ChangeRecordDeleted));
    }

    [Fact]
    public async Task Delete_Already_Deleted_Different_OpId_Is_EntityDeleted()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp) = await CreateViaPushAsync(sync, actor);
        await sync.PushAsync(PushDelete("dev-2c", Guid.NewGuid().ToString("N"), id, stamp), actor, "d1");

        var second = await sync.PushAsync(
            PushDelete("dev-2c", Guid.NewGuid().ToString("N"), id, stamp),
            actor,
            "d2");
        Assert.Equal(nameof(SyncOperationResult.Invalid), second.Results[0].Result);
        Assert.Equal("ENTITY_DELETED", second.Results[0].ErrorCode);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c =>
            c.ChangeType == SyncConstants.ChangeRecordDeleted));
    }

    [Fact]
    public async Task Pull_Receives_Tombstone_After_Delete()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();
        var (id, stamp) = await CreateViaPushAsync(sync, actor);
        await sync.PushAsync(PushDelete("dev-2c", Guid.NewGuid().ToString("N"), id, stamp), actor, "del");

        var pull = await sync.PullAsync(Pull("dev-2c", 0), actor, "pull");
        Assert.Contains(pull.Changes, c =>
            c.EntityId == id && c.ChangeType == SyncConstants.ChangeRecordDeleted);
    }

    [Fact]
    public async Task Integration_Create_Pull_Update_Pull_Delete_Pull_Monotonic()
    {
        var sync = CreateSync();
        var actor = ActorAdmin();

        var createOp = Guid.NewGuid().ToString("N");
        var create = await sync.PushAsync(PushCreate("dev-2c", createOp, 40), actor, "i1");
        Assert.Equal(nameof(SyncOperationResult.Accepted), create.Results[0].Result);
        var id = create.Results[0].EntityId!.Value;
        var stamp = create.Results[0].ConcurrencyStamp!;
        var seqCreate = create.Results[0].ChangeSequence!.Value;

        var pull1 = await sync.PullAsync(Pull("dev-2c", 0), actor, "p1");
        Assert.Single(pull1.Changes);
        Assert.Equal(SyncConstants.ChangeRecordUpserted, pull1.Changes[0].ChangeType);
        Assert.Equal(seqCreate, pull1.NextCursor);

        var updOp = Guid.NewGuid().ToString("N");
        var upd = await sync.PushAsync(PushUpdate("dev-2c", updOp, id, stamp, 450), actor, "i2");
        Assert.Equal(nameof(SyncOperationResult.Accepted), upd.Results[0].Result);
        var seqUpdate = upd.Results[0].ChangeSequence!.Value;
        Assert.True(seqUpdate > seqCreate);
        stamp = upd.Results[0].ConcurrencyStamp!;

        var pull2 = await sync.PullAsync(Pull("dev-2c", pull1.NextCursor), actor, "p2");
        Assert.Single(pull2.Changes);
        Assert.Equal(SyncConstants.ChangeRecordUpserted, pull2.Changes[0].ChangeType);
        Assert.True(pull2.Changes[0].Payload.GetProperty("updatedAtUtc").ValueKind != JsonValueKind.Null);

        var delOp = Guid.NewGuid().ToString("N");
        var del = await sync.PushAsync(PushDelete("dev-2c", delOp, id, stamp), actor, "i3");
        Assert.Equal(nameof(SyncOperationResult.Accepted), del.Results[0].Result);
        var seqDelete = del.Results[0].ChangeSequence!.Value;
        Assert.True(seqDelete > seqUpdate);

        var pull3 = await sync.PullAsync(Pull("dev-2c", pull2.NextCursor), actor, "p3");
        Assert.Single(pull3.Changes);
        Assert.Equal(SyncConstants.ChangeRecordDeleted, pull3.Changes[0].ChangeType);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c => c.ClientOperationId == createOp));
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c => c.ClientOperationId == updOp));
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c => c.ClientOperationId == delOp));
        var sequences = await db.SyncChangeLogs.OrderBy(c => c.Sequence).Select(c => c.Sequence).ToListAsync();
        Assert.Equal(3, sequences.Count);
        Assert.True(sequences[0] < sequences[1] && sequences[1] < sequences[2]);
    }

    /// <summary>
    /// Concurrencia real (archivo SQLite): dos Push Update con el mismo ClientOperationId.
    /// :memory: serializa y puede colgarse con TX concurrentes.
    /// </summary>
    [Fact]
    public async Task Update_Parallel_Same_ClientOperationId_One_ChangeLog()
    {
        var path = Path.Combine(Path.GetTempPath(), $"regneps-2c-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<RegNepsDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            var factory = new TestDbFactory(options);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await DatabaseInitializer.ApplySchemaPatchesAsync(db);
                await DbSeeder.SeedAsync(db);
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

                var hash = BCrypt.Net.BCrypt.HashPassword("p");
                var admin = new AppUser
                {
                    Username = "par_admin",
                    DisplayName = "Par",
                    PasswordHash = hash,
                    Role = AppUserRole.Admin,
                    RoleCode = "Admin",
                    IsActive = true
                };
                db.Users.Add(admin);
                await db.SaveChangesAsync();

                var actor = RecordActor.Create(
                    admin.Id.ToString(), "par_admin", "Par", AppUserRole.Admin, false, null, "Admin", true);
                var sync = new SyncAppService(
                    new SyncPersistence(factory, new AtomicNepRecordCreateStore(factory)),
                    new PermissionService(
                        new RolePermissionRepository(factory.CreateDbContext()),
                        new RoleRepository(factory.CreateDbContext()),
                        new PermissionMatrix()),
                    NullLogger<SyncAppService>.Instance);

                var (id, stamp) = await CreateViaPushAsync(sync, actor);
                var opId = Guid.NewGuid().ToString("N");
                var req = PushUpdate("dev-par", opId, id, stamp, 30);

                var results = await Task.WhenAll(
                    sync.PushAsync(req, actor, "p1"),
                    sync.PushAsync(req, actor, "p2"));

                Assert.Equal(1, results.Count(r =>
                    r.Results[0].Result == nameof(SyncOperationResult.Accepted)));
                Assert.Equal(1, results.Count(r =>
                    r.Results[0].Result == nameof(SyncOperationResult.Duplicate)));
                Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c => c.ClientOperationId == opId));
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
            try { File.Delete(path + "-wal"); } catch { /* ignore */ }
            try { File.Delete(path + "-shm"); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Online_Update_And_Delete_Write_ChangeLog()
    {
        var atomic = new AtomicNepRecordCreateStore(_factory);
        var service = new NepRecordService(
            new NepRecordRepository(_factory),
            new AlertConfigRepository(_factory.CreateDbContext()),
            atomicCreate: atomic);
        var actor = ActorAdmin();

        var created = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "ON-1",
            Neps = 15,
            Tela = "X",
            LoteTrama = "L",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);

        var updated = await service.UpdateAsync(new UpdateNepRecordRequest
        {
            Id = created.Id,
            Telar = "ON-2",
            Neps = 120,
            Tela = "X",
            LoteTrama = "L",
            ExpectedConcurrencyStamp = created.ConcurrencyStamp
        }, actor);

        Assert.NotEqual(created.ConcurrencyStamp, updated.ConcurrencyStamp);
        Assert.NotNull(updated.UpdatedAt);

        await service.DeleteAsync(updated.Id, actor);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.NepRecords.CountAsync(r => r.Id == created.Id));
        Assert.True(await db.SyncChangeLogs.CountAsync(c =>
            c.EntityId == created.Id && c.ChangeType == SyncConstants.ChangeRecordUpserted) >= 2);
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c =>
            c.EntityId == created.Id && c.ChangeType == SyncConstants.ChangeRecordDeleted));
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
