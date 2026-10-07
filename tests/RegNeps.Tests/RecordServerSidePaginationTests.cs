using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Common;
using RegNeps.Application.Records;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Tests;

/// <summary>FASE 2F — paginación server-side (auth → filtros → ORDER → OFFSET/FETCH).</summary>
public sealed class RecordServerSidePaginationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private readonly Guid _userA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly Guid _userB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private readonly DateTime _baseUtc = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public RecordServerSidePaginationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public async Task InitializeAsync()
    {
        await using var db = new RegNepsDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DbSeeder.SeedAsync(db);

        db.Users.AddRange(
            new AppUser
            {
                Id = _userA,
                Username = "page_a",
                DisplayName = "A",
                PasswordHash = "x",
                Role = AppUserRole.Operario,
                RoleCode = "Operario",
                IsActive = true
            },
            new AppUser
            {
                Id = _userB,
                Username = "page_b",
                DisplayName = "B",
                PasswordHash = "x",
                Role = AppUserRole.Operario,
                RoleCode = "Operario",
                IsActive = true
            });

        for (var i = 0; i < 120; i++)
        {
            db.NepRecords.Add(new NepRecord
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{i + 1:D12}"),
                Telar = $"T-{i:D3}",
                Neps = i % 10 == 0 ? 55 : 10,
                Tela = "Denim",
                LoteTrama = "L1",
                Turno = "A",
                Operario = "opA",
                CreatedAt = _baseUtc.AddSeconds(-(i / 3)),
                CreatedByUserId = _userA.ToString(),
                CreatedByRole = "Operario"
            });
        }

        for (var i = 0; i < 30; i++)
        {
            db.NepRecords.Add(new NepRecord
            {
                Id = Guid.Parse($"11111111-1111-1111-1111-{i + 1:D12}"),
                Telar = $"BX-{i:D3}",
                Neps = 12,
                CreatedAt = _baseUtc.AddMinutes(i),
                CreatedByUserId = _userB.ToString(),
                CreatedByRole = "Operario"
            });
        }

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private NepRecordRepository CreateRepo() => new(new TestDbFactory(_options));

    private NepRecordService CreateService() =>
        new(CreateRepo(), new AlertConfigRepository(new RegNepsDbContext(_options)));

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }

    private RecordActor ActorA(bool seesAll = false) =>
        RecordActor.Create(
            _userA.ToString(),
            "page_a",
            "A",
            AppUserRole.Operario,
            isSuperAdmin: false,
            roleCode: "Operario",
            seesAllRecords: seesAll);

    private RecordActor ActorB() =>
        RecordActor.Create(
            _userB.ToString(),
            "page_b",
            "B",
            AppUserRole.Operario,
            isSuperAdmin: false,
            roleCode: "Operario",
            seesAllRecords: false);

    [Fact]
    public async Task Page1_Returns_Only_PageSize_Items()
    {
        var page = await CreateRepo().QueryPagedAsync(
            new RecordFilters(), _userA.ToString(), viewerSeesAll: false, pageNumber: 1, pageSize: 50);

        Assert.Equal(120, page.TotalCount);
        Assert.Equal(50, page.Items.Count);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal(3, page.TotalPages);
        Assert.All(page.Items, r => Assert.Equal(_userA.ToString(), r.CreatedByUserId));
    }

    [Fact]
    public async Task Page2_Does_Not_Overlap_Page1()
    {
        var repo = CreateRepo();
        var p1 = await repo.QueryPagedAsync(new RecordFilters(), _userA.ToString(), false, 1, 50);
        var p2 = await repo.QueryPagedAsync(new RecordFilters(), _userA.ToString(), false, 2, 50);

        Assert.Equal(50, p2.Items.Count);
        var ids1 = p1.Items.Select(r => r.Id).ToHashSet();
        Assert.DoesNotContain(p2.Items, r => ids1.Contains(r.Id));
    }

    [Fact]
    public async Task Last_Page_Has_Remainder()
    {
        var page = await CreateRepo().QueryPagedAsync(
            new RecordFilters(), _userA.ToString(), false, 3, 50);

        Assert.Equal(20, page.Items.Count);
        Assert.Equal(3, page.PageNumber);
    }

    [Fact]
    public async Task Out_Of_Range_Clamps_To_Last_Page()
    {
        var page = await CreateRepo().QueryPagedAsync(
            new RecordFilters(), _userA.ToString(), false, 999, 50);

        Assert.Equal(3, page.PageNumber);
        Assert.Equal(20, page.Items.Count);
    }

    [Fact]
    public async Task PageSize_Is_Clamped_To_Max()
    {
        var page = await CreateRepo().QueryPagedAsync(
            new RecordFilters(), _userA.ToString(), false, 1, pageSize: 500_000);

        Assert.Equal(RecordPaging.MaxPageSize, page.PageSize);
        Assert.True(page.Items.Count <= RecordPaging.MaxPageSize);
    }

    [Fact]
    public async Task PageSize_Minimum_Is_One()
    {
        var page = await CreateRepo().QueryPagedAsync(
            new RecordFilters(), _userA.ToString(), false, 1, pageSize: 0);

        Assert.Equal(RecordPaging.DefaultPageSize, page.PageSize);
    }

    [Fact]
    public async Task Filters_Apply_Before_Paging()
    {
        var page = await CreateRepo().QueryPagedAsync(
            new RecordFilters { AlertLevel = AlertLevel.SecondQuality },
            _userA.ToString(),
            false,
            1,
            50);

        // Cada 10º registro tiene Neps=55 → 12 críticos SecondQuality en 120.
        Assert.Equal(12, page.TotalCount);
        Assert.Equal(12, page.Items.Count);
        Assert.All(page.Items, r =>
            Assert.True(r.Neps >= NepsQualityCriteria.CriticalAdjustmentNepsExclusiveUpper));
    }

    [Fact]
    public async Task Ownership_Hides_Foreign_Records_On_Every_Page()
    {
        var repo = CreateRepo();
        for (var p = 1; p <= 3; p++)
        {
            var page = await repo.QueryPagedAsync(new RecordFilters(), _userA.ToString(), false, p, 50);
            Assert.DoesNotContain(page.Items, r => r.CreatedByUserId == _userB.ToString());
            Assert.DoesNotContain(page.Items, r => r.Telar.StartsWith("BX-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task SeesAll_Includes_Both_Users()
    {
        var page = await CreateRepo().QueryPagedAsync(
            new RecordFilters(), _userA.ToString(), viewerSeesAll: true, 1, 100);

        Assert.Equal(150, page.TotalCount);
        Assert.Contains(page.Items, r => r.CreatedByUserId == _userB.ToString());
    }

    [Fact]
    public async Task FailClosed_Without_User_Returns_Empty()
    {
        var page = await CreateRepo().QueryPagedAsync(
            new RecordFilters(), viewerUserId: null, viewerSeesAll: false, 1, 50);

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Deterministic_Order_With_Same_CreatedAt()
    {
        var repo = CreateRepo();
        var p1 = await repo.QueryPagedAsync(new RecordFilters(), _userA.ToString(), false, 1, 10);
        var p1b = await repo.QueryPagedAsync(new RecordFilters(), _userA.ToString(), false, 1, 10);

        Assert.Equal(p1.Items.Select(r => r.Id), p1b.Items.Select(r => r.Id));

        // Dentro de empates de CreatedAt, Id DESC.
        for (var i = 0; i < p1.Items.Count - 1; i++)
        {
            var a = p1.Items[i];
            var b = p1.Items[i + 1];
            Assert.True(
                a.CreatedAt > b.CreatedAt
                || (a.CreatedAt == b.CreatedAt && a.Id.CompareTo(b.Id) > 0));
        }
    }

    [Fact]
    public async Task Service_QueryPaged_Respects_Permissions_Matrix_Via_Actor()
    {
        var svc = CreateService();
        var page = await svc.QueryPagedAsync(new RecordFilters(), ActorA(), 1, 50);
        Assert.Equal(120, page.TotalCount);
        Assert.Equal(50, page.Items.Count);
    }

    [Fact]
    public async Task After_Delete_Total_And_Page_Reflect_State()
    {
        var svc = CreateService();
        var admin = RecordActor.Create(
            _userA.ToString(), "admin", "Admin", AppUserRole.Admin, isSuperAdmin: false,
            seesAllRecords: true);
        var before = await svc.QueryPagedAsync(new RecordFilters(), ActorA(), 1, 50);
        var victim = before.Items[0];
        await svc.DeleteAsync(victim.Id, admin);

        var after = await svc.QueryPagedAsync(new RecordFilters(), ActorA(), 1, 50);
        Assert.Equal(119, after.TotalCount);
        Assert.DoesNotContain(after.Items, r => r.Id == victim.Id);
    }

    [Fact]
    public async Task After_ClearAll_Empty_Pages()
    {
        var svc = CreateService();
        var admin = RecordActor.Create(
            _userA.ToString(), "admin", "Admin", AppUserRole.Admin, isSuperAdmin: false,
            seesAllRecords: true);
        await svc.ClearAllAsync(admin);

        var page = await svc.QueryPagedAsync(new RecordFilters(), admin, 1, 50);
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
        Assert.Equal(1, page.TotalPages);
    }

    [Fact]
    public async Task Large_Volume_Materializes_Only_Page()
    {
        // Demuestra diseño: Items.Count == PageSize aunque TotalCount >> PageSize.
        var page = await CreateRepo().QueryPagedAsync(
            new RecordFilters(), _userA.ToString(), false, 1, 25);

        Assert.Equal(120, page.TotalCount);
        Assert.Equal(25, page.Items.Count);
        Assert.True(page.TotalCount > page.Items.Count);
    }

    [Fact]
    public async Task CountFiltered_Matches_Paged_Total()
    {
        var repo = CreateRepo();
        var filters = new RecordFilters { Telar = "T-000" };
        var count = await repo.CountFilteredAsync(filters, _userA.ToString(), false);
        var page = await repo.QueryPagedAsync(filters, _userA.ToString(), false, 1, 50);
        Assert.Equal(count, page.TotalCount);
        Assert.Equal(1, count);
    }

    [Fact]
    public void NormalizePageSize_Rejects_Huge_Values()
    {
        Assert.Equal(RecordPaging.MaxPageSize, RecordPaging.NormalizePageSize(500_000));
        Assert.Equal(RecordPaging.DefaultPageSize, RecordPaging.NormalizePageSize(0));
        Assert.Equal(1, RecordPaging.NormalizePageSize(1));
    }
}
