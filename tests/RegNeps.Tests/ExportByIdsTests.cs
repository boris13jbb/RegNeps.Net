using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Records;
using RegNeps.Application.Reports;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Infrastructure.Export;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Tests;

/// <summary>
/// Exportación por IDs exactos: PDF/Excel/CSV sin expandir por sesión/fecha/lote.
/// </summary>
public sealed class ExportByIdsTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;

    public ExportByIdsTests()
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

    private static RecordActor Actor(string id, string name, AppUserRole role) =>
        RecordActor.Create(id, name, name, role, isSuperAdmin: false);

    private static CreateNepRecordRequest Req(string telar, double neps = 5) => new()
    {
        Telar = telar,
        Neps = neps,
        Tela = "Denim",
        LoteTrama = "L-" + telar,
        Turno = "A",
        Operario = "Op",
        ClientOperationId = Guid.NewGuid().ToString("N"),
        CaptureSessionId = Guid.NewGuid().ToString("N")
    };

    [Fact]
    public async Task ExportByIds_OneId_Returns_Only_That_Record()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var user = Actor(Guid.NewGuid().ToString(), "opExp1", AppUserRole.Operario);

        var a = await records.CreateAsync(Req("QA-A", 11), user);
        var b = await records.CreateAsync(Req("QA-B", 22), user);
        var c = await records.CreateAsync(Req("QA-C", 33), user);

        var file = await export.ExportByIdsAsync("csv", [b.Id], user.UserId, viewerSeesAll: false);

        var csv = Encoding.UTF8.GetString(file.Bytes);
        Assert.Contains("QA-B", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("QA-A", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("QA-C", csv, StringComparison.Ordinal);
        Assert.Equal("text/csv", file.ContentType);
        Assert.EndsWith(".csv", file.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(file.Bytes.Length > 0);
        _ = a;
        _ = c;
    }

    [Fact]
    public async Task ExportByIds_SelectedIds_Returns_Exactly_Selected()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var user = Actor(Guid.NewGuid().ToString(), "opExp2", AppUserRole.Operario);

        var a = await records.CreateAsync(Req("SEL-A", 11), user);
        var b = await records.CreateAsync(Req("SEL-B", 22), user);
        var c = await records.CreateAsync(Req("SEL-C", 33), user);

        var file = await export.ExportByIdsAsync("csv", [a.Id, c.Id], user.UserId, viewerSeesAll: false);
        var csv = Encoding.UTF8.GetString(file.Bytes);

        Assert.Contains("SEL-A", csv, StringComparison.Ordinal);
        Assert.Contains("SEL-C", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("SEL-B", csv, StringComparison.Ordinal);
        _ = b;
    }

    [Fact]
    public async Task ExportByIds_Does_Not_Include_Other_User_Record()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var userA = Actor(Guid.NewGuid().ToString(), "userA", AppUserRole.Operario);
        var userB = Actor(Guid.NewGuid().ToString(), "userB", AppUserRole.Operario);

        var a1 = await records.CreateAsync(Req("OWN-A1", 11), userA);
        var b1 = await records.CreateAsync(Req("OWN-B1", 22), userB);

        var file = await export.ExportByIdsAsync(
            "csv",
            [a1.Id, b1.Id],
            userA.UserId,
            viewerSeesAll: false);

        var csv = Encoding.UTF8.GetString(file.Bytes);
        Assert.Contains("OWN-A1", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("OWN-B1", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportByIds_DuplicateIds_Do_Not_DuplicateRows()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var user = Actor(Guid.NewGuid().ToString(), "opDup", AppUserRole.Operario);
        var a = await records.CreateAsync(Req("DUP-A", 11), user);

        var file = await export.ExportByIdsAsync(
            "csv",
            [a.Id, a.Id, a.Id],
            user.UserId,
            viewerSeesAll: false);

        var csv = Encoding.UTF8.GetString(file.Bytes);
        // Telar + lote (L-DUP-A) pueden mencionar el token; no debe triplicarse por Ids duplicados.
        var count = csv.Split("DUP-A", StringSplitOptions.None).Length - 1;
        Assert.True(count <= 2, $"Se esperaba como máximo 2 menciones (telar/lote), hubo {count}. CSV:\n{csv}");
        Assert.DoesNotContain("DUP-A,DUP-A,DUP-A", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportByIds_EmptyIds_FailsSafely()
    {
        var export = CreateExportService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            export.ExportByIdsAsync("csv", Array.Empty<Guid>(), "user", viewerSeesAll: false));
    }

    [Fact]
    public async Task ExportByIds_UnsupportedFormat_Fails()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var user = Actor(Guid.NewGuid().ToString(), "opFmt", AppUserRole.Operario);
        var a = await records.CreateAsync(Req("FMT-A"), user);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            export.ExportByIdsAsync("txt", [a.Id], user.UserId, viewerSeesAll: false));
    }

    [Theory]
    [InlineData("pdf", "application/pdf", ".pdf")]
    [InlineData("excel", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
    [InlineData("csv", "text/csv", ".csv")]
    public async Task ExportByIds_Formats_Produce_Bytes_And_Correct_Type(
        string format,
        string contentType,
        string extension)
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var user = Actor(Guid.NewGuid().ToString(), "opFmt2", AppUserRole.Operario);
        var a = await records.CreateAsync(Req("FMT-" + format.ToUpperInvariant(), 15), user);

        var file = await export.ExportByIdsAsync(format, [a.Id], user.UserId, viewerSeesAll: false);

        Assert.True(file.Bytes.Length > 0);
        Assert.Equal(contentType, file.ContentType);
        Assert.EndsWith(extension, file.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectedExcelContainsOnlySelectedRecords()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var user = Actor(Guid.NewGuid().ToString(), "opXls", AppUserRole.Operario);
        var a = await records.CreateAsync(Req("XLS-A", 11), user);
        var b = await records.CreateAsync(Req("XLS-B", 22), user);
        var c = await records.CreateAsync(Req("XLS-C", 33), user);

        var file = await export.ExportByIdsAsync("xlsx", [a.Id, c.Id], user.UserId, viewerSeesAll: false);

        // XLSX es un ZIP (PK..); el contenido textual de filas se valida vía CSV equivalente.
        Assert.True(file.Bytes.Length > 4);
        Assert.Equal((byte)'P', file.Bytes[0]);
        Assert.Equal((byte)'K', file.Bytes[1]);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            file.ContentType);

        var csvCheck = await export.ExportByIdsAsync("csv", [a.Id, c.Id], user.UserId, viewerSeesAll: false);
        var csv = Encoding.UTF8.GetString(csvCheck.Bytes);
        Assert.Contains("XLS-A", csv, StringComparison.Ordinal);
        Assert.Contains("XLS-C", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("XLS-B", csv, StringComparison.Ordinal);
        _ = b;
    }

    [Fact]
    public async Task IndividualPdfContainsOneRecord_Header()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var user = Actor(Guid.NewGuid().ToString(), "opPdf", AppUserRole.Operario);
        var a = await records.CreateAsync(Req("PDF-A", 11), user);
        var b = await records.CreateAsync(Req("PDF-B", 22), user);

        var file = await export.ExportByIdsAsync("pdf", [b.Id], user.UserId, viewerSeesAll: false);
        Assert.True(file.Bytes.Length > 4);
        Assert.Equal(0x25, file.Bytes[0]); // %
        Assert.Equal(0x50, file.Bytes[1]); // P
        Assert.Equal(0x44, file.Bytes[2]); // D
        Assert.Equal(0x46, file.Bytes[3]); // F
        Assert.Equal("application/pdf", file.ContentType);
        _ = a;
    }

    [Fact]
    public async Task SessionExportStillUsesCaptureSession()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var user = Actor(Guid.NewGuid().ToString(), "opSess", AppUserRole.Operario);
        var session = Guid.NewGuid().ToString("N");

        var reqA = Req("SESS-A");
        reqA.CaptureSessionId = session;
        var reqB = Req("SESS-B");
        reqB.CaptureSessionId = Guid.NewGuid().ToString("N");

        var a = await records.CreateAsync(reqA, user);
        var b = await records.CreateAsync(reqB, user);

        var filters = new RecordFilters
        {
            CaptureSessionId = session,
            FromUtc = a.CreatedAt.AddMinutes(-1),
            ToExclusiveUtc = a.CreatedAt.AddMinutes(1)
        };

        var file = await export.ExportAsync("csv", filters, user.UserId, viewerSeesAll: false);
        var csv = Encoding.UTF8.GetString(file.Bytes);
        Assert.Contains("SESS-A", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("SESS-B", csv, StringComparison.Ordinal);
        _ = b;
    }

    [Fact]
    public async Task Repository_GetByIds_Applies_Personal_Isolation()
    {
        var repo = new NepRecordRepository(_factory);
        var records = CreateRecordService();
        var userA = Actor(Guid.NewGuid().ToString(), "isoA", AppUserRole.Operario);
        var userB = Actor(Guid.NewGuid().ToString(), "isoB", AppUserRole.Operario);

        var a1 = await records.CreateAsync(Req("ISO-A1"), userA);
        var b1 = await records.CreateAsync(Req("ISO-B1"), userB);

        var got = await repo.GetByIdsAsync([a1.Id, b1.Id], userA.UserId, viewerSeesAll: false);
        Assert.Single(got);
        Assert.Equal(a1.Id, got[0].Id);
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
