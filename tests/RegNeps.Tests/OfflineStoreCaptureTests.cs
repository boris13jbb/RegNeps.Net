using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RegNeps.Domain.Enums;
using RegNeps.OfflineStore;
using RegNeps.OfflineStore.Device;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;

namespace RegNeps.Tests;

public class OfflineStoreCaptureTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-offline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "test.db");
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            /* ignore temp cleanup */
        }

        return Task.CompletedTask;
    }

    private LocalSyncDbContext CreateContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<LocalSyncDbContext>()
            .UseSqlite($"Data Source={_dbPath}");
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new LocalSyncDbContext(builder.Options);
    }

    private async Task<(OfflineCaptureService Capture, OfflineSessionService Sessions, OfflineOutboxQuery Outbox)>
        CreateServicesAsync(LocalSyncDbContext db)
    {
        var secure = new MemorySecureAuthMaterialStore();
        var sessions = new OfflineSessionService(db, secure);
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "device"));
        var capture = new OfflineCaptureService(db, sessions, devices);
        var outbox = new OfflineOutboxQuery(db);

        await sessions.UpsertUxSnapshotAsync(
            "user-1",
            "operario@test",
            "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission, "ViewRecords"],
            "http://localhost:5080",
            TimeSpan.FromHours(24));

        return (capture, sessions, outbox);
    }

    [Fact]
    public async Task Migration_Creates_Schema()
    {
        await using var db = CreateContext();
        Assert.True(await db.Database.CanConnectAsync());
        var pending = await db.Database.GetPendingMigrationsAsync();
        Assert.Empty(pending);
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains("20261002180000_InitialLocalSync", applied);
        Assert.Contains("20261002211500_AddSyncEngineFields", applied);
    }

    [Fact]
    public async Task Create_Offline_Creates_Record_And_Exactly_One_PendingOperation()
    {
        await using var db = CreateContext();
        var (capture, _, outbox) = await CreateServicesAsync(db);

        var result = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = "003",
            Neps = 18,
            Tela = "Denim",
            LoteTrama = "63E26401",
            Turno = "A"
        });

        Assert.Equal(LocalSyncStatus.PendingSync, result.Record.SyncStatus);
        Assert.Equal(PendingOperationStatus.Pending, result.Operation.Status);
        Assert.Equal(OfflineOperationType.CreateRecord, result.Operation.OperationType);
        Assert.Equal(result.Record.ClientOperationId, result.Operation.ClientOperationId);
        Assert.False(string.IsNullOrWhiteSpace(result.Record.ClientOperationId));
        Assert.False(string.IsNullOrWhiteSpace(result.Record.CaptureSessionId));
        Assert.Equal(result.Record.CaptureSessionId, result.Operation.CaptureSessionId);

        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
        Assert.Equal(1, await db.PendingOperations.CountAsync());
        Assert.Equal(1, await outbox.CountPendingAsync());
    }

    [Fact]
    public async Task Create_Uses_NepsQualityCriteria_Boundaries()
    {
        await using var db = CreateContext();
        var (capture, _, _) = await CreateServicesAsync(db);

        async Task AssertLevel(double neps, AlertLevel expected)
        {
            var r = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
            {
                Telar = "T",
                Neps = neps
            });
            Assert.Equal(expected, r.QualityLevel);
            Assert.Equal(expected.ToDisplayLabel(), r.QualityLabel);
            Assert.Equal(expected, r.Record.GetAlertLevel());
        }

        await AssertLevel(18, AlertLevel.Ok);
        await AssertLevel(19, AlertLevel.Mention);
        await AssertLevel(46, AlertLevel.CriticalAdjustment);
        await AssertLevel(55, AlertLevel.SecondQuality);
    }

    [Fact]
    public async Task Persistence_Survives_DbContext_Restart()
    {
        string clientOp;
        string captureSession;
        await using (var db = CreateContext())
        {
            var (capture, _, _) = await CreateServicesAsync(db);
            var result = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
            {
                Telar = "104",
                Neps = 45
            });
            clientOp = result.Record.ClientOperationId;
            captureSession = result.Record.CaptureSessionId!;
        }

        await using (var db2 = CreateContext())
        {
            var record = await db2.LocalNepRecords.SingleAsync(r => r.ClientOperationId == clientOp);
            var op = await db2.PendingOperations.SingleAsync(o => o.ClientOperationId == clientOp);
            Assert.Equal(LocalSyncStatus.PendingSync, record.SyncStatus);
            Assert.Equal(PendingOperationStatus.Pending, op.Status);
            Assert.Equal(captureSession, record.CaptureSessionId);
            Assert.Equal(1, await db2.PendingOperations.CountAsync());
            Assert.Equal(1, await db2.LocalNepRecords.CountAsync());
        }
    }

    [Fact]
    public async Task Second_Insert_Failure_Rolls_Back_First_Insert_In_Same_Transaction()
    {
        // El primer SaveChanges (LocalNepRecord) entra en la TX; el segundo (PendingOperation) falla.
        // Tras rollback, la BD no debe conservar el registro local.
        await using (var db = CreateContext(new FailOnPendingOperationInsertInterceptor()))
        {
            var (capture, _, _) = await CreateServicesAsync(db);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                capture.CreateRecordAsync(new OfflineCreateRecordRequest
                {
                    Telar = "X",
                    Neps = 10
                }));
        }

        await using var verify = CreateContext();
        Assert.Equal(0, await verify.LocalNepRecords.CountAsync());
        Assert.Equal(0, await verify.PendingOperations.CountAsync());
    }

    [Fact]
    public async Task DbTrigger_Rejecting_Outbox_Also_Rolls_Back_Local_Record()
    {
        await using (var setup = CreateContext())
        {
            await CreateServicesAsync(setup);
            // Fuerza fallo del segundo INSERT a nivel SQLite (no solo interceptor).
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER IF NOT EXISTS trg_reject_pending_ops
                BEFORE INSERT ON PendingOperations
                BEGIN
                    SELECT RAISE(ABORT, 'forced outbox reject');
                END;
                """);
        }

        await using (var db = CreateContext())
        {
            var (capture, _, _) = await CreateServicesAsync(db);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                capture.CreateRecordAsync(new OfflineCreateRecordRequest
                {
                    Telar = "Y",
                    Neps = 11
                }));
        }

        await using var verify = CreateContext();
        Assert.Equal(0, await verify.LocalNepRecords.CountAsync());
        Assert.Equal(0, await verify.PendingOperations.CountAsync());
    }

    [Fact]
    public async Task LocalSession_Has_No_Password_Properties()
    {
        var names = OfflineSessionService.LocalSessionPropertyNames();
        Assert.DoesNotContain(names, n => n.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("Token", StringComparison.OrdinalIgnoreCase));

        await using (var db = CreateContext())
        {
            var secure = new MemorySecureAuthMaterialStore();
            var sessions = new OfflineSessionService(db, secure);
            await sessions.UpsertUxSnapshotAsync(
                "u1", "u1", "Operario",
                [OfflineStoreConstants.CaptureRecordsPermission],
                null,
                secureAuthMaterial: "cookie-material-not-a-password");

            var session = await sessions.GetValidSessionAsync();
            Assert.NotNull(session);
            Assert.True(session!.HasSecureAuthMaterial);

            // Columnas de LocalSessions no incluyen password/token.
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info('LocalSessions');";
            await using var reader = await cmd.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.DoesNotContain(columns, c => c.Contains("password", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(columns, c => c.Contains("token", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task DeviceId_Is_Stable_Across_Calls_And_Is_Guid()
    {
        var store = new FileDeviceIdStore(Path.Combine(_dir, "dev2"));
        var a = await store.GetOrCreateAsync();
        var b = await store.GetOrCreateAsync();
        Assert.Equal(a, b);
        Assert.True(Guid.TryParse(a, out _));
    }

    [Fact]
    public async Task Atomic_Pair_Shares_Same_ClientOperationId()
    {
        await using var db = CreateContext();
        var (capture, _, _) = await CreateServicesAsync(db);
        var result = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = "801",
            Neps = 12,
            CaptureSessionId = "session-fixed-001"
        });

        Assert.Equal("session-fixed-001", result.Record.CaptureSessionId);
        Assert.Equal(result.Record.Id, result.Operation.LocalNepRecordId);
        Assert.Equal(result.Record.ClientOperationId, result.Operation.ClientOperationId);
        Assert.All(
            await db.PendingOperations.ToListAsync(),
            o => Assert.Equal(PendingOperationStatus.Pending, o.Status));
    }

    /// <summary>Permite el SaveChanges del LocalNepRecord; falla al persistir PendingOperation.</summary>
    private sealed class FailOnPendingOperationInsertInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            if (HasAddedPendingOperation(eventData))
            {
                throw new InvalidOperationException("forced outbox insert failure");
            }

            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (HasAddedPendingOperation(eventData))
            {
                throw new InvalidOperationException("forced outbox insert failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static bool HasAddedPendingOperation(DbContextEventData eventData) =>
            eventData.Context?.ChangeTracker.Entries()
                .Any(e => e.Entity is RegNeps.OfflineStore.Entities.PendingOperation
                          && e.State == EntityState.Added) == true;
    }
}
