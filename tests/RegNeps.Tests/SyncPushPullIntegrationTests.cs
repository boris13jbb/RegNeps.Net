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

/// <summary>Integración FASE 2B: Push/Pull, idempotencia, cursor y autorización.</summary>
public sealed class SyncPushPullIntegrationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;
    private Guid _operarioAId;
    private Guid _operarioBId;
    private Guid _supervisorId;

    public SyncPushPullIntegrationTests()
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

        var hash = BCrypt.Net.BCrypt.HashPassword("SyncTestOnly!");
        var a = new AppUser
        {
            Username = "sync_op_a",
            DisplayName = "Op A",
            PasswordHash = hash,
            Role = AppUserRole.Operario,
            RoleCode = "Operario",
            IsActive = true
        };
        var b = new AppUser
        {
            Username = "sync_op_b",
            DisplayName = "Op B",
            PasswordHash = hash,
            Role = AppUserRole.Operario,
            RoleCode = "Operario",
            IsActive = true
        };
        var supervisor = new AppUser
        {
            Username = "sync_supervisor",
            DisplayName = "Supervisor",
            PasswordHash = hash,
            Role = AppUserRole.Supervisor,
            RoleCode = "Supervisor",
            IsActive = true
        };
        db.Users.AddRange(a, b, supervisor);
        await db.SaveChangesAsync();
        _operarioAId = a.Id;
        _operarioBId = b.Id;
        _supervisorId = supervisor.Id;
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private SyncAppService CreateSync()
    {
        // Role repos usan DbContext scoped; SyncPersistence usa factory (misma conexión :memory:).
        var db = _factory.CreateDbContext();
        var matrix = new PermissionMatrix();
        var permissions = new PermissionService(
            new RolePermissionRepository(db),
            new RoleRepository(db),
            matrix);
        return new SyncAppService(
            new SyncPersistence(_factory),
            permissions,
            NullLogger<SyncAppService>.Instance);
    }

    private RecordActor ActorA(bool? seesAll = false) =>
        RecordActor.Create(_operarioAId.ToString(), "sync_op_a", "Op A", AppUserRole.Operario, false, null, "Operario", seesAll);

    private RecordActor ActorB() =>
        RecordActor.Create(_operarioBId.ToString(), "sync_op_b", "Op B", AppUserRole.Operario, false, null, "Operario", false);

    private RecordActor ActorSupervisor() =>
        RecordActor.Create(_supervisorId.ToString(), "sync_supervisor", "Supervisor", AppUserRole.Supervisor, false, null, "Supervisor", true);

    private static SyncPushRequest PushCreate(
        string deviceId,
        string clientOperationId,
        double neps = 12,
        string telar = "T-1",
        string? captureSessionId = "sess-1") =>
        new()
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = deviceId,
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = clientOperationId,
                    OperationType = SyncConstants.OperationCreateRecord,
                    CaptureSessionId = captureSessionId,
                    ClientCreatedAtUtc = DateTime.UtcNow.AddHours(-2),
                    Payload = JsonSerializer.SerializeToElement(new SyncCreateRecordPayload
                    {
                        Telar = telar,
                        Neps = neps,
                        Tela = "Denim",
                        LoteTrama = "L1",
                        Turno = "A",
                        Operario = "op",
                        // Campos de identidad falsos en payload: deben ignorarse.
                        Observacion = "createdByUserId=evil;role=SuperAdmin"
                    })
                }
            ]
        };

    [Fact]
    public async Task Push_Create_Valid_Creates_Record_And_ChangeLog()
    {
        var sync = CreateSync();
        var opId = Guid.NewGuid().ToString("N");
        var response = await sync.PushAsync(PushCreate("device-a", opId), ActorA(), "corr-1");

        Assert.Single(response.Results);
        Assert.Equal(nameof(SyncOperationResult.Accepted), response.Results[0].Result);
        Assert.NotNull(response.Results[0].EntityId);
        Assert.False(string.IsNullOrWhiteSpace(response.Results[0].ConcurrencyStamp));
        Assert.Equal("OK", response.Results[0].QualityLabel);
        Assert.True(response.Results[0].ChangeSequence > 0);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync());
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync());
        var record = await db.NepRecords.SingleAsync();
        Assert.Equal(_operarioAId.ToString(), record.CreatedByUserId);
        Assert.Equal(opId, record.ClientOperationId);
    }

    [Fact]
    public async Task Push_Without_CapturePermission_Is_Forbidden()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var role = await db.Roles.SingleAsync(r => r.Code == "Operario");
            var perm = await db.RolePermissions.SingleAsync(p =>
                p.RoleId == role.Id && p.Permission == AppPermission.CaptureRecords);
            perm.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        try
        {
            var sync = CreateSync();
            var response = await sync.PushAsync(
                PushCreate("device-a", Guid.NewGuid().ToString("N")),
                ActorA(),
                "corr-forbid");

            Assert.Equal(nameof(SyncOperationResult.Forbidden), response.Results[0].Result);
            await using var verify = _factory.CreateDbContext();
            Assert.Equal(0, await verify.NepRecords.CountAsync());
            Assert.Equal(0, await verify.SyncChangeLogs.CountAsync());
        }
        finally
        {
            await using var db = _factory.CreateDbContext();
            var role = await db.Roles.SingleAsync(r => r.Code == "Operario");
            var perm = await db.RolePermissions.SingleAsync(p =>
                p.RoleId == role.Id && p.Permission == AppPermission.CaptureRecords);
            perm.IsEnabled = true;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Push_Invalid_Payload_Is_Invalid()
    {
        var sync = CreateSync();
        var request = PushCreate("device-a", Guid.NewGuid().ToString("N"), neps: -1);
        var response = await sync.PushAsync(request, ActorA(), "corr-invalid");
        Assert.Equal(nameof(SyncOperationResult.Invalid), response.Results[0].Result);
        Assert.Equal("VALIDATION", response.Results[0].ErrorCode);
    }

    [Fact]
    public async Task Push_Unknown_OperationType_Is_Invalid()
    {
        var sync = CreateSync();
        var request = new SyncPushRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "device-a",
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = Guid.NewGuid().ToString("N"),
                    OperationType = SyncConstants.OperationUpdateRecord,
                    Payload = JsonSerializer.SerializeToElement(new { telar = "1", neps = 1 })
                }
            ]
        };
        var response = await sync.PushAsync(request, ActorA(), "corr-op");
        Assert.Equal(nameof(SyncOperationResult.Invalid), response.Results[0].Result);
        Assert.Equal("OPERATION_TYPE_UNSUPPORTED", response.Results[0].ErrorCode);
    }

    [Fact]
    public async Task Push_Empty_ClientOperationId_Is_Invalid()
    {
        var sync = CreateSync();
        var request = PushCreate("device-a", "   ");
        var response = await sync.PushAsync(request, ActorA(), "corr-empty");
        Assert.Equal(nameof(SyncOperationResult.Invalid), response.Results[0].Result);
        Assert.Equal("CLIENT_OPERATION_ID", response.Results[0].ErrorCode);
    }

    [Fact]
    public async Task Push_Idempotent_Triple_Retry_Creates_Single_Record_And_Change()
    {
        var sync = CreateSync();
        var opId = Guid.NewGuid().ToString("N");
        var device = "device-a";
        var a1 = await sync.PushAsync(PushCreate(device, opId, 25), ActorA(), "r1");
        var a2 = await sync.PushAsync(PushCreate(device, opId, 25), ActorA(), "r2");
        var a3 = await sync.PushAsync(PushCreate(device, opId, 25), ActorA(), "r3");

        Assert.Equal(nameof(SyncOperationResult.Accepted), a1.Results[0].Result);
        Assert.Equal(nameof(SyncOperationResult.Duplicate), a2.Results[0].Result);
        Assert.Equal(nameof(SyncOperationResult.Duplicate), a3.Results[0].Result);
        Assert.Equal(a1.Results[0].EntityId, a2.Results[0].EntityId);
        Assert.Equal(a1.Results[0].EntityId, a3.Results[0].EntityId);
        Assert.Equal(a1.Results[0].ConcurrencyStamp, a2.Results[0].ConcurrencyStamp);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync());
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync());
    }

    [Fact]
    public async Task Push_Transaction_Rollback_Leaves_No_Record_Nor_ChangeLog()
    {
        await using (var setup = _factory.CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER IF NOT EXISTS trg_reject_sync_changelog
                BEFORE INSERT ON SyncChangeLogs
                BEGIN
                    SELECT RAISE(ABORT, 'forced changelog reject');
                END;
                """);
        }

        var sync = CreateSync();
        var response = await sync.PushAsync(
            PushCreate("device-a", Guid.NewGuid().ToString("N")),
            ActorA(),
            "corr-rollback");

        Assert.Equal(nameof(SyncOperationResult.TransientError), response.Results[0].Result);

        await using var verify = _factory.CreateDbContext();
        Assert.Equal(0, await verify.NepRecords.CountAsync());
        Assert.Equal(0, await verify.SyncChangeLogs.CountAsync());
    }

    [Fact]
    public async Task Push_Then_Pull_Returns_Change_Once_And_Retry_Does_Not_Duplicate_Pull()
    {
        var sync = CreateSync();
        var opId = Guid.NewGuid().ToString("N");
        var push1 = await sync.PushAsync(PushCreate("device-a", opId, 30), ActorA(), "p1");
        Assert.Equal(nameof(SyncOperationResult.Accepted), push1.Results[0].Result);

        var pull1 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "device-a",
            Cursor = 0,
            PageSize = 50
        }, ActorA(), "pull1");

        Assert.Single(pull1.Changes);
        Assert.Equal(push1.Results[0].EntityId, pull1.Changes[0].EntityId);
        Assert.Equal(SyncConstants.ChangeRecordUpserted, pull1.Changes[0].ChangeType);
        Assert.True(pull1.ServerTimeUtc.Kind == DateTimeKind.Utc || pull1.ServerTimeUtc.Kind == DateTimeKind.Unspecified);

        await sync.PushAsync(PushCreate("device-a", opId, 30), ActorA(), "p2");

        var pull2 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "device-a",
            Cursor = 0,
            PageSize = 50
        }, ActorA(), "pull2");

        Assert.Single(pull2.Changes);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync());
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync());
    }

    [Fact]
    public async Task Pull_Skips_Unauthorized_Sequences_But_Advances_NextCursor()
    {
        var sync = CreateSync();
        var opA1 = Guid.NewGuid().ToString("N");
        var opB = Guid.NewGuid().ToString("N");
        var opA2 = Guid.NewGuid().ToString("N");

        var r1 = await sync.PushAsync(PushCreate("dev-a", opA1, 10, "TA"), ActorA(), "1");
        var r2 = await sync.PushAsync(PushCreate("dev-b", opB, 11, "TB"), ActorB(), "2");
        var r3 = await sync.PushAsync(PushCreate("dev-a", opA2, 12, "TC"), ActorA(), "3");

        Assert.Equal(nameof(SyncOperationResult.Accepted), r1.Results[0].Result);
        Assert.Equal(nameof(SyncOperationResult.Accepted), r2.Results[0].Result);
        Assert.Equal(nameof(SyncOperationResult.Accepted), r3.Results[0].Result);

        var seq1 = r1.Results[0].ChangeSequence!.Value;
        var seq2 = r2.Results[0].ChangeSequence!.Value;
        var seq3 = r3.Results[0].ChangeSequence!.Value;
        Assert.True(seq1 < seq2 && seq2 < seq3);

        // Equivalente al caso 100/101/102: A ve 1 y 3, no 2; NextCursor = 3.
        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = 0,
            PageSize = 10
        }, ActorA(), "cursor-skip");

        Assert.Equal(2, pull.Changes.Count);
        Assert.Equal(new[] { seq1, seq3 }, pull.Changes.Select(c => c.Sequence).ToArray());
        Assert.DoesNotContain(pull.Changes, c => c.Sequence == seq2);
        Assert.Equal(seq3, pull.NextCursor);
        Assert.False(pull.HasMore);
    }

    [Fact]
    public async Task Pull_PageSize_And_Ordering_And_Empty()
    {
        var sync = CreateSync();
        for (var i = 0; i < 3; i++)
        {
            await sync.PushAsync(
                PushCreate("dev-a", Guid.NewGuid().ToString("N"), 10 + i, $"T{i}"),
                ActorA(),
                $"p{i}");
        }

        var page1 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = 0,
            PageSize = 2
        }, ActorA(), "page1");

        Assert.Equal(2, page1.Changes.Count);
        Assert.True(page1.HasMore);
        Assert.True(page1.Changes[0].Sequence < page1.Changes[1].Sequence);

        var page2 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = page1.NextCursor,
            PageSize = 2
        }, ActorA(), "page2");

        Assert.Single(page2.Changes);
        Assert.False(page2.HasMore);

        var empty = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = page2.NextCursor,
            PageSize = 2
        }, ActorA(), "empty");

        Assert.Empty(empty.Changes);
        Assert.Equal(page2.NextCursor, empty.NextCursor);
        Assert.False(empty.HasMore);
    }

    [Fact]
    public async Task Pull_PageSize_Is_Clamped_To_Max()
    {
        var sync = CreateSync();
        await sync.PushAsync(PushCreate("dev-a", Guid.NewGuid().ToString("N")), ActorA(), "x");

        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = 0,
            PageSize = 50_000
        }, ActorA(), "max");

        Assert.Single(pull.Changes);
        Assert.Equal(SyncConstants.MaxPageSize, SyncProtocol.NormalizePageSize(50_000));
    }

    [Fact]
    public async Task Pull_Supervisor_Sees_All_Authorized_Changes()
    {
        var sync = CreateSync();
        await sync.PushAsync(PushCreate("dev-a", Guid.NewGuid().ToString("N")), ActorA(), "a");
        await sync.PushAsync(PushCreate("dev-b", Guid.NewGuid().ToString("N")), ActorB(), "b");

        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-sup",
            Cursor = 0,
            PageSize = 50
        }, ActorSupervisor(), "sup");

        Assert.Equal(2, pull.Changes.Count);
    }

    [Fact]
    public async Task Push_Ignores_Client_Timestamps_And_Fake_Ownership_Hints()
    {
        var sync = CreateSync();
        var clientTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var request = PushCreate("other-device-id", Guid.NewGuid().ToString("N"));
        request.Operations[0].ClientCreatedAtUtc = clientTime;

        var response = await sync.PushAsync(request, ActorA(), "security");
        Assert.Equal(nameof(SyncOperationResult.Accepted), response.Results[0].Result);

        await using var db = _factory.CreateDbContext();
        var record = await db.NepRecords.SingleAsync();
        Assert.Equal(_operarioAId.ToString(), record.CreatedByUserId);
        Assert.NotEqual(clientTime, record.CreatedAt);
        Assert.True(record.CreatedAt >= DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task SyncChangeLogs_Table_Exists_After_Patches()
    {
        await using var db = _factory.CreateDbContext();
        Assert.True(await DatabaseInitializer.SqliteTableExistsAsync(db, "SyncChangeLogs"));
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
