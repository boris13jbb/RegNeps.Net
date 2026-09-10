using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Records;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Tests;

/// <summary>
/// Concurrencia real: archivo SQLite temporal + contextos independientes + Task.WhenAll.
/// No usa :memory: de una sola conexión (serializa escrituras).
/// </summary>
public sealed class DatabaseConcurrencyTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private DbContextOptions<RegNepsDbContext> _options = null!;
    private TestDbFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"regneps-conc-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _factory = new TestDbFactory(_options);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitializer.ApplySchemaPatchesAsync(db);
        await DbSeeder.SeedAsync(db);

        // WAL mejora escrituras concurrentes en SQLite.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }

    public Task DisposeAsync()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }

            var wal = _dbPath + "-wal";
            var shm = _dbPath + "-shm";
            if (File.Exists(wal))
            {
                File.Delete(wal);
            }

            if (File.Exists(shm))
            {
                File.Delete(shm);
            }
        }
        catch
        {
            // Best-effort cleanup of temp DB.
        }

        return Task.CompletedTask;
    }

    private NepRecordService CreateService() =>
        new(new NepRecordRepository(_factory), new AlertConfigRepository(_factory.CreateDbContext()));

    private static RecordActor Actor(string id, string name, AppUserRole role) =>
        RecordActor.Create(id, name, name, role, false);

    [Fact]
    public async Task Parallel_Same_ClientOperationId_Inserts_Single_Row()
    {
        var userId = Guid.NewGuid().ToString();
        var actor = Actor(userId, "opConc", AppUserRole.Operario);
        var opId = Guid.NewGuid().ToString("N");

        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            var service = CreateService();
            return await service.CreateAsync(new CreateNepRecordRequest
            {
                Telar = "C1",
                Neps = 11,
                Tela = "ConcFabric",
                LoteTrama = "CONCLOTE1",
                ClientOperationId = opId
            }, actor);
        }));

        var results = await Task.WhenAll(tasks);
        var distinctIds = results.Select(r => r.Id).Distinct().ToList();
        Assert.Single(distinctIds);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.NepRecords.CountAsync(r =>
            r.CreatedByUserId == userId && r.ClientOperationId == opId));
    }

    [Fact]
    public async Task Parallel_Different_Operations_Same_Telar_Lote_Insert_Two_Rows()
    {
        var a = Actor(Guid.NewGuid().ToString(), "opA", AppUserRole.Operario);
        var b = Actor(Guid.NewGuid().ToString(), "opB", AppUserRole.Operario);

        var t1 = Task.Run(async () =>
        {
            var service = CreateService();
            return await service.CreateAsync(new CreateNepRecordRequest
            {
                Telar = "99",
                Neps = 8,
                Tela = "Shared",
                LoteTrama = "SHARED99",
                ClientOperationId = Guid.NewGuid().ToString("N")
            }, a);
        });

        var t2 = Task.Run(async () =>
        {
            var service = CreateService();
            return await service.CreateAsync(new CreateNepRecordRequest
            {
                Telar = "99",
                Neps = 8,
                Tela = "Shared",
                LoteTrama = "SHARED99",
                ClientOperationId = Guid.NewGuid().ToString("N")
            }, b);
        });

        var results = await Task.WhenAll(t1, t2);
        Assert.NotEqual(results[0].Id, results[1].Id);
        Assert.Equal(a.UserId, results[0].CreatedByUserId);
        Assert.Equal(b.UserId, results[1].CreatedByUserId);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.NepRecords.CountAsync(r =>
            r.Telar == "99" && r.LoteTrama == "SHARED99"));
    }

    [Fact]
    public async Task Parallel_EnsureActive_Fabric_Creates_Single_Row()
    {
        var name = $"Fabric-{Guid.NewGuid():N}";
        var repo = new FabricRepository(_factory);

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            repo.EnsureActiveByNameAsync(name)));

        var fabrics = await Task.WhenAll(tasks);
        Assert.True(fabrics.Select(f => f.Id).Distinct().Count() == 1);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Fabrics.CountAsync(f => f.Name == name));
    }

    [Fact]
    public async Task Parallel_EnsureActive_Lote_Creates_Single_Row()
    {
        var code = $"LOT{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var repo = new LoteTramaRepository(_factory);

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            repo.EnsureActiveByCodeAsync(code)));

        var lots = await Task.WhenAll(tasks);
        Assert.True(lots.Select(l => l.Id).Distinct().Count() == 1);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.LoteTramaItems.CountAsync(l => l.Code == code));
    }

    [Fact]
    public async Task Parallel_Updates_With_Same_Stamp_Only_One_Succeeds()
    {
        var service = CreateService();
        var admin = Actor(Guid.NewGuid().ToString(), "adm", AppUserRole.Admin);
        var saved = await service.CreateAsync(new CreateNepRecordRequest
        {
            Telar = "U1",
            Neps = 3,
            Tela = "X",
            LoteTrama = "LU1",
            ClientOperationId = Guid.NewGuid().ToString("N")
        }, admin);

        var stamp = saved.ConcurrencyStamp;
        var id = saved.Id;

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            var svc = CreateService();
            try
            {
                await svc.UpdateAsync(new UpdateNepRecordRequest
                {
                    Id = id,
                    Telar = "U1",
                    Neps = 3 + i,
                    Tela = "X",
                    LoteTrama = "LU1",
                    ExpectedConcurrencyStamp = stamp
                }, admin);
                return true;
            }
            catch (RecordConcurrencyConflictException)
            {
                return false;
            }
        }));

        var outcomes = await Task.WhenAll(tasks);
        Assert.Equal(1, outcomes.Count(x => x));
        Assert.Equal(7, outcomes.Count(x => !x));

        await using var db = _factory.CreateDbContext();
        var current = await db.NepRecords.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.NotEqual(stamp, current.ConcurrencyStamp);
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }
}
