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
using Xunit;

namespace RegNeps.Tests;

public sealed class SavedReportAccessTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;

    public SavedReportAccessTests()
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

    [Fact]
    public void CanAccess_Author_Always()
    {
        Assert.True(SavedReportAccess.CanAccess("author-1", "author-1", false, false, false));
    }

    [Fact]
    public void CanAccess_SeesAll_With_Manage_Or_Export()
    {
        Assert.True(SavedReportAccess.CanAccess("author-1", "other", true, true, false));
        Assert.True(SavedReportAccess.CanAccess("author-1", "other", true, false, true));
        Assert.False(SavedReportAccess.CanAccess("author-1", "other", true, false, false));
        Assert.False(SavedReportAccess.CanAccess("author-1", "other", false, true, true));
    }

    [Fact]
    public async Task NonAuthor_Without_SeesAll_Cannot_Open_Snapshot_Or_Export()
    {
        var (export, authorId, reportId) = await SeedSavedReportAsync();
        var stranger = ReportAccessActor.Create("stranger", false, false, false);

        var denied = await Assert.ThrowsAsync<InvalidOperationException>(
            () => export.GetAccessibleSavedAsync(reportId, stranger));
        Assert.Equal(SavedReportAccess.NotFoundMessage, denied.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => export.LoadAccessibleSnapshotAsync(reportId, stranger));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            export.ExportSavedAsync(
                reportId, "csv", stranger.UserId, stranger.SeesAllRecords,
                hasManageReports: false, hasExportReports: false));
    }

    [Fact]
    public async Task Author_Can_Open_Snapshot_And_Export()
    {
        var (export, authorId, reportId) = await SeedSavedReportAsync();
        var author = ReportAccessActor.Create(authorId, false, false, false);

        var saved = await export.GetAccessibleSavedAsync(reportId, author);
        Assert.Equal(reportId, saved.Id);

        var file = await export.ExportSavedAsync(
            reportId, "csv", author.UserId, author.SeesAllRecords,
            hasManageReports: false, hasExportReports: false);
        Assert.True(file.Bytes.Length > 0);
        Assert.Equal("text/csv", file.ContentType);
        Assert.Contains("Telar", Encoding.UTF8.GetString(file.Bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeesAll_With_ManageReports_Can_Open_And_Export()
    {
        var (export, _, reportId) = await SeedSavedReportAsync();
        var manager = ReportAccessActor.Create("manager-1", true, true, false);

        var saved = await export.GetAccessibleSavedAsync(reportId, manager);
        Assert.Equal(reportId, saved.Id);

        var file = await export.ExportSavedAsync(
            reportId, "csv", manager.UserId, manager.SeesAllRecords,
            hasManageReports: true, hasExportReports: false);
        Assert.True(file.Bytes.Length > 0);
    }

    [Fact]
    public async Task SaveReportByIds_Without_ManageReports_Is_Denied()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var actor = RecordActor.Create(
            Guid.NewGuid().ToString(), "u", "u", AppUserRole.Operario, false);
        var a = await records.CreateAsync(Req("A1"), actor);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            export.SaveReportByIdsAsync(
                "x", [a.Id], actor.UserId, actor.DisplayName, viewerSeesAll: true,
                hasManageReports: false));
    }

    [Fact]
    public async Task ListSaved_Hides_Reports_Not_Accessible()
    {
        var (export, authorId, reportId) = await SeedSavedReportAsync();
        var author = ReportAccessActor.Create(authorId, false, true, false);
        var stranger = ReportAccessActor.Create("stranger", false, true, false);
        var manager = ReportAccessActor.Create("mgr", true, true, false);

        var forAuthor = await export.ListSavedAsync(author);
        Assert.Contains(forAuthor, r => r.Id == reportId);

        var forStranger = await export.ListSavedAsync(stranger);
        Assert.DoesNotContain(forStranger, r => r.Id == reportId);

        var forManager = await export.ListSavedAsync(manager);
        Assert.Contains(forManager, r => r.Id == reportId);
    }

    [Fact]
    public async Task Update_And_Delete_Deny_NonAuthor_Without_SeesAll()
    {
        var (export, authorId, reportId) = await SeedSavedReportAsync();
        var stranger = ReportAccessActor.Create("stranger", false, true, false);
        var filters = new RecordFilters();
        ReportDateRange.FromLocalCalendarDates(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1))
            .ApplyTo(filters);

        var updateDenied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            export.UpdateReportAsync(
                reportId, "Hack", filters, stranger.UserId, stranger.SeesAllRecords,
                hasManageReports: true));
        Assert.Equal(SavedReportAccess.NotFoundMessage, updateDenied.Message);

        var deleteDenied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            export.DeleteSavedAsync(reportId, stranger));
        Assert.Equal(SavedReportAccess.NotFoundMessage, deleteDenied.Message);

        var author = ReportAccessActor.Create(authorId, false, true, false);
        await export.DeleteSavedAsync(reportId, author);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            export.GetAccessibleSavedAsync(reportId, author));
    }

    private async Task<(ReportExportAppService Export, string AuthorId, Guid ReportId)> SeedSavedReportAsync()
    {
        var records = CreateRecordService();
        var export = CreateExportService();
        var authorId = Guid.NewGuid().ToString();
        var actor = RecordActor.Create(authorId, "author", "author", AppUserRole.Operario, false);
        await records.CreateAsync(Req("IDOR-1"), actor);

        var filters = new RecordFilters();
        ReportDateRange.FromLocalCalendarDates(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1))
            .ApplyTo(filters);

        var saved = await export.SaveReportAsync(
            "Informe IDOR", filters, authorId, "author", viewerSeesAll: false,
            hasManageReports: true);
        return (export, authorId, saved.Id);
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

    private static CreateNepRecordRequest Req(string telar) => new()
    {
        Telar = telar,
        Neps = 12,
        Tela = "Denim",
        LoteTrama = "L-1",
        Turno = "A",
        Operario = "Op",
        ClientOperationId = Guid.NewGuid().ToString("N"),
        CaptureSessionId = Guid.NewGuid().ToString("N")
    };

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
