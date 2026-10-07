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

/// <summary>FASE 2B.1: captura online escribe SyncChangeLog atómicamente y es visible en Pull.</summary>
public sealed class SyncOnlineChangeLogTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;
    private Guid _userAId;
    private Guid _userBId;

    public SyncOnlineChangeLogTests()
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

        var hash = BCrypt.Net.BCrypt.HashPassword("OnlineSyncTest!");
        var a = new AppUser
        {
            Username = "online_a",
            DisplayName = "Online A",
            PasswordHash = hash,
            Role = AppUserRole.Operario,
            RoleCode = "Operario",
            IsActive = true
        };
        var b = new AppUser
        {
            Username = "online_b",
            DisplayName = "Online B",
            PasswordHash = hash,
            Role = AppUserRole.Operario,
            RoleCode = "Operario",
            IsActive = true
        };
        db.Users.AddRange(a, b);
        await db.SaveChangesAsync();
        _userAId = a.Id;
        _userBId = b.Id;

        await new SyncPersistence(_factory, new AtomicNepRecordCreateStore(_factory))
            .EnsureCatalogBaselineAsync();
    }

    private async Task<long> CatalogCursorAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.SyncChangeLogs.AsNoTracking()
            .Where(c => c.EntityType == SyncConstants.EntityCatalogItem)
            .Select(c => (long?)c.Sequence)
            .MaxAsync() ?? 0L;
    }

    private static List<SyncChangeDto> NepChanges(SyncPullResponse pull) =>
        pull.Changes.Where(c => c.EntityType == SyncConstants.EntityNepRecord).ToList();

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private NepRecordService CreateOnlineService()
    {
        var atomic = new AtomicNepRecordCreateStore(_factory);
        return new NepRecordService(
            new NepRecordRepository(_factory),
            new AlertConfigRepository(_factory.CreateDbContext()),
            atomicCreate: atomic);
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

    private RecordActor ActorA() =>
        RecordActor.Create(_userAId.ToString(), "online_a", "Online A", AppUserRole.Operario, false, null, "Operario", false);

    private RecordActor ActorB() =>
        RecordActor.Create(_userBId.ToString(), "online_b", "Online B", AppUserRole.Operario, false, null, "Operario", false);

    [Fact]
    public async Task Online_Create_Writes_NepRecord_And_ChangeLog()
    {
        var service = CreateOnlineService();
        var opId = Guid.NewGuid().ToString("N");
        var saved = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T-ON",
            Neps = 12,
            Tela = "Denim",
            LoteTrama = "L1",
            ClientOperationId = opId,
            CaptureSessionId = "sess-online"
        }, ActorA());

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync());
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c =>
            c.EntityType == SyncConstants.EntityNepRecord));
        var log = await db.SyncChangeLogs.SingleAsync(c =>
            c.EntityType == SyncConstants.EntityNepRecord);
        Assert.Equal(saved.Id, log.EntityId);
        Assert.Equal(SyncConstants.ChangeRecordUpserted, log.ChangeType);
        Assert.Equal(_userAId.ToString(), log.OwnerUserId);
        Assert.Equal(_userAId.ToString(), log.ActorUserId);
        Assert.Equal(opId, log.ClientOperationId);
        Assert.Null(log.DeviceId);

        var payload = JsonSerializer.Deserialize<SyncNepRecordPayload>(
            log.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(payload);
        Assert.Equal(saved.Id, payload!.Id);
        Assert.Equal("T-ON", payload.Telar);
        Assert.Equal(saved.ConcurrencyStamp, payload.ConcurrencyStamp);
    }

    [Fact]
    public async Task Online_ChangeLog_Insert_Failure_Rolls_Back_Record()
    {
        await using (var setup = _factory.CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER IF NOT EXISTS trg_reject_online_changelog
                BEFORE INSERT ON SyncChangeLogs
                BEGIN
                    SELECT RAISE(ABORT, 'forced online changelog reject');
                END;
                """);
        }

        try
        {
            var service = CreateOnlineService();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.CreateAsync(new CreateNepRecordRequest
                {
                    Telar = "T-FAIL",
                    Neps = 10,
                    ClientOperationId = Guid.NewGuid().ToString("N")
                }, ActorA()));

            await using var verify = _factory.CreateDbContext();
            Assert.Equal(0, await verify.NepRecords.CountAsync());
            Assert.Equal(0, await verify.SyncChangeLogs.CountAsync(c =>
                c.EntityType == SyncConstants.EntityNepRecord));
        }
        finally
        {
            await using var cleanup = _factory.CreateDbContext();
            await cleanup.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS trg_reject_online_changelog;");
        }
    }

    [Fact]
    public async Task Online_Idempotent_ClientOperationId_Single_Record_And_ChangeLog()
    {
        var service = CreateOnlineService();
        var opId = Guid.NewGuid().ToString("N");
        var req = new CreateNepRecordRequest
        {
            Telar = "T-ID",
            Neps = 20,
            ClientOperationId = opId,
            CaptureSessionId = "s1"
        };

        var r1 = await service.CreateWithOutcomeAsync(req, ActorA());
        var r2 = await service.CreateWithOutcomeAsync(req, ActorA());

        Assert.Equal(RecordSaveStatus.Saved, r1.Status);
        Assert.Equal(RecordSaveStatus.AlreadySaved, r2.Status);
        Assert.Equal(r1.Record!.Id, r2.Record!.Id);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync());
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c =>
            c.EntityType == SyncConstants.EntityNepRecord));
    }

    [Fact]
    public async Task Online_Create_Then_Pull_Returns_Exactly_Once()
    {
        var online = CreateOnlineService();
        var sync = CreateSync();
        var actor = ActorA();
        var afterCatalogs = await CatalogCursorAsync();

        var saved = await online.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T-PULL",
            Neps = 14,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);

        var pull1 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-pull",
            Cursor = afterCatalogs,
            PageSize = 50
        }, actor, "p1");

        var nep1 = NepChanges(pull1);
        Assert.Single(nep1);
        Assert.Equal(saved.Id, nep1[0].EntityId);

        var pull2 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-pull",
            Cursor = pull1.NextCursor,
            PageSize = 50
        }, actor, "p2");

        Assert.Empty(NepChanges(pull2));
        Assert.Equal(pull1.NextCursor, pull2.NextCursor);
    }

    [Fact]
    public async Task Online_And_Push_Both_Visible_In_Pull_Ordered_By_Sequence()
    {
        var online = CreateOnlineService();
        var sync = CreateSync();
        var actorA = ActorA();
        var actorB = ActorB();

        var onlineRecord = await online.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T-ON2",
            Neps = 11,
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actorA);

        var pushReq = new SyncPushRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-b",
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = Guid.NewGuid().ToString("N"),
                    OperationType = SyncConstants.OperationCreateRecord,
                    Payload = JsonSerializer.SerializeToElement(new SyncCreateRecordPayload
                    {
                        Telar = "T-PUSH",
                        Neps = 13,
                        Tela = "X"
                    })
                }
            ]
        };
        var push = await sync.PushAsync(pushReq, actorB, "push");
        Assert.Equal(nameof(SyncOperationResult.Accepted), push.Results[0].Result);

        var afterCatalogs = await CatalogCursorAsync();
        // Supervisor-like: each user pulls own; A sees online, B sees push.
        var pullA = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = afterCatalogs,
            PageSize = 50
        }, actorA, "pa");
        var nepA = NepChanges(pullA);
        Assert.Single(nepA);
        Assert.Equal(onlineRecord.Id, nepA[0].EntityId);

        var pullB = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-b",
            Cursor = afterCatalogs,
            PageSize = 50
        }, actorB, "pb");
        var nepB = NepChanges(pullB);
        Assert.Single(nepB);
        Assert.Equal(push.Results[0].EntityId, nepB[0].EntityId);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.NepRecords.CountAsync());
        Assert.Equal(2, await db.SyncChangeLogs.CountAsync(c =>
            c.EntityType == SyncConstants.EntityNepRecord));
        var sequences = await db.SyncChangeLogs.OrderBy(c => c.Sequence)
            .Select(c => c.Sequence)
            .ToListAsync();
        Assert.True(sequences[0] < sequences[1]);
    }

    [Fact]
    public async Task Online_And_Push_Payload_Format_Is_Compatible()
    {
        var online = CreateOnlineService();
        var sync = CreateSync();
        var actor = ActorA();

        await online.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T-FMT",
            Neps = 12,
            Tela = "Denim",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);

        await sync.PushAsync(new SyncPushRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-fmt",
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = Guid.NewGuid().ToString("N"),
                    OperationType = SyncConstants.OperationCreateRecord,
                    Payload = JsonSerializer.SerializeToElement(new SyncCreateRecordPayload
                    {
                        Telar = "T-FMT2",
                        Neps = 12,
                        Tela = "Denim"
                    })
                }
            ]
        }, actor, "fmt");

        await using var db = _factory.CreateDbContext();
        var payloads = await db.SyncChangeLogs.AsNoTracking()
            .Where(c => c.EntityType == SyncConstants.EntityNepRecord)
            .OrderBy(c => c.Sequence)
            .Select(c => c.PayloadJson)
            .ToListAsync();
        Assert.Equal(2, payloads.Count);

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var json in payloads)
        {
            var p = JsonSerializer.Deserialize<SyncNepRecordPayload>(json, opts);
            Assert.NotNull(p);
            Assert.False(string.IsNullOrWhiteSpace(p!.Telar));
            Assert.False(string.IsNullOrWhiteSpace(p.ConcurrencyStamp));
            Assert.False(string.IsNullOrWhiteSpace(p.QualityLabel));
            Assert.Equal(actor.UserId, p.OwnerUserId);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("mtsCalculados", out _));
            Assert.True(doc.RootElement.TryGetProperty("createdAtUtc", out _));
        }
    }

    [Fact]
    public async Task Push_Still_Single_ChangeLog_Per_Operation()
    {
        var sync = CreateSync();
        var opId = Guid.NewGuid().ToString("N");
        var req = new SyncPushRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-once",
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = opId,
                    OperationType = SyncConstants.OperationCreateRecord,
                    Payload = JsonSerializer.SerializeToElement(new SyncCreateRecordPayload
                    {
                        Telar = "T-ONCE",
                        Neps = 10
                    })
                }
            ]
        };

        await sync.PushAsync(req, ActorA(), "1");
        await sync.PushAsync(req, ActorA(), "2");

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync());
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c => c.ClientOperationId == opId));
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
