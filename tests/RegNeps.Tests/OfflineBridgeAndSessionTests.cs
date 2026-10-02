using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.OfflineStore;
using RegNeps.OfflineStore.Bridge;
using RegNeps.OfflineStore.Device;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;

namespace RegNeps.Tests;

public class OfflineBridgeAndSessionTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-bridge-tests-" + Guid.NewGuid().ToString("N"));
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

    private static OfflineSessionService CreateSessions(LocalSyncDbContext db) =>
        new(db, new MemorySecureAuthMaterialStore());

    [Fact]
    public async Task LocalSession_Save_And_Read()
    {
        await using var db = CreateContext();
        var sessions = CreateSessions(db);

        await sessions.UpsertUxSnapshotAsync(
            "user-a", "alice", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://192.168.1.10:5080",
            OfflineStoreConstants.DefaultSessionTtl);

        var session = await sessions.GetValidSessionAsync();
        Assert.NotNull(session);
        Assert.Equal("user-a", session!.UserId);
        Assert.Equal("alice", session.Username);
        Assert.Equal("Operario", session.RoleCode);
        Assert.Contains("CaptureRecords", session.PermissionsCsv);
        Assert.Equal("http://192.168.1.10:5080", session.ServerBaseUrl);
        Assert.False(session.HasSecureAuthMaterial);
    }

    [Fact]
    public async Task LocalSession_Ttl_Valid_Within_72h()
    {
        await using var db = CreateContext();
        var sessions = CreateSessions(db);
        await sessions.UpsertUxSnapshotAsync(
            "u1", "u1", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            null,
            TimeSpan.FromHours(72));

        var session = await sessions.GetValidSessionAsync();
        Assert.NotNull(session);
        Assert.True(session!.ExpiresAtUtc > DateTime.UtcNow.AddHours(71));
        Assert.True(OfflineSessionService.IsWithinTtl(session));
    }

    [Fact]
    public async Task LocalSession_Ttl_Expired_Returns_Null()
    {
        await using var db = CreateContext();
        var sessions = CreateSessions(db);
        await sessions.UpsertUxSnapshotAsync(
            "u1", "u1", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            null,
            TimeSpan.Zero);

        Assert.Null(await sessions.GetValidSessionAsync());
        var raw = await sessions.GetRawSessionAsync();
        Assert.NotNull(raw);
        Assert.False(OfflineSessionService.IsWithinTtl(raw!));
    }

    [Fact]
    public async Task LocalSession_Does_Not_Store_Password_Or_Cookie_Columns()
    {
        var names = OfflineSessionService.LocalSessionPropertyNames();
        Assert.DoesNotContain(names, n => n.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("Token", StringComparison.OrdinalIgnoreCase));

        await using var db = CreateContext();
        var sessions = CreateSessions(db);
        await sessions.UpsertUxSnapshotAsync("u1", "u1", "Operario", ["CaptureRecords"], null);

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
        Assert.DoesNotContain(columns, c => c.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, c => c.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalSession_Rejects_Cookie_Like_Secure_Material()
    {
        await using var db = CreateContext();
        var sessions = CreateSessions(db);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sessions.UpsertUxSnapshotAsync(
                "u1", "u1", "Operario", ["CaptureRecords"], null,
                secureAuthMaterial: "RegNeps.Auth=abc123; path=/"));
    }

    [Fact]
    public async Task DeviceId_Is_Persistent_Guid()
    {
        var store = new FileDeviceIdStore(Path.Combine(_dir, "device-bridge"));
        var a = await store.GetOrCreateAsync();
        var b = await store.GetOrCreateAsync();
        Assert.Equal(a, b);
        Assert.True(Guid.TryParse(a, out _));
    }

    [Fact]
    public async Task Logout_Clears_Session_But_Retains_PendingOperations()
    {
        await using var db = CreateContext();
        var secure = new MemorySecureAuthMaterialStore();
        var sessions = new OfflineSessionService(db, secure);
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev-logout"));
        var capture = new OfflineCaptureService(db, sessions, devices);

        await sessions.UpsertUxSnapshotAsync(
            "user-1", "op", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            null);

        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });
        Assert.Equal(1, await db.PendingOperations.CountAsync());

        var clear = await sessions.ClearUxSnapshotAsync();
        Assert.True(clear.Cleared);
        Assert.Equal(1, clear.PendingOperationsRetained);
        Assert.Null(await sessions.GetRawSessionAsync());
        Assert.Equal(1, await db.PendingOperations.CountAsync());
        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
    }

    [Fact]
    public void Bridge_Valid_Message_Roundtrip()
    {
        var req = OfflineBridgeCodec.CreateRequest(
            OfflineBridgeActions.SessionGet,
            new { },
            "req-1");
        Assert.Equal(1, req.ProtocolVersion);
        Assert.Equal("req-1", req.RequestId);

        var url = OfflineBridgeCodec.BuildInvokeUrl(req);
        Assert.StartsWith("regneps-bridge://invoke?", url);
        Assert.True(OfflineBridgeCodec.TryParseInvokeUrl(url, out var parsed, out var err));
        Assert.Null(err);
        Assert.NotNull(parsed);
        Assert.Equal("req-1", parsed!.RequestId);
        Assert.Equal(OfflineBridgeActions.SessionGet, parsed.Action);
        Assert.Null(OfflineBridgeCodec.ValidateRequest(parsed));
    }

    [Fact]
    public void Bridge_Incompatible_ProtocolVersion()
    {
        var env = new OfflineBridgeEnvelope
        {
            ProtocolVersion = 99,
            MessageType = OfflineBridgeConstants.MessageTypeRequest,
            RequestId = "r1",
            Action = OfflineBridgeActions.SessionGet
        };
        Assert.Equal(OfflineBridgeErrorCodes.IncompatibleProtocol, OfflineBridgeCodec.ValidateRequest(env));
    }

    [Fact]
    public void Bridge_RequestId_Required()
    {
        Assert.False(OfflineBridgeCodec.TryParseEnvelope(
            """{"protocolVersion":1,"messageType":"request","action":"offline.session.get"}""",
            out _,
            out var err));
        Assert.Equal(OfflineBridgeErrorCodes.InvalidEnvelope, err);
    }

    [Fact]
    public void Bridge_Unknown_And_Unauthorized_Actions()
    {
        var unknown = OfflineBridgeCodec.CreateRequest("offline.totally.unknown", new { }, "r1");
        Assert.Equal(OfflineBridgeErrorCodes.UnauthorizedAction, OfflineBridgeCodec.ValidateRequest(unknown));

        var sync = OfflineBridgeCodec.CreateRequest("offline.sync.push", new { }, "r2");
        Assert.Equal(OfflineBridgeErrorCodes.UnauthorizedAction, OfflineBridgeCodec.ValidateRequest(sync));
    }

    [Fact]
    public async Task Bridge_Invalid_Payload_And_Forbidden_Fields()
    {
        var handlers = new FakeHandlers();
        var processor = new OfflineBridgeProcessor(handlers);

        var bad = OfflineBridgeCodec.CreateRequest(OfflineBridgeActions.SessionSave, new { }, "r-bad");
        var badResp = await processor.ProcessAsync(bad);
        Assert.False(badResp.Ok);
        Assert.Equal(OfflineBridgeErrorCodes.InvalidPayload, badResp.ErrorCode);

        var forbidden = OfflineBridgeCodec.CreateRequest(
            OfflineBridgeActions.SessionSave,
            new { userId = "u1", username = "x", password = "secret" },
            "r-pwd");
        var forbiddenResp = await processor.ProcessAsync(forbidden);
        Assert.False(forbiddenResp.Ok);
        Assert.Equal(OfflineBridgeErrorCodes.ForbiddenField, forbiddenResp.ErrorCode);
        Assert.Equal(0, handlers.SaveCount);
    }

    [Fact]
    public async Task Bridge_No_Arbitrary_Execution_Only_Whitelist()
    {
        var handlers = new FakeHandlers();
        var processor = new OfflineBridgeProcessor(handlers);

        // Intentar "ejecutar" algo arbitrario como acción.
        var evil = OfflineBridgeCodec.CreateRequest("System.IO.File.Delete", new { path = "x" }, "evil");
        var resp = await processor.ProcessAsync(evil);
        Assert.False(resp.Ok);
        Assert.Equal(OfflineBridgeErrorCodes.UnauthorizedAction, resp.ErrorCode);
        Assert.Equal(0, handlers.SaveCount);
        Assert.Equal(0, handlers.ClearCount);
        Assert.Equal(0, handlers.OpenCount);
    }

    [Fact]
    public async Task Bridge_Save_Get_Clear_And_OpenCapture()
    {
        await using var db = CreateContext();
        var sessions = CreateSessions(db);
        var handlers = new SessionBackedHandlers(sessions, openCount: new Counter());
        var processor = new OfflineBridgeProcessor(handlers);

        var save = OfflineBridgeCodec.CreateRequest(
            OfflineBridgeActions.SessionSave,
            new
            {
                userId = "u-bridge",
                username = "bridge-user",
                roleCode = "Operario",
                permissions = new[] { "CaptureRecords", "ViewRecords" },
                serverBaseUrl = "http://localhost:5080"
            },
            "save-1");
        var saveResp = await processor.ProcessAsync(save);
        Assert.True(saveResp.Ok);

        var get = OfflineBridgeCodec.CreateRequest(OfflineBridgeActions.SessionGet, new { }, "get-1");
        var getResp = await processor.ProcessAsync(get);
        Assert.True(getResp.Ok);
        Assert.NotNull(getResp.Result);
        var view = getResp.Result!.Value.Deserialize<OfflineSessionViewDto>(OfflineBridgeCodec.JsonOptions);
        Assert.NotNull(view);
        Assert.True(view!.IsValid);
        Assert.Equal("u-bridge", view.UserId);
        Assert.False(view.HasSecureAuthMaterial);

        var open = OfflineBridgeCodec.CreateRequest(OfflineBridgeActions.CaptureOpen, new { }, "open-1");
        Assert.True((await processor.ProcessAsync(open)).Ok);
        Assert.Equal(1, handlers.OpenCounter.Value);

        var clear = OfflineBridgeCodec.CreateRequest(OfflineBridgeActions.SessionClear, new { }, "clear-1");
        var clearResp = await processor.ProcessAsync(clear);
        Assert.True(clearResp.Ok);
        Assert.Null(await sessions.GetRawSessionAsync());
    }

    [Fact]
    public async Task OfflineCapture_Valid_Session_Allowed_Expired_Rejected()
    {
        await using var db = CreateContext();
        var sessions = CreateSessions(db);
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "cap-device"));
        var capture = new OfflineCaptureService(db, sessions, devices);

        await sessions.UpsertUxSnapshotAsync(
            "u1", "u1", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            null,
            TimeSpan.FromHours(1));

        var ok = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
        {
            Telar = "10",
            Neps = 18,
            CaptureSessionId = "cap-sess-1"
        });
        Assert.Equal(LocalSyncStatus.PendingSync, ok.Record.SyncStatus);
        Assert.Equal("cap-sess-1", ok.Record.CaptureSessionId);
        Assert.Equal(ok.Record.ClientOperationId, ok.Operation.ClientOperationId);
        Assert.False(string.IsNullOrWhiteSpace(ok.Record.ClientOperationId));
        Assert.Equal(AlertLevel.Ok, ok.QualityLevel);

        await sessions.ClearUxSnapshotAsync();
        await sessions.UpsertUxSnapshotAsync(
            "u1", "u1", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            null,
            TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "11", Neps = 12 }));
    }

    [Fact]
    public async Task OfflineCapture_Neps_Classification_Uses_Official_Criteria()
    {
        await using var db = CreateContext();
        var sessions = CreateSessions(db);
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "neps-device"));
        var capture = new OfflineCaptureService(db, sessions, devices);
        await sessions.UpsertUxSnapshotAsync(
            "u1", "u1", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            null);

        async Task<AlertLevel> Level(double neps)
        {
            var r = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "T", Neps = neps });
            Assert.Equal(AlertEvaluator.GetLevel(neps), r.QualityLevel);
            return r.QualityLevel;
        }

        Assert.Equal(AlertLevel.Ok, await Level(18));
        Assert.Equal(AlertLevel.Mention, await Level(19));
        Assert.Equal(AlertLevel.CriticalAdjustment, await Level(46));
        Assert.Equal(AlertLevel.SecondQuality, await Level(55));
    }

    private sealed class Counter
    {
        public int Value;
    }

    private sealed class FakeHandlers : IOfflineBridgeHandlers
    {
        public int SaveCount;
        public int ClearCount;
        public int OpenCount;

        public Task SaveSessionAsync(OfflineSessionSavePayload payload, CancellationToken ct = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<ClearLocalSessionResult> ClearSessionAsync(CancellationToken ct = default)
        {
            ClearCount++;
            return Task.FromResult(new ClearLocalSessionResult { Cleared = true });
        }

        public Task<OfflineSessionViewDto> GetSessionAsync(CancellationToken ct = default) =>
            Task.FromResult(new OfflineSessionViewDto());

        public Task OpenCaptureAsync(CancellationToken ct = default)
        {
            OpenCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class SessionBackedHandlers : IOfflineBridgeHandlers
    {
        private readonly OfflineSessionService _sessions;
        public Counter OpenCounter { get; }

        public SessionBackedHandlers(OfflineSessionService sessions, Counter openCount)
        {
            _sessions = sessions;
            OpenCounter = openCount;
        }

        public Task SaveSessionAsync(OfflineSessionSavePayload payload, CancellationToken ct = default) =>
            _sessions.UpsertUxSnapshotAsync(
                payload.UserId,
                payload.Username,
                payload.RoleCode,
                payload.Permissions,
                payload.ServerBaseUrl,
                secureAuthMaterial: null,
                ct: ct);

        public Task<ClearLocalSessionResult> ClearSessionAsync(CancellationToken ct = default) =>
            _sessions.ClearUxSnapshotAsync(ct);

        public async Task<OfflineSessionViewDto> GetSessionAsync(CancellationToken ct = default)
        {
            var raw = await _sessions.GetRawSessionAsync(ct);
            return _sessions.ToView(raw);
        }

        public Task OpenCaptureAsync(CancellationToken ct = default)
        {
            OpenCounter.Value++;
            return Task.CompletedTask;
        }
    }
}
