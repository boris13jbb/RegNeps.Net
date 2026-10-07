using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RegNeps.OfflineStore;
using RegNeps.OfflineStore.Device;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;
using RegNeps.OfflineStore.Sync.Contracts;
using RegNeps.OfflineStore.Sync.Ux;

namespace RegNeps.Tests;

/// <summary>
/// FASE 2D.3.1 — validación de persistencia UX, consistencia de contadores,
/// gate, auth vs red, conflictos y ServerRecordId (sin cambiar el protocolo).
/// </summary>
public sealed class OfflineSyncUxValidation231Tests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-ux231-" + Guid.NewGuid().ToString("N"));
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
            /* ignore */
        }

        return Task.CompletedTask;
    }

    private LocalSyncDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LocalSyncDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new LocalSyncDbContext(options);
    }

    private async Task SeedSessionAsync(LocalSyncDbContext db, string userId = "user-a", TimeSpan? ttl = null)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM PendingOperations");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalNepRecords");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalSessions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SyncStates");
        db.ChangeTracker.Clear();

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        await sessions.UpsertUxSnapshotAsync(
            userId,
            "alice",
            "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080",
            ttl ?? TimeSpan.FromHours(72));
        db.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = 0 });
        await db.SaveChangesAsync();
    }

    private OfflineCaptureService CreateCapture(LocalSyncDbContext db)
    {
        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "d-" + Guid.NewGuid().ToString("N")[..8]));
        return new OfflineCaptureService(db, sessions, devices);
    }

    // --- Persistencia tras reinicio (nuevo DbContext = “reabrir app”) ---

    [Fact]
    public async Task Persist_Pending_Survives_Store_Reopen()
    {
        Guid opId;
        string clientOpId;
        await using (var db = CreateContext())
        {
            await SeedSessionAsync(db);
            var capture = CreateCapture(db);
            var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "P1", Neps = 10 });
            opId = created.Operation.Id;
            clientOpId = created.Operation.ClientOperationId;
            Assert.Equal(PendingOperationStatus.Pending, created.Operation.Status);
        }

        await using (var db2 = CreateContext())
        {
            var ux = new OfflineSyncUxService(db2);
            var counters = await ux.GetCountersAsync("user-a");
            Assert.Equal(1, counters.Pending);
            Assert.Equal(0, counters.Synced);
            Assert.Equal(0, counters.SyncError);
            Assert.Equal(0, counters.Conflict);

            var op = await db2.PendingOperations.SingleAsync(o => o.Id == opId);
            Assert.Equal(PendingOperationStatus.Pending, op.Status);
            Assert.Equal(clientOpId, op.ClientOperationId);
            Assert.Equal(1, await db2.LocalNepRecords.CountAsync());
        }
    }

    [Fact]
    public async Task Persist_SyncError_Survives_Store_Reopen()
    {
        Guid opId;
        await using (var db = CreateContext())
        {
            await SeedSessionAsync(db);
            var capture = CreateCapture(db);
            var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "E1", Neps = 11 });
            opId = created.Operation.Id;
            var op = await db.PendingOperations.SingleAsync();
            op.Status = PendingOperationStatus.SyncError;
            op.LastServerErrorCode = "FORBIDDEN";
            op.LastError = "No permission";
            await db.SaveChangesAsync();
        }

        await using (var db2 = CreateContext())
        {
            var ux = new OfflineSyncUxService(db2);
            var counters = await ux.GetCountersAsync("user-a");
            Assert.Equal(0, counters.Pending);
            Assert.Equal(1, counters.SyncError);
            var op = await db2.PendingOperations.SingleAsync(o => o.Id == opId);
            Assert.Equal(PendingOperationStatus.SyncError, op.Status);
            Assert.Equal("FORBIDDEN", op.LastServerErrorCode);
            Assert.Equal(SyncResultUserMessages.Forbidden, OfflineSyncUxService.DescribeSyncErrorKind(op));
        }
    }

    [Fact]
    public async Task Persist_Conflict_Survives_Store_Reopen_With_Snapshots()
    {
        Guid localId;
        string clientOpId;
        const string snapJson = """{"telar":"SERVER","neps":50,"qualityLabel":"OK","concurrencyStamp":"srv-stamp"}""";

        await using (var db = CreateContext())
        {
            await SeedSessionAsync(db);
            var capture = CreateCapture(db);
            var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "LOCAL", Neps = 20 });
            localId = created.Record.Id;
            clientOpId = created.Operation.ClientOperationId;
            var op = await db.PendingOperations.SingleAsync();
            op.Status = PendingOperationStatus.Conflict;
            op.ConflictServerConcurrencyStamp = "srv-stamp";
            op.ConflictServerSnapshotJson = snapJson;
            op.LastError = "Concurrency conflict";
            created.Record.SyncStatus = LocalSyncStatus.Conflict;
            created.Record.Telar = "LOCAL";
            created.Record.Neps = 20;
            await db.SaveChangesAsync();
        }

        await using (var db2 = CreateContext())
        {
            var ux = new OfflineSyncUxService(db2);
            var counters = await ux.GetCountersAsync("user-a");
            Assert.Equal(1, counters.Conflict);
            Assert.Equal(0, counters.Pending);
            Assert.Equal(0, counters.Synced);

            var op = await db2.PendingOperations.SingleAsync();
            Assert.Equal(PendingOperationStatus.Conflict, op.Status);
            Assert.Equal("srv-stamp", op.ConflictServerConcurrencyStamp);
            Assert.Equal(snapJson, op.ConflictServerSnapshotJson);
            Assert.Equal(clientOpId, op.ClientOperationId);

            var local = await db2.LocalNepRecords.SingleAsync(r => r.Id == localId);
            Assert.Equal("LOCAL", local.Telar);
            Assert.Equal(20, local.Neps);
            Assert.Equal(LocalSyncStatus.Conflict, local.SyncStatus);

            var items = await ux.GetConflictsAsync("user-a");
            Assert.Single(items);
            Assert.Equal("Requiere revisión", items[0].ActionHint);
            Assert.Contains("LOCAL", items[0].LocalSummary);
            Assert.Contains("SERVER", items[0].ServerSummary);
        }
    }

    // --- Contadores ---

    [Fact]
    public async Task Counters_Empty_And_Mixed_Are_Mutually_Exclusive()
    {
        await using var db = CreateContext();
        await SeedSessionAsync(db);
        var ux = new OfflineSyncUxService(db);

        var empty = await ux.GetCountersAsync("user-a");
        Assert.Equal(0, empty.Pending);
        Assert.Equal(0, empty.Synced);
        Assert.Equal(0, empty.SyncError);
        Assert.Equal(0, empty.Conflict);
        Assert.Equal(0, empty.Total);

        var capture = CreateCapture(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 1 });
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "2", Neps = 2 });
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "3", Neps = 3 });
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "4", Neps = 4 });

        var ops = await db.PendingOperations.OrderBy(o => o.CreatedAtUtc).ToListAsync();
        ops[0].Status = PendingOperationStatus.Pending;
        ops[1].Status = PendingOperationStatus.Synced;
        ops[2].Status = PendingOperationStatus.SyncError;
        ops[3].Status = PendingOperationStatus.Conflict;
        await db.SaveChangesAsync();

        var mixed = await ux.GetCountersAsync("user-a");
        Assert.Equal(1, mixed.Pending);
        Assert.Equal(1, mixed.Synced);
        Assert.Equal(1, mixed.SyncError);
        Assert.Equal(1, mixed.Conflict);
        Assert.Equal(4, mixed.Total);
        // Una fila = un estado; Synced no infla Pending.
        Assert.Equal(4, await db.PendingOperations.CountAsync());
        Assert.Equal(1, await db.PendingOperations.CountAsync(o => o.Status == PendingOperationStatus.Pending));
    }

    [Fact]
    public async Task Counters_Sending_Counts_As_Pending()
    {
        await using var db = CreateContext();
        await SeedSessionAsync(db);
        var capture = CreateCapture(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "S", Neps = 5 });
        var op = await db.PendingOperations.SingleAsync();
        op.Status = PendingOperationStatus.Sending;
        await db.SaveChangesAsync();

        var ux = new OfflineSyncUxService(db);
        var c = await ux.GetCountersAsync("user-a");
        Assert.Equal(1, c.Pending);
        Assert.Equal(0, c.Synced);
    }

    // --- Gate / concurrencia / error libera ---

    [Fact]
    public async Task ManualSync_Error_Releases_Gate_For_Retry()
    {
        var gate = new ManualSyncGate();
        var calls = 0;
        var engine = new ScriptedEngine(_ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(new SyncRunResult { Started = true, PullCompleted = true });
        });

        var runner = new ManualSyncRunner(engine, gate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TrySyncAsync());
        Assert.False(gate.IsBusy);

        var (invoked, result) = await runner.TrySyncAsync();
        Assert.True(invoked);
        Assert.NotNull(result);
        Assert.True(result!.PullCompleted);
        Assert.Equal(2, calls);
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task ManualSync_Concurrent_Single_Engine_Call_No_Double_Push()
    {
        var gate = new ManualSyncGate();
        var pushCalls = 0;
        var engine = new ScriptedEngine(async ct =>
        {
            Interlocked.Increment(ref pushCalls);
            await Task.Delay(120, ct);
            return new SyncRunResult { Started = true, PullCompleted = true, PushedAccepted = 1 };
        });

        var runner = new ManualSyncRunner(engine, gate);
        var tasks = Enumerable.Range(0, 5).Select(_ => runner.TrySyncAsync()).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Invoked));
        Assert.Equal(4, results.Count(r => !r.Invoked));
        Assert.Equal(1, pushCalls);
        Assert.False(gate.IsBusy);
    }

    // --- Auth vs red vs 403 ---

    [Fact]
    public void MapRunResult_Distinguishes_401_Network_And_403()
    {
        var auth = OfflineSyncUxService.MapRunResult(new SyncRunResult
        {
            Started = true,
            AuthRequired = true,
            Message = "Sesión de servidor no válida (401)."
        });
        Assert.True(auth.RequiresLogin);
        Assert.Equal(SyncResultUserMessages.RequiresLogin, auth.Message);

        var network = OfflineSyncUxService.MapRunResult(new SyncRunResult
        {
            Started = true,
            AuthRequired = false,
            PullCompleted = false,
            Message = "Error de red al contactar el servidor."
        });
        Assert.False(network.RequiresLogin);
        Assert.Contains("red", network.Message, StringComparison.OrdinalIgnoreCase);

        var forbidden = OfflineSyncUxService.MapRunResult(new SyncRunResult
        {
            Started = true,
            AuthRequired = false,
            PullCompleted = false,
            Message = "Acceso denegado (403)."
        });
        Assert.False(forbidden.RequiresLogin);
        Assert.Equal(SyncResultUserMessages.Forbidden, forbidden.Message);

        var timeout = OfflineSyncUxService.MapRunResult(new SyncRunResult
        {
            Started = true,
            PullCompleted = false,
            Message = "Timeout al contactar el servidor."
        });
        Assert.False(timeout.RequiresLogin);
        Assert.Contains("Timeout", timeout.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncEngine_MissingCookie_Is_AuthRequired_Not_Network()
    {
        await using var db = CreateContext();
        await SeedSessionAsync(db);
        var capture = CreateCapture(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "cookie-miss"));
        var engine = new SyncEngine(db, sessions, devices, new ThrowingApi(), new StaticCookie(null));
        var run = await engine.SyncAsync();

        Assert.False(run.Started);
        Assert.True(run.AuthRequired);
        Assert.False(run.SessionMissingOrExpired);

        var feedback = OfflineSyncUxService.MapRunResult(run);
        Assert.True(feedback.RequiresLogin);
        Assert.Equal(1, await db.PendingOperations.CountAsync(o => o.Status == PendingOperationStatus.Pending));
    }

    [Fact]
    public async Task SyncEngine_Http401_AuthRequired_Leaves_Pending()
    {
        await using var db = CreateContext();
        await SeedSessionAsync(db);
        var capture = CreateCapture(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "http401"));
        var api = new ThrowingApi(new SyncTransportException(
            SyncTransportFailureKind.Unauthorized, "Sesión de servidor no válida (401).", 401));
        var engine = new SyncEngine(db, sessions, devices, api, new StaticCookie("RegNeps.Auth=x"));
        var run = await engine.SyncAsync();

        Assert.True(run.AuthRequired);
        Assert.Equal(1, await db.PendingOperations.CountAsync(o => o.Status == PendingOperationStatus.Pending));
        var feedback = OfflineSyncUxService.MapRunResult(run);
        Assert.True(feedback.RequiresLogin);
        Assert.False(feedback.Message.Contains("red", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SyncEngine_Http403_Not_AuthRequired_Ops_Stay_Pending_Or_ErrorPath()
    {
        await using var db = CreateContext();
        await SeedSessionAsync(db);
        var capture = CreateCapture(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "http403"));
        var api = new ThrowingApi(new SyncTransportException(
            SyncTransportFailureKind.Forbidden, "Acceso denegado (403).", 403));
        var engine = new SyncEngine(db, sessions, devices, api, new StaticCookie("RegNeps.Auth=x"));
        var run = await engine.SyncAsync();

        Assert.False(run.AuthRequired);
        Assert.Contains("403", run.Message ?? "");
        // Transporte Forbidden no marca AuthRequired; Outbox no se marca Synced.
        Assert.Equal(0, await db.PendingOperations.CountAsync(o => o.Status == PendingOperationStatus.Synced));
        var feedback = OfflineSyncUxService.MapRunResult(run);
        Assert.False(feedback.RequiresLogin);
        Assert.Equal(SyncResultUserMessages.Forbidden, feedback.Message);
    }

    [Fact]
    public async Task SyncEngine_Network_Error_Keeps_Pending_Not_Login()
    {
        await using var db = CreateContext();
        await SeedSessionAsync(db);
        var capture = CreateCapture(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "net"));
        var api = new ThrowingApi(new SyncTransportException(
            SyncTransportFailureKind.Network, "Error de red al contactar el servidor."));
        var engine = new SyncEngine(db, sessions, devices, api, new StaticCookie("RegNeps.Auth=x"));
        var run = await engine.SyncAsync();

        Assert.False(run.AuthRequired);
        Assert.True(run.PushedTransient >= 1);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);

        var feedback = OfflineSyncUxService.MapRunResult(run, await new OfflineSyncUxService(db).GetCountersAsync("user-a"));
        Assert.False(feedback.RequiresLogin);
        Assert.Equal(1, feedback.StillPending);
    }

    // --- Connectivity UX / TTL ---

    [Fact]
    public void Connectivity_LocalSession_Vs_Cookie_Vs_Expired()
    {
        Assert.Equal(
            SyncConnectivityUxKind.OnlineReady,
            OfflineSyncUxService.ResolveConnectivity(true, false, true, false, false, false));

        Assert.Equal(
            SyncConnectivityUxKind.LocalSessionWithoutOnlineAuth,
            OfflineSyncUxService.ResolveConnectivity(true, false, false, false, false, false));
        Assert.Contains(
            "capturar temporalmente",
            OfflineSyncUxService.ConnectivityBanner(SyncConnectivityUxKind.LocalSessionWithoutOnlineAuth),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            SyncConnectivityUxKind.OfflineNoNetwork,
            OfflineSyncUxService.ResolveConnectivity(true, false, true, true, false, false));

        Assert.Equal(
            SyncConnectivityUxKind.LocalSessionExpired,
            OfflineSyncUxService.ResolveConnectivity(false, true, false, false, false, false));

        Assert.Equal(
            SyncConnectivityUxKind.NoLocalSession,
            OfflineSyncUxService.ResolveConnectivity(false, false, false, false, false, false));

        Assert.Equal(
            SyncConnectivityUxKind.RequiresLogin,
            OfflineSyncUxService.ResolveConnectivity(true, false, true, false, false, true));
    }

    [Fact]
    public void ServerBaseUrl_Mismatch_Hint_Is_Clear_Without_Hardcoding()
    {
        Assert.Null(OfflineSyncUxService.ServerBaseUrlMismatchHint(
            "http://192.168.1.10:5080", "http://192.168.1.10:5080/"));
        Assert.Null(OfflineSyncUxService.ServerBaseUrlMismatchHint(null, "http://x"));
        var hint = OfflineSyncUxService.ServerBaseUrlMismatchHint(
            "http://192.168.1.10:5080", "http://10.0.0.5:5080");
        Assert.NotNull(hint);
        Assert.Contains("URL", hint!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie=", hint!, StringComparison.OrdinalIgnoreCase);
    }

    // --- Logout / re-login ciclo ---

    [Fact]
    public async Task Logout_Then_Relogin_Restores_Sync_Eligibility_Keeps_Outbox()
    {
        await using var db = CreateContext();
        await SeedSessionAsync(db);
        var capture = CreateCapture(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        var clientOpId = created.Operation.ClientOperationId;

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var clear = await sessions.ClearUxSnapshotAsync();
        Assert.True(clear.Cleared);
        Assert.Null(await sessions.GetRawSessionAsync());
        Assert.Equal(1, await db.PendingOperations.CountAsync());
        Assert.Equal(clientOpId, (await db.PendingOperations.SingleAsync()).ClientOperationId);

        var devices = new FileDeviceIdStore(Path.Combine(_dir, "relogin"));
        var engine = new SyncEngine(db, sessions, devices, new ThrowingApi(), new StaticCookie("RegNeps.Auth=x"));
        var blocked = await engine.SyncAsync();
        Assert.False(blocked.Started);
        Assert.True(blocked.SessionMissingOrExpired);

        await sessions.UpsertUxSnapshotAsync(
            "user-a",
            "alice",
            "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080",
            TimeSpan.FromHours(72));

        var session = await sessions.GetValidSessionAsync();
        Assert.NotNull(session);
        Assert.Equal(1, await new OfflineSyncUxService(db).GetCountersAsync("user-a").ContinueWith(t => t.Result.Pending));
    }

    // --- Conflict no LWW + no retry ---

    [Fact]
    public async Task Conflict_Does_Not_Overwrite_Local_Nor_AutoRetry_Same_ClientOperationId()
    {
        await using var db = CreateContext();
        await SeedSessionAsync(db);
        var capture = CreateCapture(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "LOC", Neps = 30 });

        var op = await db.PendingOperations.SingleAsync();
        op.OperationType = OfflineOperationType.UpdateRecord;
        op.ExpectedConcurrencyStamp = "stale";
        created.Record.Telar = "LOC";
        created.Record.Neps = 30;
        await db.SaveChangesAsync();

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "conf"));
        var api = new ScriptedApi(
            req => new ClientSyncPushResponse
            {
                Results =
                [
                    new ClientSyncOperationResultDto
                    {
                        ClientOperationId = op.ClientOperationId,
                        Result = ClientSyncResultNames.Conflict,
                        ErrorCode = "CONFLICT",
                        ServerConcurrencyStamp = "new-stamp",
                        ServerSnapshot = JsonSerializer.SerializeToElement(new ClientNepRecordSnapshot
                        {
                            Id = created.Record.Id,
                            Telar = "SRV",
                            Neps = 99,
                            ConcurrencyStamp = "new-stamp"
                        }, ClientSyncJson.Options)
                    }
                ]
            },
            _ => new ClientSyncPullResponse { NextCursor = 0 });

        var engine = new SyncEngine(db, sessions, devices, api, new StaticCookie("RegNeps.Auth=x"));
        await engine.SyncAsync();

        op = await db.PendingOperations.SingleAsync();
        Assert.Equal(PendingOperationStatus.Conflict, op.Status);
        Assert.Equal("new-stamp", op.ConflictServerConcurrencyStamp);
        Assert.False(string.IsNullOrWhiteSpace(op.ConflictServerSnapshotJson));
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.Equal("LOC", local.Telar);
        Assert.Equal(30, local.Neps);

        api.PushCalls = 0;
        await engine.SyncAsync();
        Assert.Equal(0, api.PushCalls);

        var ux = await new OfflineSyncUxService(db).GetConflictsAsync("user-a");
        Assert.Single(ux);
        Assert.Equal("Requiere revisión", ux[0].ActionHint);
        Assert.DoesNotContain("Sincronizado", ux[0].ActionHint, StringComparison.OrdinalIgnoreCase);
    }

    // --- ServerRecordId / Duplicate / no doble LocalNepRecord ---

    [Fact]
    public async Task Accepted_Sets_ServerRecordId_Distinct_From_Local_No_Duplicate_Record()
    {
        await using var db = CreateContext();
        await SeedSessionAsync(db);
        var capture = CreateCapture(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        var localId = created.Record.Id;
        var serverId = Guid.NewGuid();

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "sid"));
        var api = new ScriptedApi(
            req => new ClientSyncPushResponse
            {
                Results =
                [
                    new ClientSyncOperationResultDto
                    {
                        ClientOperationId = req.Operations[0].ClientOperationId,
                        Result = ClientSyncResultNames.Accepted,
                        EntityId = serverId,
                        ConcurrencyStamp = "c1"
                    }
                ]
            },
            _ => new ClientSyncPullResponse { NextCursor = 0, HasMore = false });

        var engine = new SyncEngine(db, sessions, devices, api, new StaticCookie("RegNeps.Auth=x"));
        var run = await engine.SyncAsync();
        Assert.Equal(1, run.PushedAccepted);

        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
        var record = await db.LocalNepRecords.SingleAsync();
        Assert.Equal(localId, record.Id);
        Assert.Equal(serverId, record.ServerRecordId);
        Assert.NotEqual(localId, record.ServerRecordId);
        Assert.Equal(LocalSyncStatus.Synced, record.SyncStatus);
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Transient_Then_Retry_Same_ClientOperationId_No_Second_Logical_Op()
    {
        await using var db = CreateContext();
        await SeedSessionAsync(db);
        var capture = CreateCapture(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        var clientOpId = created.Operation.ClientOperationId;
        var serverId = Guid.NewGuid();

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "retry"));
        var attempt = 0;
        var api = new ScriptedApi(
            req =>
            {
                attempt++;
                if (attempt == 1)
                {
                    return new ClientSyncPushResponse
                    {
                        Results =
                        [
                            new ClientSyncOperationResultDto
                            {
                                ClientOperationId = clientOpId,
                                Result = ClientSyncResultNames.TransientError,
                                Message = "busy"
                            }
                        ]
                    };
                }

                return new ClientSyncPushResponse
                {
                    Results =
                    [
                        new ClientSyncOperationResultDto
                        {
                            ClientOperationId = clientOpId,
                            Result = ClientSyncResultNames.Accepted,
                            EntityId = serverId,
                            ConcurrencyStamp = "ok"
                        }
                    ]
                };
            },
            _ => new ClientSyncPullResponse { NextCursor = 0 });

        var engine = new SyncEngine(db, sessions, devices, api, new StaticCookie("RegNeps.Auth=x"));
        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);
        Assert.Equal(clientOpId, (await db.PendingOperations.SingleAsync()).ClientOperationId);
        Assert.Equal(1, await db.PendingOperations.CountAsync());

        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations.SingleAsync()).Status);
        Assert.Equal(clientOpId, (await db.PendingOperations.SingleAsync()).ClientOperationId);
        Assert.Equal(serverId, (await db.LocalNepRecords.SingleAsync()).ServerRecordId);
        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
        Assert.Equal(1, await db.PendingOperations.CountAsync());
    }

    [Fact]
    public async Task Two_CreateRecord_Calls_Produce_Two_Distinct_ClientOperationIds()
    {
        // Documenta: sin gate UI, dos Create lógicos = dos ops. La UI debe impedir doble toque.
        await using var db = CreateContext();
        await SeedSessionAsync(db);
        var capture = CreateCapture(db);
        var a = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        var b = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        Assert.NotEqual(a.Operation.ClientOperationId, b.Operation.ClientOperationId);
        Assert.Equal(2, await db.PendingOperations.CountAsync());
        Assert.Equal(2, await db.LocalNepRecords.CountAsync());
    }

    [Fact]
    public void SanitizeError_Never_Leaks_Secrets()
    {
        Assert.DoesNotContain("RegNeps.Auth=", OfflineSyncUxService.SanitizeError("cookie=RegNeps.Auth=secret")!);
        Assert.DoesNotContain("at RegNeps.", OfflineSyncUxService.SanitizeError("at RegNeps.OfflineStore.Sync.SyncEngine")!);
        Assert.DoesNotContain("Bearer", OfflineSyncUxService.SanitizeError("Authorization: Bearer xyz")!);
    }

    private sealed class ScriptedEngine : ISyncEngine
    {
        private readonly Func<CancellationToken, Task<SyncRunResult>> _fn;

        public ScriptedEngine(Func<CancellationToken, Task<SyncRunResult>> fn) => _fn = fn;

        public Task<SyncRunResult> SyncAsync(CancellationToken ct = default) => _fn(ct);

        public Task<SyncRunResult> PullAsync(CancellationToken ct = default) =>
            Task.FromResult(new SyncRunResult { Started = true, PullCompleted = true });
    }

    private sealed class ThrowingApi : ISyncApiClient
    {
        private readonly Exception? _ex;

        public ThrowingApi(Exception? ex = null) => _ex = ex;

        public Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPushRequest request, CancellationToken ct = default)
        {
            if (_ex is not null)
            {
                throw _ex;
            }

            return Task.FromResult(new ClientSyncPushResponse());
        }

        public Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPullRequest request, CancellationToken ct = default)
        {
            if (_ex is not null)
            {
                throw _ex;
            }

            return Task.FromResult(new ClientSyncPullResponse());
        }
    }

    private sealed class ScriptedApi : ISyncApiClient
    {
        private readonly Func<ClientSyncPushRequest, ClientSyncPushResponse> _push;
        private readonly Func<ClientSyncPullRequest, ClientSyncPullResponse> _pull;
        public int PushCalls;

        public ScriptedApi(
            Func<ClientSyncPushRequest, ClientSyncPushResponse> push,
            Func<ClientSyncPullRequest, ClientSyncPullResponse> pull)
        {
            _push = push;
            _pull = pull;
        }

        public Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPushRequest request, CancellationToken ct = default)
        {
            PushCalls++;
            return Task.FromResult(_push(request));
        }

        public Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPullRequest request, CancellationToken ct = default) =>
            Task.FromResult(_pull(request));
    }

    private sealed class StaticCookie : ISyncAuthCookieProvider
    {
        private readonly string? _cookie;

        public StaticCookie(string? cookie) => _cookie = cookie;

        public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default) =>
            Task.FromResult(_cookie);
    }
}
