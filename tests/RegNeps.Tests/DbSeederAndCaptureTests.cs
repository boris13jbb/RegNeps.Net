using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Records;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Tests;

public class DbSeederAndCaptureTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;

    public DbSeederAndCaptureTests()
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
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private RegNepsDbContext CreateDb() => new(_options);

    [Fact]
    public async Task Seeder_Creates_Admin_Once()
    {
        await using var db = CreateDb();
        var admin = await db.Users.SingleAsync(u => u.Username == "admin");
        Assert.True(admin.IsSuperAdmin);
        Assert.True(admin.IsActive);

        await DbSeeder.SeedAsync(db);
        Assert.Equal(1, await db.Users.CountAsync(u => u.Username == "admin"));
    }

    [Fact]
    public async Task Seeder_Does_Not_Reactivate_SoftDeleted_Admin_When_Another_Super_Exists()
    {
        await using var db = CreateDb();
        var admin = await db.Users.SingleAsync(u => u.Username == "admin");
        admin.IsActive = false;
        admin.DeletedAt = DateTime.UtcNow;

        db.Users.Add(new AppUser
        {
            Username = "otro_super",
            DisplayName = "Otro",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("OtroSuper1!"),
            Role = AppUserRole.SuperAdmin,
            IsSuperAdmin = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        await DbSeeder.SeedAsync(db);

        var reloaded = await db.Users.SingleAsync(u => u.Username == "admin");
        Assert.False(reloaded.IsActive);
        Assert.NotNull(reloaded.DeletedAt);
    }

    [Fact]
    public async Task Create_Rejects_Zero_And_Negative_Neps()
    {
        var factory = new TestDbFactory(_options);
        var service = new NepRecordService(
            new NepRecordRepository(factory),
            new AlertConfigRepository(factory.CreateDbContext()));
        var actor = RecordActor.Create(Guid.NewGuid().ToString(), "op", "op", AppUserRole.Operario, false);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T1",
            Neps = 0,
            Tela = "A",
            Turno = "1",
            Operario = "op"
        }, actor));

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T1",
            Neps = -5,
            Tela = "A",
            Turno = "1",
            Operario = "op"
        }, actor));
    }

    [Fact]
    public async Task Create_Persists_Valid_Record_With_Meters()
    {
        var factory = new TestDbFactory(_options);
        var service = new NepRecordService(
            new NepRecordRepository(factory),
            new AlertConfigRepository(factory.CreateDbContext()));
        var actor = RecordActor.Create(Guid.NewGuid().ToString(), "juan", "juan", AppUserRole.Operario, false);

        var saved = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "T10",
            Neps = 9,
            Tela = "Denim",
            Turno = "A",
            Operario = "juan",
            LoteTrama = "63E26401",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, actor);

        Assert.Equal(100, saved.MtsCalculados, precision: 6);
        await using var db = CreateDb();
        Assert.Equal(1, await db.NepRecords.CountAsync());
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
