using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Records;
using RegNeps.Domain.Enums;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Tests;

/// <summary>
/// Aislamiento de compartir: cada registro se resuelve por Id, nunca por sesión/fecha/lote.
/// </summary>
public class RecordShareIsolationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;

    public RecordShareIsolationTests()
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

    private static CreateNepRecordRequest Req(string telar, string? sessionId = null) => new()
    {
        Telar = telar,
        Neps = 5,
        Tela = "Denim",
        LoteTrama = "L-" + telar,
        Turno = "A",
        Operario = "Op",
        ClientOperationId = Guid.NewGuid().ToString("N"),
        CaptureSessionId = sessionId
    };

    [Fact]
    public async Task Share_Individual_Record_Returns_Only_Requested_Record()
    {
        var service = CreateService();
        var user = Actor(Guid.NewGuid().ToString(), "opShare", AppUserRole.Operario);
        var session = Guid.NewGuid().ToString("N");

        var a = await service.CreateAsync(Req("A", session), user);
        var b = await service.CreateAsync(Req("B", session), user);

        var shared = await service.GetAccessibleByIdsAsync([b.Id], user);

        Assert.Single(shared);
        Assert.Equal(b.Id, shared[0].Id);
        Assert.DoesNotContain(shared, r => r.Id == a.Id);
    }

    [Fact]
    public async Task Share_New_Record_Does_Not_Include_Previous_Record()
    {
        var service = CreateService();
        var user = Actor(Guid.NewGuid().ToString(), "opSeq", AppUserRole.Operario);
        var session = Guid.NewGuid().ToString("N");

        var a = await service.CreateAsync(Req("A1", session), user);
        var shareA = await service.GetAccessibleByIdsAsync([a.Id], user);
        Assert.Single(shareA);
        Assert.Equal(a.Id, shareA[0].Id);

        var b = await service.CreateAsync(Req("B1", session), user);
        var shareB = await service.GetAccessibleByIdsAsync([b.Id], user);

        Assert.Single(shareB);
        Assert.Equal(b.Id, shareB[0].Id);
        Assert.DoesNotContain(shareB, r => r.Id == a.Id);
    }

    [Fact]
    public async Task Share_Selected_Records_Returns_Exactly_Selected_Ids()
    {
        var service = CreateService();
        var user = Actor(Guid.NewGuid().ToString(), "opSel", AppUserRole.Operario);
        var session = Guid.NewGuid().ToString("N");

        var a = await service.CreateAsync(Req("SA", session), user);
        var b = await service.CreateAsync(Req("SB", session), user);
        var c = await service.CreateAsync(Req("SC", session), user);

        var shared = await service.GetAccessibleByIdsAsync([a.Id, c.Id], user);

        Assert.Equal(2, shared.Count);
        Assert.Contains(shared, r => r.Id == a.Id);
        Assert.Contains(shared, r => r.Id == c.Id);
        Assert.DoesNotContain(shared, r => r.Id == b.Id);
    }

    [Fact]
    public async Task Share_Selection_Does_Not_Leak_Other_User_Records()
    {
        var service = CreateService();
        var userA = Actor(Guid.NewGuid().ToString(), "opA", AppUserRole.Operario);
        var userB = Actor(Guid.NewGuid().ToString(), "opB", AppUserRole.Operario);
        var session = Guid.NewGuid().ToString("N");

        var a1 = await service.CreateAsync(Req("A1", session), userA);
        var b1 = await service.CreateAsync(Req("B1", session), userB);

        var shared = await service.GetAccessibleByIdsAsync([a1.Id, b1.Id], userA);

        Assert.Single(shared);
        Assert.Equal(a1.Id, shared[0].Id);
        Assert.DoesNotContain(shared, r => r.Id == b1.Id);
    }

    [Fact]
    public async Task CaptureSession_Does_Not_Implicitly_Define_Share_Group()
    {
        var service = CreateService();
        var user = Actor(Guid.NewGuid().ToString(), "opSess", AppUserRole.Operario);
        var session = Guid.NewGuid().ToString("N");

        await service.CreateAsync(Req("X", session), user);
        var b = await service.CreateAsync(Req("Y", session), user);

        var shared = await service.GetAccessibleByIdsAsync([b.Id], user);

        Assert.Single(shared);
        Assert.Equal(b.Id, shared[0].Id);
    }

    [Fact]
    public async Task BuildShareText_Includes_Only_Requested_Record_Content()
    {
        var service = CreateService();
        var user = Actor(Guid.NewGuid().ToString(), "opTxt", AppUserRole.Operario);
        var session = Guid.NewGuid().ToString("N");

        var a = await service.CreateAsync(Req("TA", session), user);
        var b = await service.CreateAsync(Req("TB", session), user);

        var text = await service.BuildShareTextForIdsAsync([b.Id], user);

        Assert.Contains("Telar: TB", text);
        Assert.DoesNotContain("Telar: TA", text);
        Assert.Contains(b.LoteTrama, text);
        Assert.DoesNotContain(a.LoteTrama, text);
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
