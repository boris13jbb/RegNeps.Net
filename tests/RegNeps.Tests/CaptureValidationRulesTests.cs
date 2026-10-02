using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Records;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Tests;

public sealed class CaptureValidationRulesTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;
    private TestDbFactory _factory = null!;

    public CaptureValidationRulesTests()
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

    [Theory]
    [InlineData(100, false)]
    [InlineData(100.0, false)]
    [InlineData(101, true)]
    [InlineData(99.99, false)]
    public void RequiresHighNepsConfirmation_UsesThreshold100(double neps, bool expected)
    {
        Assert.Equal(expected, CaptureValidationRules.RequiresHighNepsConfirmation(neps));
        Assert.Equal(100, CaptureValidationConstants.NepsConfirmationThreshold);
    }

    [Fact]
    public void IsRecentDuplicate_MatchesWithinWindow()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var existing = new NepRecord
        {
            CreatedByUserId = "user-1",
            Telar = "T01",
            Tela = "Denim",
            LoteTrama = "63E264H001",
            Neps = 45,
            CreatedAt = now.AddMinutes(-1)
        };

        Assert.True(CaptureValidationRules.IsRecentDuplicate(
            "user-1", "T01", "Denim", "63E264H001", 45, existing, now, 2));
    }

    [Fact]
    public void IsRecentDuplicate_OutsideWindow_IsFalse()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var existing = new NepRecord
        {
            CreatedByUserId = "user-1",
            Telar = "T01",
            Tela = "Denim",
            LoteTrama = "63E264H001",
            Neps = 45,
            CreatedAt = now.AddMinutes(-3)
        };

        Assert.False(CaptureValidationRules.IsRecentDuplicate(
            "user-1", "T01", "Denim", "63E264H001", 45, existing, now, 2));
    }

    [Fact]
    public void IsRecentDuplicate_DifferentNeps_IsFalse()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var existing = new NepRecord
        {
            CreatedByUserId = "user-1",
            Telar = "T01",
            Tela = "Denim",
            LoteTrama = "63E264H001",
            Neps = 45,
            CreatedAt = now.AddSeconds(-30)
        };

        Assert.False(CaptureValidationRules.IsRecentDuplicate(
            "user-1", "T01", "Denim", "63E264H001", 46, existing, now, 2));
    }

    [Fact]
    public void ResolveShareTodayIds_PrefersLastCreated()
    {
        var last = Guid.NewGuid();
        var selected = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var today = new[] { (Guid.NewGuid(), DateTime.UtcNow) };

        var ids = CaptureValidationRules.ResolveShareTodayIds(last, selected, today);
        Assert.Single(ids);
        Assert.Equal(last, ids[0]);
    }

    [Fact]
    public void ResolveShareTodayIds_ThenSelected()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var today = new[] { (Guid.NewGuid(), DateTime.UtcNow.AddHours(-1)) };

        var ids = CaptureValidationRules.ResolveShareTodayIds(null, [a, b, a], today);
        Assert.Equal(2, ids.Count);
        Assert.Equal(a, ids[0]);
        Assert.Equal(b, ids[1]);
    }

    [Fact]
    public void ResolveShareTodayIds_ThenMostRecentToday()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var today = new[]
        {
            (older, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc)),
            (newer, new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc))
        };

        var ids = CaptureValidationRules.ResolveShareTodayIds(null, [], today);
        Assert.Single(ids);
        Assert.Equal(newer, ids[0]);
    }

    [Fact]
    public void PendingCaptureReportIds_PutsPreferFirst()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var s3 = Guid.NewGuid();
        var saved = new HashSet<Guid> { s2 };

        var ids = CaptureValidationRules.PendingCaptureReportIds(
            [s1, s2, s3],
            saved,
            preferLastPending: s3);

        Assert.Equal([s3, s1], ids);
    }

    [Fact]
    public void PendingCaptureReportIds_ExcludesSaved()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var saved = new HashSet<Guid> { s1, s2 };

        Assert.Empty(CaptureValidationRules.PendingCaptureReportIds([s1, s2], saved, null));
    }

    [Fact]
    public async Task HasRecentDuplicateAsync_DetectsSavedRecord()
    {
        var service = new NepRecordService(
            new NepRecordRepository(_factory),
            new AlertConfigRepository(_factory.CreateDbContext()));

        var actor = RecordActor.Create("dup-user", "dup", "dup@test", AppUserRole.Admin, isSuperAdmin: false);
        var session = Guid.NewGuid().ToString("N");
        var req = new CreateNepRecordRequest
        {
            Telar = "T-DUP",
            Neps = 12,
            Tela = "Canvas",
            LoteTrama = "63E264X001",
            Turno = "A",
            Operario = "Op",
            ClientOperationId = Guid.NewGuid().ToString("N"),
            CaptureSessionId = session
        };

        await service.CreateAsync(req, actor);

        var duplicateReq = new CreateNepRecordRequest
        {
            Telar = "T-DUP",
            Neps = 12,
            Tela = "Canvas",
            LoteTrama = "63E264X001",
            Turno = "A",
            Operario = "Op",
            ClientOperationId = Guid.NewGuid().ToString("N"),
            CaptureSessionId = session
        };

        Assert.True(await service.HasRecentDuplicateAsync(duplicateReq, actor));
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
