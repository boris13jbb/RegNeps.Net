using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Tests;

public class RecordFilterTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;

    public RecordFilterTests()
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

        var now = DateTime.UtcNow;
        // Muchos normales antiguos + críticos recientes: el Take antiguo fallaba al filtrar alerta en memoria.
        for (var i = 0; i < 40; i++)
        {
            db.NepRecords.Add(new NepRecord
            {
                Telar = "T-NORM",
                Neps = 10,
                Tela = "Denim",
                LoteTrama = "63E26401",
                Turno = "A",
                Operario = "op1",
                CreatedAt = now.AddDays(-10).AddMinutes(i)
            });
        }

        db.NepRecords.Add(new NepRecord
        {
            Telar = "t-crit",
            Neps = 75,
            Tela = "DENIM",
            LoteTrama = "63E26401",
            Turno = "b",
            Operario = "OP2",
            CreatedAt = now.AddMinutes(-5),
            RevisadoPorSupervisor = false
        });
        db.NepRecords.Add(new NepRecord
        {
            Telar = "T-WARN",
            Neps = 45,
            Tela = "denim",
            LoteTrama = "63E26402",
            Turno = "A",
            Operario = "op3",
            CreatedAt = now.AddMinutes(-2),
            RevisadoPorSupervisor = false
        });
        db.NepRecords.Add(new NepRecord
        {
            Telar = "T-PEND-DONE",
            Neps = 80,
            Tela = "Otra",
            Turno = "C",
            CreatedAt = now.AddMinutes(-1),
            RevisadoPorSupervisor = true
        });

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private NepRecordRepository CreateRepo() => new(new TestDbFactory(_options));

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }

    [Fact]
    public async Task AlertLevel_Critico_Is_Applied_In_Sql_Before_Take()
    {
        var repo = CreateRepo();
        var result = await repo.QueryAsync(
            new RecordFilters { AlertLevel = AlertLevel.Critico },
            viewerUserId: null,
            viewerSeesAll: true,
            take: 10);

        Assert.NotEmpty(result);
        Assert.All(result, r => Assert.True(r.Neps >= 60.5));
        Assert.Contains(result, r => r.Telar.Equals("t-crit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Telar_Filter_Is_Case_Insensitive()
    {
        var repo = CreateRepo();
        var result = await repo.QueryAsync(
            new RecordFilters { Telar = "T-CRIT" },
            null,
            true,
            50);

        Assert.Single(result);
        Assert.Equal(75, result[0].Neps);
    }

    [Fact]
    public async Task Tela_And_Turno_Are_Case_Insensitive()
    {
        var repo = CreateRepo();
        var result = await repo.QueryAsync(
            new RecordFilters { Tela = "denim", Turno = "B" },
            null,
            true,
            50);

        Assert.Single(result);
        Assert.Equal("t-crit", result[0].Telar, ignoreCase: true);
    }

    [Fact]
    public async Task SoloPendientes_Excludes_Reviewed_And_Normal()
    {
        var repo = CreateRepo();
        var result = await repo.QueryAsync(
            new RecordFilters { SoloPendientes = true },
            null,
            true,
            50);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, r => r.RevisadoPorSupervisor);
        Assert.DoesNotContain(result, r => r.Neps < 30.5);
    }

    [Fact]
    public async Task Date_Range_Includes_Full_Local_Day()
    {
        var repo = CreateRepo();
        var today = DateTime.Today;
        var filters = new RecordFilters();
        ReportDateRange.FromLocalCalendarDates(today, today).ApplyTo(filters);

        var result = await repo.QueryAsync(filters, null, true, 50_000);

        Assert.Contains(result, r => r.Telar.Equals("t-crit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, r => r.Telar.Equals("T-WARN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Boundary_Neps_30_Is_Normal_And_60_Is_Advertencia()
    {
        await using var db = new RegNepsDbContext(_options);
        db.NepRecords.AddRange(
            new NepRecord { Telar = "B30", Neps = 30, CreatedAt = DateTime.UtcNow },
            new NepRecord { Telar = "B60", Neps = 60, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var repo = CreateRepo();
        var normals = await repo.QueryAsync(
            new RecordFilters { AlertLevel = AlertLevel.Normal, Telar = "B30" }, null, true, 10);
        var warns = await repo.QueryAsync(
            new RecordFilters { AlertLevel = AlertLevel.Advertencia, Telar = "B60" }, null, true, 10);

        Assert.Single(normals);
        Assert.Single(warns);
    }
}
