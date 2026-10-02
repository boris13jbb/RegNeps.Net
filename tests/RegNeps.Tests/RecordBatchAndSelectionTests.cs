using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Records;
using RegNeps.Application.Reports;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Infrastructure.Export;
using RegNeps.Infrastructure.Import;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Tests;

public sealed class RecordBatchAndSelectionTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;

    public RecordBatchAndSelectionTests()
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

    private NepRecordService CreateRecordService() =>
        new(new NepRecordRepository(_factory), new AlertConfigRepository(_factory.CreateDbContext()));

    private ReportExportAppService CreateExportService() =>
        new(
            new NepRecordRepository(_factory),
            new AlertConfigRepository(_factory.CreateDbContext()),
            new ExportFileService(),
            new SavedReportRepository(_factory.CreateDbContext()),
            new NoopSnapshotService());

    private RecordImportService CreateImportService() =>
        new(new NepRecordRepository(_factory));

    private static RecordActor Actor(string id, AppUserRole role = AppUserRole.Admin) =>
        RecordActor.Create(id, "user", "user", role, isSuperAdmin: role == AppUserRole.SuperAdmin);

    private static CreateNepRecordRequest Req(string telar, double neps = 5) => new()
    {
        Telar = telar,
        Neps = neps,
        Tela = "Denim",
        LoteTrama = "L-1",
        Turno = "A",
        Operario = "Op",
        ClientOperationId = Guid.NewGuid().ToString("N"),
        CaptureSessionId = Guid.NewGuid().ToString("N")
    };

    [Fact]
    public void Selection_Clears_On_Page_Or_Filter_Change()
    {
        var set = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        RecordSelectionRules.ClearOnPageOrFilterChange(set);
        Assert.Empty(set);
    }

    [Fact]
    public void Selection_Prunes_To_Visible_Page()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var set = new HashSet<Guid> { a, b, c };
        RecordSelectionRules.PruneToVisible(set, [a, b]);
        Assert.Equal(2, set.Count);
        Assert.Contains(a, set);
        Assert.Contains(b, set);
        Assert.DoesNotContain(c, set);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void CanEditSelection_Only_When_Exactly_One(int count, bool expected) =>
        Assert.Equal(expected, RecordSelectionRules.CanEditSelection(count));

    [Fact]
    public async Task DeleteMany_Reports_Missing_And_Removes_Deleted()
    {
        var service = CreateRecordService();
        var actor = Actor(Guid.NewGuid().ToString());
        var a = await service.CreateAsync(Req("T1"), actor);
        var b = await service.CreateAsync(Req("T2"), actor);
        var missing = Guid.NewGuid();

        var result = await service.DeleteManyAsync([a.Id, missing, b.Id], actor);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(1, result.NotFoundOrFailed);
        Assert.Contains(missing, result.FailedIds);
        Assert.Contains("2 eliminados, 1 no encontrados/fallidos", result.SummaryText);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(a.Id, actor));
    }

    [Fact]
    public async Task SaveReportByIds_Contains_Exactly_Selected()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var actor = Actor(Guid.NewGuid().ToString());
        var a = await records.CreateAsync(Req("A1"), actor);
        var b = await records.CreateAsync(Req("B1"), actor);
        _ = await records.CreateAsync(Req("C1"), actor);

        var report = await export.SaveReportByIdsAsync(
            "Informe test",
            [a.Id, b.Id],
            actor.UserId,
            actor.DisplayName,
            viewerSeesAll: true,
            hasManageReports: true);

        Assert.Equal(2, report.RecordCount);
        Assert.Contains(a.Id.ToString("N"), report.SnapshotJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(b.Id.ToString("N"), report.SnapshotJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C1", report.SnapshotJson!);
    }

    [Fact]
    public async Task ImportCsv_Valid_And_Invalid_Rows()
    {
        var import = CreateImportService();
        var csv = """
                  TELAR,NEPS,TELA,LOTE
                  T10,12.5,Denim,L1
                  ,9,Denim,L1
                  T11,abc,Denim,L1
                  T12,8,Denim,L2
                  """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var result = await import.ImportCsvAsync(stream, "u1", "u1@x.com", "Operario");

        Assert.Equal(2, result.Imported);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Rows, r => r.Success && r.RowNumber == 2);
        Assert.Contains(result.Rows, r => !r.Success && r.Reason!.Contains("TELAR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportCsv_Semicolon_And_Bom()
    {
        var import = CreateImportService();
        var csv = "\uFEFFTELAR;NEPS;TELA\nTX;3;Denim\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var result = await import.ImportCsvAsync(stream, "u1", "u1@x.com", "Operario");
        Assert.Equal(1, result.Imported);
    }

    private sealed class NoopSnapshotService : IReportSnapshotService
    {
        public Task<IReadOnlyList<NepRecord>> LoadSnapshotRecordsAsync(Guid reportId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NepRecord>>(Array.Empty<NepRecord>());
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
