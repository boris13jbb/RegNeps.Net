using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Records;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Domain.Permissions;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Tests;

public class CaptureIsolationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;

    public CaptureIsolationTests()
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
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private NepRecordService CreateService() =>
        new(new NepRecordRepository(_factory), new AlertConfigRepository(_factory.CreateDbContext()));

    private static RecordActor Actor(string id, string name, AppUserRole role, bool super = false) =>
        RecordActor.Create(id, name, name, role, super);

    [Fact]
    public async Task Two_Users_Same_Telar_Lote_Produce_Two_Rows()
    {
        var service = CreateService();
        var a = Actor(Guid.NewGuid().ToString(), "opA", AppUserRole.Operario);
        var b = Actor(Guid.NewGuid().ToString(), "opB", AppUserRole.Operario);

        var r1 = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "102",
            Neps = 10,
            Tela = "Denim",
            LoteTrama = "63E264H10A",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, a);

        var r2 = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "102",
            Neps = 10,
            Tela = "Denim",
            LoteTrama = "63E264H10A",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, b);

        Assert.NotEqual(r1.Id, r2.Id);
        Assert.Equal(a.UserId, r1.CreatedByUserId);
        Assert.Equal(b.UserId, r2.CreatedByUserId);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.NepRecords.CountAsync());
    }

    [Fact]
    public async Task Personal_Query_Does_Not_Return_Other_Users()
    {
        var service = CreateService();
        var a = Actor(Guid.NewGuid().ToString(), "opA", AppUserRole.Operario);
        var b = Actor(Guid.NewGuid().ToString(), "opB", AppUserRole.Operario);

        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "1", Neps = 5, Tela = "X", LoteTrama = "L1",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, a);
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "2", Neps = 5, Tela = "X", LoteTrama = "L2",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, b);

        var mine = await service.QueryAsync(new RecordFilters(), a, RecordQueryScope.PersonalOnly);
        Assert.Single(mine);
        Assert.Equal(a.UserId, mine[0].CreatedByUserId);
    }

    [Fact]
    public async Task Empty_UserId_Personal_Query_Is_Empty_Not_Global()
    {
        var service = CreateService();
        var admin = Actor(Guid.NewGuid().ToString(), "admin", AppUserRole.Admin);
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "9", Neps = 5, Tela = "X", LoteTrama = "L9",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, admin);

        var repo = new NepRecordRepository(_factory);
        var leaked = await repo.QueryAsync(new RecordFilters(), viewerUserId: "", viewerSeesAll: false, take: 100);
        Assert.Empty(leaked);

        var leakedNull = await repo.QueryAsync(new RecordFilters(), viewerUserId: null, viewerSeesAll: false, take: 100);
        Assert.Empty(leakedNull);
    }

    [Fact]
    public async Task Operario_Cannot_Delete_Foreign_Record()
    {
        var service = CreateService();
        var a = Actor(Guid.NewGuid().ToString(), "opA", AppUserRole.Operario);
        var b = Actor(Guid.NewGuid().ToString(), "opB", AppUserRole.Operario);
        var saved = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "3", Neps = 5, Tela = "X", LoteTrama = "L3",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, a);

        await Assert.ThrowsAsync<UnauthorizedRecordAccessException>(() =>
            service.DeleteAsync(saved.Id, b));
    }

    [Fact]
    public async Task Operario_Cannot_Edit_Foreign_Record()
    {
        var service = CreateService();
        var a = Actor(Guid.NewGuid().ToString(), "opA", AppUserRole.Operario);
        var b = Actor(Guid.NewGuid().ToString(), "opB", AppUserRole.Operario);
        var saved = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "4", Neps = 5, Tela = "X", LoteTrama = "L4",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, a);

        await Assert.ThrowsAsync<UnauthorizedRecordAccessException>(() =>
            service.UpdateAsync(new UpdateNepRecordRequest
            {
                Id = saved.Id,
                Telar = "4",
                Neps = 8,
                Tela = "X",
                LoteTrama = "L4",
                ExpectedConcurrencyStamp = saved.ConcurrencyStamp
            }, b));
    }

    [Fact]
    public async Task Create_Requires_Authenticated_Actor()
    {
        await Assert.ThrowsAsync<UnauthorizedRecordAccessException>(() =>
            Task.FromResult(RecordActor.Create(null, "x", "x", AppUserRole.Operario, false)));
    }

    [Fact]
    public async Task Create_Requires_CapturePermission()
    {
        var service = CreateService();
        var gerencia = Actor(Guid.NewGuid().ToString(), "g", AppUserRole.Gerencia);
        await Assert.ThrowsAsync<UnauthorizedRecordAccessException>(() =>
            service.CreateAsync(new CreateNepRecordRequest
            {
                Telar = "1", Neps = 5, Tela = "X", LoteTrama = "L"
            }, gerencia));
    }

    [Fact]
    public async Task Idempotent_ClientOperation_Does_Not_Duplicate()
    {
        var service = CreateService();
        var a = Actor(Guid.NewGuid().ToString(), "opA", AppUserRole.Operario);
        var opId = Guid.NewGuid().ToString("N");
        var req = new CreateNepRecordRequest
        {
            Telar = "55",
            Neps = 12,
            Tela = "Denim",
            LoteTrama = "LOTE55",
            ClientOperationId = opId
        };

        var first = await service.CreateWithOutcomeAsync(req, a);
        var second = await service.CreateWithOutcomeAsync(req, a);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(RecordSaveStatus.AlreadySaved, second.Status);
        Assert.Equal(first.Record!.Id, second.Record!.Id);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync(r => r.ClientOperationId == opId));
    }

    [Fact]
    public async Task Concurrent_Edit_Detects_Stamp_Conflict()
    {
        var service = CreateService();
        var admin = Actor(Guid.NewGuid().ToString(), "adm", AppUserRole.Admin);
        var saved = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "70", Neps = 5, Tela = "X", LoteTrama = "L70",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, admin);

        var stale = saved.ConcurrencyStamp;
        await service.UpdateAsync(new UpdateNepRecordRequest
        {
            Id = saved.Id,
            Telar = "70",
            Neps = 6,
            Tela = "X",
            LoteTrama = "L70",
            ExpectedConcurrencyStamp = stale
        }, admin);

        await Assert.ThrowsAsync<RecordConcurrencyConflictException>(() =>
            service.UpdateAsync(new UpdateNepRecordRequest
            {
                Id = saved.Id,
                Telar = "70",
                Neps = 7,
                Tela = "X",
                LoteTrama = "L70",
                ExpectedConcurrencyStamp = stale
            }, admin));
    }

    [Fact]
    public async Task Admin_Sees_All_In_Default_Scope_But_PersonalOnly_Is_Own()
    {
        var service = CreateService();
        var admin = Actor(Guid.NewGuid().ToString(), "adm", AppUserRole.Admin);
        var op = Actor(Guid.NewGuid().ToString(), "op", AppUserRole.Operario);

        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "A1", Neps = 5, Tela = "X", LoteTrama = "LA",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, admin);
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "O1", Neps = 5, Tela = "X", LoteTrama = "LO",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, op);

        var all = await service.QueryAsync(new RecordFilters(), admin, RecordQueryScope.Default);
        Assert.True(all.Count >= 2);

        var personal = await service.QueryAsync(new RecordFilters(), admin, RecordQueryScope.PersonalOnly);
        Assert.All(personal, r => Assert.Equal(admin.UserId, r.CreatedByUserId));
    }

    [Fact]
    public async Task Historical_Null_ClientOperationId_Compatible_With_Unique_Index()
    {
        await using var db = _factory.CreateDbContext();
        var userId = Guid.NewGuid().ToString();
        db.NepRecords.AddRange(
            new NepRecord
            {
                Id = Guid.NewGuid(),
                Telar = "H1",
                Neps = 1,
                Tela = "X",
                LoteTrama = "LH1",
                CreatedAt = DateTime.UtcNow,
                CreatedByUserId = userId,
                ClientOperationId = null,
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            },
            new NepRecord
            {
                Id = Guid.NewGuid(),
                Telar = "H2",
                Neps = 2,
                Tela = "X",
                LoteTrama = "LH2",
                CreatedAt = DateTime.UtcNow,
                CreatedByUserId = userId,
                ClientOperationId = null,
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.NepRecords.CountAsync(r =>
            r.CreatedByUserId == userId && r.ClientOperationId == null));
    }

    [Fact]
    public async Task Schema_Patches_Are_Idempotent()
    {
        await using var db = _factory.CreateDbContext();
        await DatabaseInitializer.ApplySchemaPatchesAsync(db);
        await DatabaseInitializer.ApplySchemaPatchesAsync(db);
    }

    [Fact]
    public async Task Update_Changes_ConcurrencyStamp()
    {
        var service = CreateService();
        var admin = Actor(Guid.NewGuid().ToString(), "adm", AppUserRole.Admin);
        var saved = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "71", Neps = 5, Tela = "X", LoteTrama = "L71",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, admin);

        var before = saved.ConcurrencyStamp;
        var updated = await service.UpdateAsync(new UpdateNepRecordRequest
        {
            Id = saved.Id,
            Telar = "71",
            Neps = 9,
            Tela = "X",
            LoteTrama = "L71",
            ExpectedConcurrencyStamp = before
        }, admin);

        Assert.False(string.IsNullOrWhiteSpace(updated.ConcurrencyStamp));
        Assert.NotEqual(before, updated.ConcurrencyStamp);
    }

    [Fact]
    public async Task Capture_Session_Filter_Ignores_Historical_And_Other_Sessions()
    {
        var service = CreateService();
        var user = Actor(Guid.NewGuid().ToString(), "opS", AppUserRole.Operario);
        var sessionA = Guid.NewGuid().ToString("N");
        var sessionB = Guid.NewGuid().ToString("N");

        // Histórico sin CaptureSessionId (no debe aparecer en sesión A).
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "H1", Neps = 9, Tela = "X", LoteTrama = "LH1",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, user);

        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "A1", Neps = 5, Tela = "X", LoteTrama = "LA1",
            ClientOperationId = Guid.NewGuid().ToString("N"),
            CaptureSessionId = sessionA
        }, user);
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "A2", Neps = 7, Tela = "X", LoteTrama = "LA2",
            ClientOperationId = Guid.NewGuid().ToString("N"),
            CaptureSessionId = sessionA
        }, user);
        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "B1", Neps = 3, Tela = "X", LoteTrama = "LB1",
            ClientOperationId = Guid.NewGuid().ToString("N"),
            CaptureSessionId = sessionB
        }, user);

        var inA = await service.QueryAsync(
            new RecordFilters { CaptureSessionId = sessionA },
            user,
            RecordQueryScope.PersonalOnly);
        Assert.Equal(2, inA.Count);
        Assert.All(inA, r => Assert.Equal(sessionA, r.CaptureSessionId));

        var inB = await service.QueryAsync(
            new RecordFilters { CaptureSessionId = sessionB },
            user,
            RecordQueryScope.PersonalOnly);
        Assert.Single(inB);

        var emptyNew = await service.QueryAsync(
            new RecordFilters { CaptureSessionId = Guid.NewGuid().ToString("N") },
            user,
            RecordQueryScope.PersonalOnly);
        Assert.Empty(emptyNew);

        // Históricos siguen consultables sin filtro de sesión.
        var allMine = await service.QueryAsync(new RecordFilters(), user, RecordQueryScope.PersonalOnly);
        Assert.True(allMine.Count >= 4);
    }

    [Fact]
    public async Task Capture_Sessions_Do_Not_Leak_Across_Users()
    {
        var service = CreateService();
        var a = Actor(Guid.NewGuid().ToString(), "opA", AppUserRole.Operario);
        var b = Actor(Guid.NewGuid().ToString(), "opB", AppUserRole.Operario);
        var sessionId = Guid.NewGuid().ToString("N");

        await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "99", Neps = 4, Tela = "X", LoteTrama = "L99",
            ClientOperationId = Guid.NewGuid().ToString("N"),
            CaptureSessionId = sessionId
        }, a);

        var leaked = await service.QueryAsync(
            new RecordFilters { CaptureSessionId = sessionId },
            b,
            RecordQueryScope.PersonalOnly);
        Assert.Empty(leaked);
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
