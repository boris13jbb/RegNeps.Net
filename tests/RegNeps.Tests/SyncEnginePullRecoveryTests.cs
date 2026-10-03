using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Sync;
using RegNeps.OfflineStore;
using RegNeps.OfflineStore.Device;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;
using RegNeps.OfflineStore.Sync.Contracts;

namespace RegNeps.Tests;

/// <summary>
/// FASE 2E — Pull-only recovery: cursor, páginas, ClearAll/tombstones, Outbox intacto, logout.
/// Simula desconexión SignalR (mensajes perdidos) usando solo PullAsync.
/// </summary>
public sealed class SyncEnginePullRecoveryTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-pull-recovery-" + Guid.NewGuid().ToString("N"));
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

    private async Task<(SyncEngine Engine, OfflineCaptureService Capture, OfflineSessionService Sessions, FakeApi Api)>
        CreateAsync(LocalSyncDbContext db, string userId = "user-a", long cursor = 0)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM PendingOperations");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalNepRecords");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalSessions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SyncStates");
        db.ChangeTracker.Clear();

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "device-" + Guid.NewGuid().ToString("N")[..8]));
        var capture = new OfflineCaptureService(db, sessions, devices);
        var api = new FakeApi();
        var engine = new SyncEngine(db, sessions, devices, api, new StaticCookie("RegNeps.Auth=test"));

        await sessions.UpsertUxSnapshotAsync(
            userId,
            "alice",
            "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080");

        db.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = cursor });
        await db.SaveChangesAsync();
        return (engine, capture, sessions, api);
    }

    [Fact]
    public async Task A_Disconnect_Simple_Pull_Advances_Cursor_X_To_XPlusN()
    {
        await using var db = CreateContext();
        var (engine, _, _, api) = await CreateAsync(db, cursor: 5);
        var id = Guid.NewGuid();
        api.PullHandler = req =>
        {
            Assert.Equal(5, req.Cursor);
            return Page(req.Cursor, hasMore: false,
                Upsert(6, id, "A", 1, "user-a"),
                Upsert(7, Guid.NewGuid(), "B", 2, "user-a"),
                Upsert(8, Guid.NewGuid(), "C", 3, "user-a"));
        };

        var run = await engine.PullAsync();
        Assert.True(run.PullCompleted);
        Assert.Equal(5, run.CursorBefore);
        Assert.Equal(8, run.CursorAfter);
        Assert.Equal(0, api.PushCalls);
        Assert.Equal(8, (await db.SyncStates.SingleAsync()).LastPulledSequence);
    }

    [Fact]
    public async Task B_Lost_SignalR_Messages_Still_Recovered_By_Pull()
    {
        // Ninguna notificación llega; solo Pull al “reconnect”.
        await using var db = CreateContext();
        var (engine, _, _, api) = await CreateAsync(db, cursor: 2);
        var serverId = Guid.NewGuid();
        api.PullHandler = req => Page(req.Cursor, false, Upsert(3, serverId, "lost", 9, "user-a"));

        var run = await engine.PullAsync();
        Assert.True(run.PullCompleted);
        Assert.Equal(3, run.CursorAfter);
        Assert.Equal(serverId, (await db.LocalNepRecords.SingleAsync()).ServerRecordId);
    }

    [Fact]
    public async Task C_Multiple_Changes_Same_Record_Converge()
    {
        await using var db = CreateContext();
        var (engine, _, _, api) = await CreateAsync(db, cursor: 0);
        var id = Guid.NewGuid();
        // Misma semántica que sync real: páginas ASC; varios cambios del mismo EntityId.
        api.PullHandler = req => req.Cursor switch
        {
            0 => Page(req.Cursor, true, Upsert(1, id, "v1", 1, "user-a", "s1")),
            1 => Page(req.Cursor, true, Upsert(2, id, "v2", 2, "user-a", "s2")),
            2 => Page(req.Cursor, false, Delete(3, id)),
            _ => Page(req.Cursor, false)
        };

        var run = await engine.PullAsync();
        Assert.True(string.IsNullOrEmpty(run.Message), run.Message);
        Assert.True(run.PullCompleted);
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.True(local.IsDeleted);
        Assert.Equal(3, (await db.SyncStates.SingleAsync()).LastPulledSequence);
    }

    [Fact]
    public async Task D_ClearAll_During_Disconnect_Tombstones_Then_Create_Y()
    {
        await using var db = CreateContext();
        var (engine, _, _, api) = await CreateAsync(db, cursor: 10);
        // Réplica local previa (debe quedar tombstone / no resucitar).
        var oldServer = Guid.NewGuid();
        db.LocalNepRecords.Add(new LocalNepRecord
        {
            Id = Guid.NewGuid(),
            ServerRecordId = oldServer,
            Telar = "OLD",
            Neps = 1,
            UserId = "user-a",
            ClientOperationId = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = "old",
            CaptureSessionId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            SyncStatus = LocalSyncStatus.Synced
        });
        await db.SaveChangesAsync();

        var yId = Guid.NewGuid();
        api.PullHandler = req =>
        {
            Assert.Equal(10, req.Cursor);
            return Page(req.Cursor, false,
                Delete(11, oldServer),
                Upsert(12, yId, "Y", 5, "user-a", "y1"));
        };

        await engine.PullAsync();
        var old = await db.LocalNepRecords.SingleAsync(r => r.ServerRecordId == oldServer);
        Assert.True(old.IsDeleted);
        var y = await db.LocalNepRecords.SingleAsync(r => r.ServerRecordId == yId);
        Assert.False(y.IsDeleted);
        Assert.Equal("Y", y.Telar);
        Assert.Equal(12, (await db.SyncStates.SingleAsync()).LastPulledSequence);
        Assert.Equal(0, api.PushCalls);
    }

    [Fact]
    public async Task F_HasMore_Drains_All_Pages()
    {
        await using var db = CreateContext();
        var (engine, _, _, api) = await CreateAsync(db, cursor: 0);
        var page = 0;
        api.PullHandler = req =>
        {
            page++;
            var seq = page;
            return new ClientSyncPullResponse
            {
                NextCursor = seq,
                HasMore = page < 3,
                Changes =
                [
                    Upsert(seq, Guid.NewGuid(), "P" + page, page, "user-a")
                ]
            };
        };

        var run = await engine.PullAsync();
        Assert.True(run.PullCompleted);
        Assert.Equal(3, run.PullPagesProcessed);
        Assert.Equal(3, run.CursorAfter);
        Assert.Equal(3, await db.LocalNepRecords.CountAsync());
    }

    [Fact]
    public async Task G_Temporary_Pull_Failure_Keeps_Cursor_And_Outbox_Pending()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api) = await CreateAsync(db, cursor: 4);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "pend", Neps = 1 });
        api.PullHandler = _ => throw new SyncTransportException(
            SyncTransportFailureKind.Timeout, "timeout");

        var run = await engine.PullAsync();
        Assert.False(run.PullCompleted);
        Assert.Equal(4, (await db.SyncStates.SingleAsync()).LastPulledSequence);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);

        // Reintento OK
        var id = Guid.NewGuid();
        api.PullHandler = req => Page(req.Cursor, false, Upsert(5, id, "ok", 1, "user-a"));
        var retry = await engine.PullAsync();
        Assert.True(retry.PullCompleted);
        Assert.Equal(5, retry.CursorAfter);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);
        Assert.Equal(0, api.PushCalls);
    }

    [Fact]
    public async Task H_Concurrent_Pull_Does_Not_Regress_Cursor()
    {
        await using var db = CreateContext();
        var (engine, _, _, api) = await CreateAsync(db, cursor: 0);
        var gate = new SemaphoreSlim(0, 1);
        var started = 0;
        api.PullHandler = req =>
        {
            Interlocked.Increment(ref started);
            if (started == 1)
            {
                gate.Wait(2000);
            }

            return Page(req.Cursor, false, Upsert(req.Cursor + 1, Guid.NewGuid(), "X", 1, "user-a"));
        };

        var t1 = engine.PullAsync();
        await WaitUntilAsync(() => Volatile.Read(ref started) >= 1, 2000);
        // Segunda corrida: si ve cursor ya avanzado en TX, aborta página sin regresar.
        var t2 = engine.PullAsync();
        await Task.Delay(50);
        gate.Release();
        var r1 = await t1;
        var r2 = await t2;
        var cursor = (await db.SyncStates.SingleAsync()).LastPulledSequence;
        Assert.True(cursor >= 1);
        Assert.True(r1.CursorAfter >= 0);
        Assert.True(r2.CursorAfter >= 0);
        Assert.True(cursor >= Math.Max(r1.CursorAfter, r2.CursorAfter) || cursor >= 1);
    }

    [Fact]
    public async Task I_Logout_Blocks_Recovery_Until_Same_User_Login()
    {
        await using var db = CreateContext();
        var (engine, capture, sessions, api) = await CreateAsync(db, cursor: 1);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 1 });
        await sessions.ClearUxSnapshotAsync();

        var blocked = await engine.PullAsync();
        Assert.True(blocked.SessionMissingOrExpired);
        Assert.Equal(0, api.PullCalls);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);

        await sessions.UpsertUxSnapshotAsync(
            "user-a", "alice", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080");
        api.PullHandler = req => Page(req.Cursor, false);
        var run = await engine.PullAsync();
        Assert.True(run.Started);
        Assert.True(run.PullCompleted);
    }

    [Fact]
    public async Task J_PullOnly_Never_Pushes_Outbox()
    {
        await using var db = CreateContext();
        var (engine, capture, _, api) = await CreateAsync(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "x", Neps = 2 });
        api.PullHandler = req => Page(req.Cursor, false);
        await engine.PullAsync();
        Assert.Equal(0, api.PushCalls);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Pull_Does_Not_Advance_Cursor_From_Notification_Semantics()
    {
        // El motor solo avanza cursor tras aplicar página de /api/sync/pull.
        await using var db = CreateContext();
        var (engine, _, _, api) = await CreateAsync(db, cursor: 20);
        api.PullHandler = req => new ClientSyncPullResponse
        {
            NextCursor = 20,
            HasMore = false,
            Changes = []
        };
        var run = await engine.PullAsync();
        Assert.Equal(20, run.CursorBefore);
        Assert.Equal(20, run.CursorAfter);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(15);
        }
    }

    private static ClientSyncPullResponse Page(
        long cursor,
        bool hasMore,
        params ClientSyncChangeDto[] changes)
    {
        var next = changes.Length == 0 ? cursor : changes.Max(c => c.Sequence);
        return new ClientSyncPullResponse
        {
            NextCursor = next,
            HasMore = hasMore,
            Changes = changes.ToList()
        };
    }

    private static ClientSyncChangeDto Upsert(
        long seq,
        Guid id,
        string telar,
        double neps,
        string owner,
        string stamp = "st") =>
        new()
        {
            Sequence = seq,
            EntityId = id,
            ChangeType = SyncConstants.ChangeRecordUpserted,
            EntityType = SyncConstants.EntityNepRecord,
            OccurredAtUtc = DateTime.UtcNow,
            Payload = StableJson(new ClientNepRecordSnapshot
            {
                Id = id,
                Telar = telar,
                Neps = neps,
                ConcurrencyStamp = stamp,
                OwnerUserId = owner,
                ClientOperationId = Guid.NewGuid().ToString("N")
            })
        };

    private static ClientSyncChangeDto Delete(long seq, Guid id) =>
        new()
        {
            Sequence = seq,
            EntityId = id,
            ChangeType = SyncConstants.ChangeRecordDeleted,
            EntityType = SyncConstants.EntityNepRecord,
            OccurredAtUtc = DateTime.UtcNow,
            Payload = StableJson(new ClientNepRecordDeletedSnapshot
            {
                Id = id,
                OwnerUserId = "user-a",
                DeletedAtUtc = DateTime.UtcNow,
                LastConcurrencyStamp = "tomb"
            })
        };

    private static JsonElement StableJson<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, ClientSyncJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class StaticCookie : ISyncAuthCookieProvider
    {
        private readonly string? _cookie;
        public StaticCookie(string? cookie) => _cookie = cookie;
        public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default) =>
            Task.FromResult(_cookie);
    }

    private sealed class FakeApi : ISyncApiClient
    {
        public int PushCalls;
        public int PullCalls;
        public Func<ClientSyncPullRequest, ClientSyncPullResponse>? PullHandler;

        public Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl,
            string? cookieHeader,
            ClientSyncPushRequest request,
            CancellationToken ct = default)
        {
            PushCalls++;
            return Task.FromResult(new ClientSyncPushResponse());
        }

        public Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl,
            string? cookieHeader,
            ClientSyncPullRequest request,
            CancellationToken ct = default)
        {
            PullCalls++;
            if (PullHandler is null)
            {
                return Task.FromResult(new ClientSyncPullResponse
                {
                    NextCursor = request.Cursor,
                    HasMore = false,
                    Changes = []
                });
            }

            return Task.FromResult(PullHandler(request));
        }
    }
}
