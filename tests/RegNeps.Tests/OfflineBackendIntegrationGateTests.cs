using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RegNeps.Application.Permissions;
using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
using RegNeps.Domain.Services;
using RegNeps.Domain.Sync;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;
using RegNeps.Infrastructure.Sync;
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
/// FASE 2D.4.1 — Gate OfflineStore (SQLite) ↔ SyncAppService/backend (SQLite :memory:).
/// SQL Server E2E no disponible en este harness (documentado). Sin secretos ni cookies reales.
/// </summary>
public sealed class OfflineBackendIntegrationGateTests : IAsyncLifetime
{
    private readonly SqliteConnection _serverConn;
    private readonly DbContextOptions<RegNepsDbContext> _serverOptions;
    private TestDbFactory _serverFactory = null!;
    private Guid _userAId;
    private Guid _userBId;
    private Guid _adminId;
    private string _dir = null!;
    private string _offlineDbPath = null!;

    public OfflineBackendIntegrationGateTests()
    {
        _serverConn = new SqliteConnection("Data Source=:memory:");
        _serverConn.Open();
        _serverOptions = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(_serverConn)
            .Options;
    }

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-gate241-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _offlineDbPath = Path.Combine(_dir, "offline.db");

        _serverFactory = new TestDbFactory(_serverOptions);
        await using var db = _serverFactory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitializer.ApplySchemaPatchesAsync(db);
        await DbSeeder.SeedAsync(db);
        // FASE 2D.9: baseline de catálogos antes de los gates (evita +N logs en el primer Pull).
        await new SyncPersistence(_serverFactory, new AtomicNepRecordCreateStore(_serverFactory))
            .EnsureCatalogBaselineAsync();

        var hash = BCrypt.Net.BCrypt.HashPassword("Gate241TestOnly!");
        var a = new AppUser
        {
            Username = "gate_a",
            DisplayName = "Gate A",
            PasswordHash = hash,
            Role = AppUserRole.Operario,
            RoleCode = "Operario",
            IsActive = true
        };
        var b = new AppUser
        {
            Username = "gate_b",
            DisplayName = "Gate B",
            PasswordHash = hash,
            Role = AppUserRole.Operario,
            RoleCode = "Operario",
            IsActive = true
        };
        var admin = new AppUser
        {
            Username = "gate_admin",
            DisplayName = "Gate Admin",
            PasswordHash = hash,
            Role = AppUserRole.Admin,
            RoleCode = "Admin",
            IsActive = true
        };
        db.Users.AddRange(a, b, admin);
        await db.SaveChangesAsync();
        _userAId = a.Id;
        _userBId = b.Id;
        _adminId = admin.Id;

        await using var offline = CreateOfflineDb();
        await offline.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        _serverConn.Dispose();
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

        await Task.CompletedTask;
    }

    private LocalSyncDbContext CreateOfflineDb(params IInterceptor[] interceptors)
    {
        var b = new DbContextOptionsBuilder<LocalSyncDbContext>()
            .UseSqlite($"Data Source={_offlineDbPath}");
        if (interceptors.Length > 0)
        {
            b.AddInterceptors(interceptors);
        }

        return new LocalSyncDbContext(b.Options);
    }

    private SyncAppService CreateServerSync()
    {
        var db = _serverFactory.CreateDbContext();
        var matrix = new PermissionMatrix();
        var permissions = new PermissionService(
            new RolePermissionRepository(db),
            new RoleRepository(db),
            matrix);
        return new SyncAppService(
            new SyncPersistence(_serverFactory, new AtomicNepRecordCreateStore(_serverFactory)),
            permissions,
            NullLogger<SyncAppService>.Instance);
    }

    private RecordActor Actor(Guid userId, string username, string display, AppUserRole role, string roleCode) =>
        RecordActor.Create(userId.ToString(), username, display, role, false, null, roleCode, role is AppUserRole.Admin or AppUserRole.Supervisor);

    private async Task<(SyncEngine Engine, OfflineCaptureService Capture, OfflineSessionService Sessions, OfflineOperationsUxService Ops, BridgeApi Api)>
        BootOfflineAsync(
            LocalSyncDbContext offlineDb,
            Guid userId,
            string username,
            AppUserRole role,
            string roleCode,
            bool withCookie = true,
            BridgeApi? apiOverride = null)
    {
        await offlineDb.Database.ExecuteSqlRawAsync("DELETE FROM PendingOperations");
        await offlineDb.Database.ExecuteSqlRawAsync("DELETE FROM LocalNepRecords");
        await offlineDb.Database.ExecuteSqlRawAsync("DELETE FROM LocalSessions");
        await offlineDb.Database.ExecuteSqlRawAsync("DELETE FROM SyncStates");
        offlineDb.ChangeTracker.Clear();

        var sessions = new OfflineSessionService(offlineDb, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-" + Guid.NewGuid().ToString("N")[..8]));
        var capture = new OfflineCaptureService(offlineDb, sessions, devices);
        var ops = new OfflineOperationsUxService(offlineDb);
        var server = CreateServerSync();
        var actor = Actor(userId, username, username, role, roleCode);
        var api = apiOverride ?? new BridgeApi(server, () => actor);
        var engine = new SyncEngine(
            offlineDb,
            sessions,
            devices,
            api,
            new StaticCookie(withCookie ? "RegNeps.Auth=gate-test" : null));

        await sessions.UpsertUxSnapshotAsync(
            userId.ToString(),
            username,
            roleCode,
            [OfflineStoreConstants.CaptureRecordsPermission, "EditRecords", "DeleteRecords"],
            "http://localhost:5080",
            TimeSpan.FromHours(72));
        offlineDb.SyncStates.Add(new SyncState { Id = 1, DeviceId = "gate-dev", LastPulledSequence = 0 });
        await offlineDb.SaveChangesAsync();
        return (engine, capture, sessions, ops, api);
    }

    [Fact]
    public async Task Gate_Create_Offline_Push_Pull_Single_Record_And_ChangeLog()
    {
        await using var offline = CreateOfflineDb();
        var (engine, capture, _, _, _) = await BootOfflineAsync(offline, _userAId, "gate_a", AppUserRole.Operario, "Operario");

        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = "G1",
            Neps = 18,
            Tela = "Denim",
            Turno = "A"
        });

        Assert.Equal(PendingOperationStatus.Pending, created.Operation.Status);
        Assert.False(string.IsNullOrWhiteSpace(created.Operation.ClientOperationId));
        Assert.False(string.IsNullOrWhiteSpace(created.Record.CaptureSessionId));
        Assert.Equal(18, created.Record.Neps);
        Assert.Equal(AlertLevel.Ok.ToDisplayLabel(), created.QualityLabel);
        Assert.Equal(NepsConstants.TestLengthM, 0.09, 6);

        var run = await engine.SyncAsync();
        Assert.True(run.Started);
        Assert.Equal(1, run.PushedAccepted);
        Assert.True(run.PullCompleted);

        var local = await offline.LocalNepRecords.SingleAsync();
        Assert.NotNull(local.ServerRecordId);
        Assert.NotEqual(local.Id, local.ServerRecordId);
        Assert.Equal(PendingOperationStatus.Synced, (await offline.PendingOperations.SingleAsync()).Status);
        Assert.Equal(LocalSyncStatus.Synced, local.SyncStatus);

        await using var server = _serverFactory.CreateDbContext();
        Assert.Equal(1, await server.NepRecords.CountAsync());
        Assert.Equal(1, await server.SyncChangeLogs.CountAsync(c =>
            c.EntityType == SyncConstants.EntityNepRecord));
        var nep = await server.NepRecords.SingleAsync();
        Assert.Equal(local.ServerRecordId, nep.Id);
        Assert.Equal(_userAId.ToString(), nep.CreatedByUserId);
        Assert.Equal(created.Operation.ClientOperationId, nep.ClientOperationId);
        Assert.Equal(18, nep.Neps);
        Assert.Equal("OK", nep.GetAlertLevel().ToDisplayLabel());

        var log = await server.SyncChangeLogs.SingleAsync(c =>
            c.EntityType == SyncConstants.EntityNepRecord);
        Assert.Equal(SyncConstants.ChangeRecordUpserted, log.ChangeType);
        Assert.Equal(nep.Id, log.EntityId);
        Assert.True((await offline.SyncStates.SingleAsync()).LastPulledSequence >= log.Sequence);
    }

    [Fact]
    public async Task Gate_Duplicate_Retry_After_Local_Persist_Fail_Recovers_ServerRecordId()
    {
        var failOnce = new FailNextSaveInterceptor();
        await using (var offline = CreateOfflineDb(failOnce))
        {
            var (engine, capture, _, _, _) = await BootOfflineAsync(offline, _userAId, "gate_a", AppUserRole.Operario, "Operario");
            await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "DUP", Neps = 18 });
            failOnce.Armed = true;
            var run1 = await engine.SyncAsync();
            // Persistencia local falló tras Accepted en servidor → Outbox puede quedar Pending.
            Assert.True(run1.Started);
        }

        await using var offline2 = CreateOfflineDb();
        var sessions = new OfflineSessionService(offline2, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-dup"));
        var server = CreateServerSync();
        var api = new BridgeApi(server, () => Actor(_userAId, "gate_a", "gate_a", AppUserRole.Operario, "Operario"));
        var engine2 = new SyncEngine(offline2, sessions, devices, api, new StaticCookie("RegNeps.Auth=x"));
        Assert.NotNull(await sessions.GetValidSessionAsync());

        var beforeServerCount = await _serverFactory.CreateDbContext().NepRecords.CountAsync();
        Assert.True(beforeServerCount >= 1, "El servidor debió aceptar el Create antes del fallo local.");

        var opBefore = await offline2.PendingOperations.SingleAsync();
        if (opBefore.Status == PendingOperationStatus.Pending)
        {
            var run2 = await engine2.SyncAsync();
            Assert.True(run2.PushedDuplicate + run2.PushedAccepted >= 1);
        }
        // Si Pull del primer ciclo ya enlazó (Synced), también es recuperación válida sin segundo Create.

        var local = await offline2.LocalNepRecords.SingleAsync();
        Assert.NotNull(local.ServerRecordId);
        Assert.Equal(PendingOperationStatus.Synced, (await offline2.PendingOperations.SingleAsync()).Status);

        await using var serverDb = _serverFactory.CreateDbContext();
        Assert.Equal(beforeServerCount, await serverDb.NepRecords.CountAsync());
        Assert.Equal(beforeServerCount, await serverDb.SyncChangeLogs.CountAsync(c => c.ChangeType == SyncConstants.ChangeRecordUpserted));
        Assert.Equal(1, await offline2.PendingOperations.CountAsync());
        Assert.Equal(1, await offline2.LocalNepRecords.CountAsync());
    }

    [Fact]
    public async Task Gate_Update_NewStamp_ChangeLog_And_Pull()
    {
        await using var offline = CreateOfflineDb();
        var (engine, capture, _, _, _) = await BootOfflineAsync(offline, _adminId, "gate_admin", AppUserRole.Admin, "Admin");
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "U1", Neps = 18 });
        await engine.SyncAsync();

        var local = await offline.LocalNepRecords.SingleAsync();
        var stamp = local.ConcurrencyStamp!;
        var serverId = local.ServerRecordId!.Value;

        var op = await offline.PendingOperations.SingleAsync();
        // Nueva operación Update (motor; UI Create-only).
        var updateOp = new PendingOperation
        {
            Id = Guid.NewGuid(),
            ClientOperationId = Guid.NewGuid().ToString("N"),
            OperationType = OfflineOperationType.UpdateRecord,
            PayloadJson = JsonSerializer.Serialize(new
            {
                entityId = serverId,
                telar = "U1-EDIT",
                neps = 19,
                tela = "Denim",
                loteTrama = "L1",
                turno = "A",
                operario = "op",
                lineaProduccion = "",
                observacion = ""
            }, ClientSyncJson.Options),
            CreatedAtUtc = DateTime.UtcNow,
            Status = PendingOperationStatus.Pending,
            UserId = _adminId.ToString(),
            DeviceId = op.DeviceId,
            LocalNepRecordId = local.Id,
            TargetServerRecordId = serverId,
            ExpectedConcurrencyStamp = stamp
        };
        local.Telar = "U1-EDIT";
        local.Neps = 19;
        local.SyncStatus = LocalSyncStatus.PendingSync;
        offline.PendingOperations.Add(updateOp);
        await offline.SaveChangesAsync();

        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.Synced, (await offline.PendingOperations.SingleAsync(o => o.Id == updateOp.Id)).Status);

        await using var server = _serverFactory.CreateDbContext();
        var nep = await server.NepRecords.SingleAsync(r => r.Id == serverId);
        Assert.Equal("U1-EDIT", nep.Telar);
        Assert.Equal(19, nep.Neps);
        Assert.NotEqual(stamp, nep.ConcurrencyStamp);
        Assert.NotNull(nep.UpdatedAt);
        Assert.True(await server.SyncChangeLogs.CountAsync(c => c.EntityId == serverId) >= 2);

        var pulled = await offline.LocalNepRecords.SingleAsync();
        Assert.Equal("U1-EDIT", pulled.Telar);
        Assert.Equal(nep.ConcurrencyStamp, pulled.ConcurrencyStamp);
    }

    [Fact]
    public async Task Gate_Conflict_No_Lww_Ux_Requires_Review_No_ChangeLog_For_Rejected()
    {
        // Servidor: admin crea y actualiza; cliente offline intenta Update con stamp stale.
        var serverSync = CreateServerSync();
        var admin = Actor(_adminId, "gate_admin", "gate_admin", AppUserRole.Admin, "Admin");
        var create = await serverSync.PushAsync(new SyncPushRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "srv-device",
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = Guid.NewGuid().ToString("N"),
                    OperationType = SyncConstants.OperationCreateRecord,
                    Payload = JsonSerializer.SerializeToElement(new SyncCreateRecordPayload
                    {
                        Telar = "C1",
                        Neps = 18
                    })
                }
            ]
        }, admin, "c1");
        var entityId = create.Results[0].EntityId!.Value;
        var stamp1 = create.Results[0].ConcurrencyStamp!;

        await serverSync.PushAsync(new SyncPushRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "srv-device",
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = Guid.NewGuid().ToString("N"),
                    OperationType = SyncConstants.OperationUpdateRecord,
                    ExpectedConcurrencyStamp = stamp1,
                    Payload = JsonSerializer.SerializeToElement(new SyncUpdateRecordPayload
                    {
                        EntityId = entityId,
                        Telar = "SERVER",
                        Neps = 45
                    })
                }
            ]
        }, admin, "c2");

        var logsBefore = await _serverFactory.CreateDbContext().SyncChangeLogs
            .CountAsync(c => c.EntityType == SyncConstants.EntityNepRecord);

        await using var offline = CreateOfflineDb();
        var (engine, _, _, opsUx, _) = await BootOfflineAsync(offline, _adminId, "gate_admin", AppUserRole.Admin, "Admin");
        var localId = Guid.NewGuid();
        var clientOp = Guid.NewGuid().ToString("N");
        offline.LocalNepRecords.Add(new LocalNepRecord
        {
            Id = localId,
            ClientOperationId = "seed-" + clientOp,
            Telar = "LOCAL",
            Neps = 18,
            UserId = _adminId.ToString(),
            ServerRecordId = entityId,
            ConcurrencyStamp = stamp1, // stale
            SyncStatus = LocalSyncStatus.PendingSync,
            CreatedAtUtc = DateTime.UtcNow
        });
        offline.PendingOperations.Add(new PendingOperation
        {
            Id = Guid.NewGuid(),
            ClientOperationId = clientOp,
            OperationType = OfflineOperationType.UpdateRecord,
            PayloadJson = JsonSerializer.Serialize(new
            {
                entityId,
                telar = "LOCAL",
                neps = 18
            }, ClientSyncJson.Options),
            CreatedAtUtc = DateTime.UtcNow,
            Status = PendingOperationStatus.Pending,
            UserId = _adminId.ToString(),
            DeviceId = "d",
            LocalNepRecordId = localId,
            TargetServerRecordId = entityId,
            ExpectedConcurrencyStamp = stamp1
        });
        await offline.SaveChangesAsync();

        await engine.SyncAsync();
        var op = await offline.PendingOperations.SingleAsync(o => o.ClientOperationId == clientOp);
        Assert.Equal(PendingOperationStatus.Conflict, op.Status);
        Assert.False(string.IsNullOrWhiteSpace(op.ConflictServerSnapshotJson));
        Assert.False(string.IsNullOrWhiteSpace(op.ConflictServerConcurrencyStamp));
        var local = await offline.LocalNepRecords.SingleAsync(r => r.Id == localId);
        Assert.Equal("LOCAL", local.Telar); // no LWW
        Assert.Equal(18, local.Neps);

        // Conflict no debe emitir ChangeLog NepRecord; catálogos baseline ya existían.
        Assert.Equal(logsBefore, await _serverFactory.CreateDbContext().SyncChangeLogs
            .CountAsync(c => c.EntityType == SyncConstants.EntityNepRecord));

        var detail = await opsUx.GetDetailAsync(_adminId.ToString(), op.Id);
        Assert.NotNull(detail);
        Assert.Equal(OfflineOperationUxAction.RequiresReview, detail!.PrimaryAction);
        Assert.Contains("revisión", detail.ActionHint, StringComparison.OrdinalIgnoreCase);

        await engine.SyncAsync();
        Assert.Equal(PendingOperationStatus.Conflict, (await offline.PendingOperations.SingleAsync(o => o.Id == op.Id)).Status);
    }

    [Fact]
    public async Task Gate_Delete_Tombstone_No_Resurrection()
    {
        await using var offline = CreateOfflineDb();
        var (engine, capture, _, _, _) = await BootOfflineAsync(offline, _adminId, "gate_admin", AppUserRole.Admin, "Admin");
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "DEL", Neps = 18 });
        await engine.SyncAsync();
        var local = await offline.LocalNepRecords.SingleAsync();
        var serverId = local.ServerRecordId!.Value;
        var stamp = local.ConcurrencyStamp!;
        var deviceId = (await offline.PendingOperations.FirstAsync()).DeviceId;

        offline.PendingOperations.Add(new PendingOperation
        {
            Id = Guid.NewGuid(),
            ClientOperationId = Guid.NewGuid().ToString("N"),
            OperationType = OfflineOperationType.DeleteRecord,
            PayloadJson = JsonSerializer.Serialize(new { entityId = serverId }, ClientSyncJson.Options),
            CreatedAtUtc = DateTime.UtcNow,
            Status = PendingOperationStatus.Pending,
            UserId = _adminId.ToString(),
            DeviceId = deviceId,
            LocalNepRecordId = local.Id,
            TargetServerRecordId = serverId,
            ExpectedConcurrencyStamp = stamp
        });
        await offline.SaveChangesAsync();

        await engine.SyncAsync();
        Assert.True((await offline.LocalNepRecords.SingleAsync()).IsDeleted);

        await using var server = _serverFactory.CreateDbContext();
        Assert.True(await server.SyncChangeLogs.AnyAsync(c =>
            c.EntityId == serverId && c.ChangeType == SyncConstants.ChangeRecordDeleted));
        Assert.False(await server.NepRecords.AnyAsync(r => r.Id == serverId));

        // Pull repetido no resucita
        await engine.SyncAsync();
        Assert.True((await offline.LocalNepRecords.SingleAsync()).IsDeleted);
        Assert.Equal(1, await offline.LocalNepRecords.CountAsync());
    }

    [Fact]
    public async Task Gate_Auth_Cookie_401_403_Network_Differentiated()
    {
        await using var offline = CreateOfflineDb();
        var (engineNoCookie, capture, _, _, _) =
            await BootOfflineAsync(offline, _userAId, "gate_a", AppUserRole.Operario, "Operario", withCookie: false);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "A1", Neps = 18 });
        var noCookie = await engineNoCookie.SyncAsync();
        Assert.True(noCookie.AuthRequired);
        Assert.False(noCookie.Started);
        Assert.Equal(PendingOperationStatus.Pending, (await offline.PendingOperations.SingleAsync()).Status);

        // 401 transport
        var sessions = new OfflineSessionService(offline, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "auth"));
        var throw401 = new ThrowingBridge(new SyncTransportException(
            SyncTransportFailureKind.Unauthorized, "Sesión de servidor no válida (401).", 401));
        var eng401 = new SyncEngine(offline, sessions, devices, throw401, new StaticCookie("RegNeps.Auth=x"));
        var r401 = await eng401.SyncAsync();
        Assert.True(r401.AuthRequired);
        var fb401 = OfflineSyncUxService.MapRunResult(r401);
        Assert.True(fb401.RequiresLogin);

        var throw403 = new ThrowingBridge(new SyncTransportException(
            SyncTransportFailureKind.Forbidden, "Acceso denegado (403).", 403));
        var eng403 = new SyncEngine(offline, sessions, devices, throw403, new StaticCookie("RegNeps.Auth=x"));
        var r403 = await eng403.SyncAsync();
        Assert.False(r403.AuthRequired);
        Assert.Equal(SyncResultUserMessages.Forbidden, OfflineSyncUxService.MapRunResult(r403).Message);

        var throwNet = new ThrowingBridge(new SyncTransportException(
            SyncTransportFailureKind.Network, "Error de red al contactar el servidor."));
        var engNet = new SyncEngine(offline, sessions, devices, throwNet, new StaticCookie("RegNeps.Auth=x"));
        var rNet = await engNet.SyncAsync();
        Assert.False(rNet.AuthRequired);
        Assert.True(rNet.PushedTransient >= 1);
        Assert.Equal(PendingOperationStatus.Pending, (await offline.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Gate_Multiusuario_B_Does_Not_Push_A_Outbox()
    {
        await using var offline = CreateOfflineDb();
        // Sesión A + pending A
        var sessions = new OfflineSessionService(offline, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "multi"));
        var capture = new OfflineCaptureService(offline, sessions, devices);
        var opsUx = new OfflineOperationsUxService(offline);
        offline.SyncStates.Add(new SyncState { Id = 1, DeviceId = "multi", LastPulledSequence = 0 });
        await offline.SaveChangesAsync();

        await sessions.UpsertUxSnapshotAsync(
            _userAId.ToString(), "gate_a", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080");
        var createdA = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "A", Neps = 18 });
        var clientA = createdA.Operation.ClientOperationId;

        await sessions.ClearUxSnapshotAsync();
        Assert.Equal(1, await offline.PendingOperations.CountAsync());

        await sessions.UpsertUxSnapshotAsync(
            _userBId.ToString(), "gate_b", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080");
        var createdB = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "B", Neps = 19 });

        var listB = await opsUx.ListAsync(_userBId.ToString());
        Assert.Single(listB);
        Assert.Equal(createdB.Operation.Id, listB[0].OperationId);
        Assert.Null(await opsUx.GetDetailAsync(_userBId.ToString(), createdA.Operation.Id));

        var server = CreateServerSync();
        var api = new BridgeApi(server, () => Actor(_userBId, "gate_b", "gate_b", AppUserRole.Operario, "Operario"));
        var engineB = new SyncEngine(offline, sessions, devices, api, new StaticCookie("RegNeps.Auth=x"));
        await engineB.SyncAsync();

        Assert.DoesNotContain(clientA, api.PushedClientOps);
        Assert.Contains(createdB.Operation.ClientOperationId, api.PushedClientOps);
        Assert.Equal(PendingOperationStatus.Pending,
            (await offline.PendingOperations.SingleAsync(o => o.ClientOperationId == clientA)).Status);
    }

    [Fact]
    public async Task Gate_Restart_Preserves_Pending_Error_Conflict_Synced()
    {
        await using (var offline = CreateOfflineDb())
        {
            var (_, capture, _, _, _) = await BootOfflineAsync(offline, _userAId, "gate_a", AppUserRole.Operario, "Operario");
            await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
            await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "2", Neps = 11 });
            await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "3", Neps = 12 });
            await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "4", Neps = 13 });
            var ops = await offline.PendingOperations.OrderBy(o => o.CreatedAtUtc).ToListAsync();
            ops[1].Status = PendingOperationStatus.Synced;
            ops[2].Status = PendingOperationStatus.SyncError;
            ops[2].LastServerErrorCode = "FORBIDDEN";
            ops[3].Status = PendingOperationStatus.Conflict;
            ops[3].ConflictServerSnapshotJson = """{"telar":"S","neps":1}""";
            ops[3].ConflictServerConcurrencyStamp = "x";
            await offline.SaveChangesAsync();
        }

        await using var reopened = CreateOfflineDb();
        var ux = new OfflineSyncUxService(reopened);
        var c = await ux.GetCountersAsync(_userAId.ToString());
        Assert.Equal(1, c.Pending);
        Assert.Equal(1, c.Synced);
        Assert.Equal(1, c.SyncError);
        Assert.Equal(1, c.Conflict);
        var conflict = await reopened.PendingOperations.SingleAsync(o => o.Status == PendingOperationStatus.Conflict);
        Assert.Equal("x", conflict.ConflictServerConcurrencyStamp);
        Assert.False(string.IsNullOrWhiteSpace(conflict.ConflictServerSnapshotJson));
    }

    [Fact]
    public async Task Gate_Cursor_Monotonic_And_Pull_Idempotent()
    {
        await using var offline = CreateOfflineDb();
        var (engine, capture, _, _, _) = await BootOfflineAsync(offline, _userAId, "gate_a", AppUserRole.Operario, "Operario");
        Assert.Equal(0, (await offline.SyncStates.SingleAsync()).LastPulledSequence);

        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "C1", Neps = 18 });
        await engine.SyncAsync();
        var cursor1 = (await offline.SyncStates.SingleAsync()).LastPulledSequence;
        Assert.True(cursor1 > 0);

        await engine.SyncAsync();
        var cursor2 = (await offline.SyncStates.SingleAsync()).LastPulledSequence;
        Assert.Equal(cursor1, cursor2);
        Assert.Equal(1, await offline.LocalNepRecords.CountAsync());

        await using var server = _serverFactory.CreateDbContext();
        Assert.Equal(1, await server.NepRecords.CountAsync());
    }

    [Fact]
    public async Task Gate_Neps_Quality_Preserved_Offline_To_Backend()
    {
        await using var offline = CreateOfflineDb();
        var (engine, capture, _, _, _) = await BootOfflineAsync(offline, _userAId, "gate_a", AppUserRole.Operario, "Operario");

        var cases = new (double Q, string Label)[]
        {
            (18, AlertLevel.Ok.ToDisplayLabel()),
            (19, AlertLevel.Mention.ToDisplayLabel()),
            (45, AlertLevel.Mention.ToDisplayLabel()),
            (46, AlertLevel.CriticalAdjustment.ToDisplayLabel()),
            (54, AlertLevel.CriticalAdjustment.ToDisplayLabel()),
            (55, AlertLevel.SecondQuality.ToDisplayLabel())
        };

        foreach (var (q, label) in cases)
        {
            var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
            {
                Telar = $"Q{q}",
                Neps = q
            });
            Assert.Equal(label, created.QualityLabel);
            Assert.Equal(q / NepsConstants.TestLengthM, created.Record.MtsCalculados, 6);
        }

        await engine.SyncAsync();

        await using var server = _serverFactory.CreateDbContext();
        Assert.Equal(6, await server.NepRecords.CountAsync());
        foreach (var (q, label) in cases)
        {
            var nep = await server.NepRecords.SingleAsync(r => r.Telar == $"Q{q}");
            Assert.Equal(q, nep.Neps);
            Assert.Equal(label, nep.GetAlertLevel().ToDisplayLabel());
            Assert.Equal(AlertEvaluator.GetLevel(q), AlertEvaluator.GetLevel(nep.Neps));
        }
    }

    [Fact]
    public async Task Gate_Client_Atomicity_Rollback_Leaves_No_Orphan()
    {
        await using var offline = CreateOfflineDb(new FailNextSaveInterceptor { Armed = true, FailOnPendingOperations = true });
        var sessions = new OfflineSessionService(offline, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "atom"));
        var capture = new OfflineCaptureService(offline, sessions, devices);
        await sessions.UpsertUxSnapshotAsync(
            _userAId.ToString(), "gate_a", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080");
        offline.SyncStates.Add(new SyncState { Id = 1, DeviceId = "d" });
        await offline.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "X", Neps = 18 }));

        Assert.Equal(0, await offline.LocalNepRecords.CountAsync());
        Assert.Equal(0, await offline.PendingOperations.CountAsync());
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }

    /// <summary>Adaptador in-process: DTOs OfflineStore ↔ SyncAppService (sin HTTP/cookies reales).</summary>
    private sealed class BridgeApi : ISyncApiClient
    {
        private readonly SyncAppService _sync;
        private readonly Func<RecordActor> _actor;
        public List<string> PushedClientOps { get; } = [];

        public BridgeApi(SyncAppService sync, Func<RecordActor> actor)
        {
            _sync = sync;
            _actor = actor;
        }

        public async Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl,
            string? cookieHeader,
            ClientSyncPushRequest request,
            CancellationToken ct = default)
        {
            var mapped = new SyncPushRequest
            {
                ProtocolVersion = request.ProtocolVersion,
                DeviceId = request.DeviceId,
                Operations = request.Operations.Select(o =>
                {
                    PushedClientOps.Add(o.ClientOperationId);
                    return new SyncOperationDto
                    {
                        ClientOperationId = o.ClientOperationId,
                        OperationType = o.OperationType,
                        CaptureSessionId = o.CaptureSessionId,
                        ExpectedConcurrencyStamp = o.ExpectedConcurrencyStamp,
                        ClientCreatedAtUtc = o.ClientCreatedAtUtc,
                        Payload = o.Payload
                    };
                }).ToList()
            };

            var response = await _sync.PushAsync(mapped, _actor(), "gate-241", ct);
            return new ClientSyncPushResponse
            {
                ProtocolVersion = response.ProtocolVersion,
                ServerTimeUtc = response.ServerTimeUtc,
                Results = response.Results.Select(r => new ClientSyncOperationResultDto
                {
                    ClientOperationId = r.ClientOperationId,
                    Result = r.Result,
                    ErrorCode = r.ErrorCode,
                    Message = r.Message,
                    EntityId = r.EntityId,
                    ConcurrencyStamp = r.ConcurrencyStamp,
                    QualityLabel = r.QualityLabel,
                    ChangeSequence = r.ChangeSequence,
                    ServerConcurrencyStamp = r.ServerConcurrencyStamp,
                    ServerSnapshot = r.ServerSnapshot
                }).ToList()
            };
        }

        public async Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl,
            string? cookieHeader,
            ClientSyncPullRequest request,
            CancellationToken ct = default)
        {
            var response = await _sync.PullAsync(new SyncPullRequest
            {
                ProtocolVersion = request.ProtocolVersion,
                DeviceId = request.DeviceId,
                Cursor = request.Cursor,
                PageSize = request.PageSize
            }, _actor(), "gate-241", ct);

            return new ClientSyncPullResponse
            {
                ProtocolVersion = response.ProtocolVersion,
                ServerTimeUtc = response.ServerTimeUtc,
                NextCursor = response.NextCursor,
                HasMore = response.HasMore,
                Changes = response.Changes.Select(c => new ClientSyncChangeDto
                {
                    Sequence = c.Sequence,
                    EntityType = c.EntityType,
                    EntityId = c.EntityId,
                    ChangeType = c.ChangeType,
                    OccurredAtUtc = c.OccurredAtUtc,
                    Payload = c.Payload
                }).ToList()
            };
        }
    }

    private sealed class ThrowingBridge : ISyncApiClient
    {
        private readonly Exception _ex;
        public ThrowingBridge(Exception ex) => _ex = ex;

        public Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPushRequest request, CancellationToken ct = default) =>
            throw _ex;

        public Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPullRequest request, CancellationToken ct = default) =>
            throw _ex;
    }

    private sealed class StaticCookie : ISyncAuthCookieProvider
    {
        private readonly string? _c;
        public StaticCookie(string? c) => _c = c;
        public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default) =>
            Task.FromResult(_c);
    }

    private sealed class FailNextSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool FailOnPendingOperations { get; set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            MaybeThrow(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeThrow(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void MaybeThrow(DbContext? ctx)
        {
            if (!Armed || ctx is null)
            {
                return;
            }

            if (FailOnPendingOperations)
            {
                if (ctx.ChangeTracker.Entries<PendingOperation>().Any(e => e.State == EntityState.Added))
                {
                    Armed = false;
                    throw new InvalidOperationException("Simulated fail on PendingOperation insert.");
                }

                return;
            }

            // Fallar al persistir resultado de Push (Synced).
            if (ctx.ChangeTracker.Entries<PendingOperation>()
                .Any(e => e.State == EntityState.Modified
                          && e.Entity.Status == PendingOperationStatus.Synced))
            {
                Armed = false;
                throw new InvalidOperationException("Simulated fail after server Accepted.");
            }
        }
    }
}
