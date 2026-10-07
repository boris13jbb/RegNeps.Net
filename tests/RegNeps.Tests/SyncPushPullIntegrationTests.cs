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

        // FASE 2D.9: catálogos en ChangeLog antes de Push/Pull de pruebas NepRecord.
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

    private SyncAppService CreateSync()
    {
        // Role repos usan DbContext scoped; SyncPersistence usa factory (misma conexión :memory:).
        var db = _factory.CreateDbContext();
        var matrix = new PermissionMatrix();
        var permissions = new PermissionService(
            new RolePermissionRepository(db),
            new RoleRepository(db),
            matrix);
        var atomic = new AtomicNepRecordCreateStore(_factory);
        return new SyncAppService(
            new SyncPersistence(_factory, atomic),
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
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c =>
            c.EntityType == SyncConstants.EntityNepRecord));
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
            Assert.Equal(0, await verify.SyncChangeLogs.CountAsync(c =>
                c.EntityType == SyncConstants.EntityNepRecord));
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
                    OperationType = "UpsertCatalog",
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
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c =>
            c.EntityType == SyncConstants.EntityNepRecord));
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

        try
        {
            var sync = CreateSync();
            var response = await sync.PushAsync(
                PushCreate("device-a", Guid.NewGuid().ToString("N")),
                ActorA(),
                "corr-rollback");

            Assert.Equal(nameof(SyncOperationResult.TransientError), response.Results[0].Result);

            await using var verify = _factory.CreateDbContext();
            Assert.Equal(0, await verify.NepRecords.CountAsync());
            Assert.Equal(0, await verify.SyncChangeLogs.CountAsync(c =>
                c.EntityType == SyncConstants.EntityNepRecord));
        }
        finally
        {
            await using var cleanup = _factory.CreateDbContext();
            await cleanup.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS trg_reject_sync_changelog;");
        }
    }

    [Fact]
    public async Task Push_Then_Pull_Returns_Change_Once_And_Retry_Does_Not_Duplicate_Pull()
    {
        var sync = CreateSync();
        var afterCatalogs = await CatalogCursorAsync();
        var opId = Guid.NewGuid().ToString("N");
        var push1 = await sync.PushAsync(PushCreate("device-a", opId, 30), ActorA(), "p1");
        Assert.Equal(nameof(SyncOperationResult.Accepted), push1.Results[0].Result);

        var pull1 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "device-a",
            Cursor = afterCatalogs,
            PageSize = 50
        }, ActorA(), "pull1");

        var nep1 = NepChanges(pull1);
        Assert.Single(nep1);
        Assert.Equal(push1.Results[0].EntityId, nep1[0].EntityId);
        Assert.Equal(SyncConstants.ChangeRecordUpserted, nep1[0].ChangeType);
        Assert.True(pull1.ServerTimeUtc.Kind == DateTimeKind.Utc || pull1.ServerTimeUtc.Kind == DateTimeKind.Unspecified);

        await sync.PushAsync(PushCreate("device-a", opId, 30), ActorA(), "p2");

        var pull2 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "device-a",
            Cursor = afterCatalogs,
            PageSize = 50
        }, ActorA(), "pull2");

        Assert.Single(NepChanges(pull2));

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync());
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c =>
            c.EntityType == SyncConstants.EntityNepRecord));
    }

    [Fact]
    public async Task Pull_Skips_Unauthorized_Sequences_But_Advances_NextCursor()
    {
        var sync = CreateSync();
        var afterCatalogs = await CatalogCursorAsync();
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
            Cursor = afterCatalogs,
            PageSize = 10
        }, ActorA(), "cursor-skip");

        var nep = NepChanges(pull);
        Assert.Equal(2, nep.Count);
        Assert.Equal(new[] { seq1, seq3 }, nep.Select(c => c.Sequence).ToArray());
        Assert.DoesNotContain(nep, c => c.Sequence == seq2);
        Assert.Equal(seq3, pull.NextCursor);
        Assert.False(pull.HasMore);
    }

    [Fact]
    public async Task Pull_PageSize_And_Ordering_And_Empty()
    {
        var sync = CreateSync();
        var afterCatalogs = await CatalogCursorAsync();
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
            Cursor = afterCatalogs,
            PageSize = 2
        }, ActorA(), "page1");

        Assert.Equal(2, page1.Changes.Count);
        Assert.All(page1.Changes, c => Assert.Equal(SyncConstants.EntityNepRecord, c.EntityType));
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
        Assert.Equal(SyncConstants.EntityNepRecord, page2.Changes[0].EntityType);
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
        var afterCatalogs = await CatalogCursorAsync();
        await sync.PushAsync(PushCreate("dev-a", Guid.NewGuid().ToString("N")), ActorA(), "x");

        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = afterCatalogs,
            PageSize = 50_000
        }, ActorA(), "max");

        Assert.Single(NepChanges(pull));
        Assert.Equal(SyncConstants.MaxPageSize, SyncProtocol.NormalizePageSize(50_000));
    }

    [Fact]
    public async Task Pull_Supervisor_Sees_All_Authorized_Changes()
    {
        var sync = CreateSync();
        var afterCatalogs = await CatalogCursorAsync();
        await sync.PushAsync(PushCreate("dev-a", Guid.NewGuid().ToString("N")), ActorA(), "a");
        await sync.PushAsync(PushCreate("dev-b", Guid.NewGuid().ToString("N")), ActorB(), "b");

        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-sup",
            Cursor = afterCatalogs,
            PageSize = 50
        }, ActorSupervisor(), "sup");

        Assert.Equal(2, NepChanges(pull).Count);
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

    [Fact]
    public async Task Push_Concurrent_Same_ClientOperationId_Yields_Single_Record()
    {
        var opId = Guid.NewGuid().ToString("N");
        var actor = ActorA();
        var sync = CreateSync();

        var tasks = Enumerable.Range(0, 8)
            .Select(i => sync.PushAsync(PushCreate($"dev-{i}", opId, 15, "T-PAR"), actor, $"c-{i}"))
            .ToArray();

        var responses = await Task.WhenAll(tasks);
        var results = responses.Select(r => r.Results[0].Result).ToList();

        Assert.Contains(nameof(SyncOperationResult.Accepted), results);
        Assert.All(results, r =>
            Assert.True(r is nameof(SyncOperationResult.Accepted)
                or nameof(SyncOperationResult.Duplicate)
                or nameof(SyncOperationResult.TransientError)));

        // Reintentos tras TransientError de carrera deben converger a Duplicate.
        var final = await sync.PushAsync(PushCreate("dev-final", opId, 15, "T-PAR"), actor, "final");
        Assert.True(final.Results[0].Result is nameof(SyncOperationResult.Accepted)
            or nameof(SyncOperationResult.Duplicate));

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync(r => r.ClientOperationId == opId));
        Assert.Equal(1, await db.SyncChangeLogs.CountAsync(c => c.ClientOperationId == opId));
    }

    [Fact]
    public async Task Pull_Cursor_Ahead_Of_Max_Is_Clamped()
    {
        var sync = CreateSync();
        var push = await sync.PushAsync(PushCreate("dev-a", Guid.NewGuid().ToString("N")), ActorA(), "ahead");
        var maxSeq = push.Results[0].ChangeSequence!.Value;

        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = maxSeq + 10_000,
            PageSize = 10
        }, ActorA(), "cursor-ahead");

        Assert.Empty(pull.Changes);
        Assert.Equal(maxSeq, pull.NextCursor);
        Assert.False(pull.HasMore);

        // Un insert posterior debe ser visible desde el cursor anclado.
        var later = await sync.PushAsync(PushCreate("dev-a", Guid.NewGuid().ToString("N"), 16, "T-L"), ActorA(), "later");
        var pull2 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = pull.NextCursor,
            PageSize = 10
        }, ActorA(), "after-clamp");

        Assert.Contains(NepChanges(pull2), c => c.EntityId == later.Results[0].EntityId);
    }

    [Fact]
    public async Task Pull_Invalid_DeviceId_Does_Not_Rewind_Cursor()
    {
        var sync = CreateSync();
        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "",
            Cursor = 42,
            PageSize = 10
        }, ActorA(), "bad-device");

        Assert.Empty(pull.Changes);
        Assert.Equal(42, pull.NextCursor);
        Assert.False(pull.HasMore);
    }

    [Fact]
    public async Task Pull_Only_Unauthorized_Advances_Cursor_Without_Leaking_Payloads()
    {
        var sync = CreateSync();
        var afterCatalogs = await CatalogCursorAsync();
        await sync.PushAsync(PushCreate("dev-b", Guid.NewGuid().ToString("N"), 10, "TB1"), ActorB(), "b1");
        await sync.PushAsync(PushCreate("dev-b", Guid.NewGuid().ToString("N"), 11, "TB2"), ActorB(), "b2");

        var pull = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = afterCatalogs,
            PageSize = 10
        }, ActorA(), "only-foreign");

        // Catálogos son visibles a todo autenticado; NepRecord ajenos no se filtran al payload.
        Assert.Empty(NepChanges(pull));
        Assert.DoesNotContain(pull.Changes, c => c.EntityType == SyncConstants.EntityNepRecord);
        Assert.True(pull.NextCursor > afterCatalogs);
        Assert.False(pull.HasMore);
    }

    [Fact]
    public async Task Pull_Dense_Unauthorized_Then_Visible_Keeps_Examined_Cursor()
    {
        var sync = CreateSync();
        var afterCatalogs = await CatalogCursorAsync();
        // 100,101,102,103,104 conceptual: B,B,A,B,A
        await sync.PushAsync(PushCreate("dev-b", Guid.NewGuid().ToString("N"), 10, "U1"), ActorB(), "u1");
        await sync.PushAsync(PushCreate("dev-b", Guid.NewGuid().ToString("N"), 10, "U2"), ActorB(), "u2");
        var a1 = await sync.PushAsync(PushCreate("dev-a", Guid.NewGuid().ToString("N"), 10, "V1"), ActorA(), "v1");
        await sync.PushAsync(PushCreate("dev-b", Guid.NewGuid().ToString("N"), 10, "U3"), ActorB(), "u3");
        var a2 = await sync.PushAsync(PushCreate("dev-a", Guid.NewGuid().ToString("N"), 10, "V2"), ActorA(), "v2");

        var seqA1 = a1.Results[0].ChangeSequence!.Value;
        var seqA2 = a2.Results[0].ChangeSequence!.Value;

        var page = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = afterCatalogs,
            PageSize = 1
        }, ActorA(), "dense-1");

        Assert.Single(page.Changes);
        Assert.Equal(seqA1, page.Changes[0].Sequence);
        Assert.Equal(seqA1, page.NextCursor);
        Assert.True(page.HasMore);

        var page2 = await sync.PullAsync(new SyncPullRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-a",
            Cursor = page.NextCursor,
            PageSize = 1
        }, ActorA(), "dense-2");

        Assert.Single(page2.Changes);
        Assert.Equal(seqA2, page2.Changes[0].Sequence);
        Assert.Equal(seqA2, page2.NextCursor);
    }

    [Fact]
    public async Task Different_Users_Same_ClientOperationId_Are_Independent()
    {
        var sync = CreateSync();
        var sharedOp = Guid.NewGuid().ToString("N");
        var a = await sync.PushAsync(PushCreate("dev-a", sharedOp), ActorA(), "a");
        var b = await sync.PushAsync(PushCreate("dev-b", sharedOp), ActorB(), "b");

        Assert.Equal(nameof(SyncOperationResult.Accepted), a.Results[0].Result);
        Assert.Equal(nameof(SyncOperationResult.Accepted), b.Results[0].Result);
        Assert.NotEqual(a.Results[0].EntityId, b.Results[0].EntityId);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.NepRecords.CountAsync(r => r.ClientOperationId == sharedOp));
        Assert.Equal(2, await db.SyncChangeLogs.CountAsync(c => c.ClientOperationId == sharedOp));
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
