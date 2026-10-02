using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Records;
using RegNeps.Application.Reports;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Domain.Services;
using RegNeps.Infrastructure.Export;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Tests;

public sealed class ExportSavedStyleAndColumnsTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;

    public ExportSavedStyleAndColumnsTests()
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

    private static RecordActor Actor(string id, string name) =>
        RecordActor.Create(id, name, name, AppUserRole.Operario, isSuperAdmin: false);

    private static CreateNepRecordRequest Req(string telar) => new()
    {
        Telar = telar,
        Neps = 12,
        Tela = "Denim",
        LoteTrama = "L-" + telar,
        Turno = "A",
        Operario = "Op",
        ClientOperationId = Guid.NewGuid().ToString("N"),
        CaptureSessionId = Guid.NewGuid().ToString("N")
    };

    [Fact]
    public void SavedReportViewMode_IsReadOnly_When_ReportId_Present()
    {
        Assert.False(SavedReportViewMode.IsReadOnlyView(null));
        Assert.True(SavedReportViewMode.IsReadOnlyView(Guid.NewGuid()));
    }

    [Fact]
    public void ParseQuerySelection_Splits_Comma_And_Repeated_Values()
    {
        var parsed = ReportColumnIds.ParseQuerySelection(["telar,neps", "tela"]);
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Count);
        Assert.Equal(ReportColumnIds.Telar, parsed[0]);
        Assert.Equal(ReportColumnIds.Neps, parsed[1]);
        Assert.Equal(ReportColumnIds.Tela, parsed[2]);
    }

    [Fact]
    public async Task ExportSavedAsync_ClasicoStyle_Uses_Clasico_FileSuffix()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var user = Actor(Guid.NewGuid().ToString(), "savedStyle");

        await records.CreateAsync(Req("STY-A"), user);
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddDays(1);
        var filters = new RecordFilters();
        ReportDateRange.FromLocalCalendarDates(from.ToLocalTime().Date, to.ToLocalTime().Date).ApplyTo(filters);

        var saved = await export.SaveReportAsync(
            "Informe estilo test", filters, user.UserId, user.DisplayName, viewerSeesAll: true,
            hasManageReports: true);

        var savedFile = await export.ExportSavedAsync(
            saved.Id, "csv", user.UserId, viewerSeesAll: true, style: "clasico",
            hasManageReports: true, hasExportReports: true);

        var savedCsv = Encoding.UTF8.GetString(savedFile.Bytes);
        Assert.Contains("Formula utilizada", savedCsv, StringComparison.Ordinal);
        Assert.Equal("text/csv", savedFile.ContentType);
        Assert.True(savedFile.Bytes.Length > 0);

        var liveExport = await export.ExportAsync(
            "csv", filters, user.UserId, viewerSeesAll: true, style: "clasico");
        Assert.Contains("_clasico", liveExport.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_ThreeColumns_Csv_Header_Has_Only_Those()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var user = Actor(Guid.NewGuid().ToString(), "colsCsv");

        await records.CreateAsync(Req("COL-A"), user);
        var filters = new RecordFilters();
        ReportDateRange.FromLocalCalendarDates(DateTime.Today.AddDays(-7), DateTime.Today).ApplyTo(filters);

        var columns = new[] { ReportColumnIds.Telar, ReportColumnIds.Neps, ReportColumnIds.Tela };
        var file = await export.ExportAsync(
            "csv", filters, user.UserId, viewerSeesAll: true, "completo", columns);

        var text = Encoding.UTF8.GetString(file.Bytes).TrimStart('\uFEFF');
        var header = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        Assert.Equal("Tela,Telar,Neps", header);
        Assert.DoesNotContain("Observación", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Lote de trama", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportFileService_ThreeColumns_Excel_And_Pdf_Produce_Bytes()
    {
        var records = CreateRecordService();
        var user = Actor(Guid.NewGuid().ToString(), "colsBin");
        var created = await records.CreateAsync(Req("COL-X"), user);
        var config = await new AlertConfigRepository(_factory.CreateDbContext()).GetAsync();
        var exportFile = new ExportFileService();
        var columns = new[] { ReportColumnIds.Telar, ReportColumnIds.Neps, ReportColumnIds.Tela };

        var xlsx = exportFile.BuildExcel([created], config, columns: columns);
        var pdf = exportFile.BuildPdf([created], config, columns: columns);

        Assert.True(xlsx.Length > 4);
        Assert.Equal((byte)'P', xlsx[0]);
        Assert.Equal((byte)'K', xlsx[1]);
        Assert.True(pdf.Length > 4);
        Assert.Equal(0x25, pdf[0]);
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
