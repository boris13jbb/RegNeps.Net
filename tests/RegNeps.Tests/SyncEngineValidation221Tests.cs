using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// FASE 2D.2.1 — validación de integración SyncEngine (Id local ≠ EntityId, cookie/401, crash-safe).
/// </summary>
public sealed class SyncEngineValidation221Tests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-221-" + Guid.NewGuid().ToString("N"));
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

    private async Task<(SyncEngine Engine, OfflineCaptureService Capture, FakeApi Api)> BootAsync(
        LocalSyncDbContext db,
        string? cookie = "RegNeps.Auth=valid",
        ISyncApiClient? apiOverride = null)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM PendingOperations");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalNepRecords");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalSessions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SyncStates");
        db.ChangeTracker.Clear();

        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "d-" + Guid.NewGuid().ToString("N")[..8]));
        var capture = new OfflineCaptureService(db, sessions, devices);
        var api = new FakeApi();
        var engine = new SyncEngine(db, sessions, devices, apiOverride ?? api, new StaticCookie(cookie));

        await sessions.UpsertUxSnapshotAsync(
            "user-a", "alice", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://192.0.2.10:5080");
        db.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = 0 });
        await db.SaveChangesAsync();
        return (engine, capture, api);
    }

    [Fact]
    public async Task Create_Accepted_Sets_ServerRecordId_Distinct_From_LocalId()
    {
        await using var db = CreateContext();
        var (engine, capture, api) = await BootAsync(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "10", Neps = 18 });
        var localId = created.Record.Id;
        var serverId = Guid.NewGuid();
        Assert.NotEqual(localId, serverId);

        api.OnPush = req => Accepted(req, serverId, "stamp-a");
        api.OnPull = r => Empty(r.Cursor);

        await engine.SyncAsync();

        var record = await db.LocalNepRecords.SingleAsync();
        Assert.Equal(localId, record.Id);
        Assert.Equal(serverId, record.ServerRecordId);
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations.SingleAsync()).Status);
        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
    }

    [Fact]
    public async Task Accepted_Then_Local_Save_Fails_Retry_Duplicate_Recovers_ServerRecordId()
    {
        var failOnce = new FailNextSaveInterceptor();
        await using (var db = CreateContext(failOnce))
        {
            var (engine, capture, api) = await BootAsync(db);
            var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "11", Neps = 19 });
            var localId = created.Record.Id;
            var serverId = Guid.NewGuid();
            var clientOp = created.Operation.ClientOperationId;

            api.OnPush = req => Accepted(req, serverId, "stamp-1");
            api.OnPull = r => Empty(r.Cursor);
            failOnce.Armed = true;

            // El servidor aceptó, pero el SaveChanges local del lote Push falla → Outbox intacto.
            var run1 = await engine.SyncAsync();
            Assert.Contains("forced local persist failure", run1.Message ?? "", StringComparison.Ordinal);
            Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);
            Assert.Null((await db.LocalNepRecords.SingleAsync()).ServerRecordId);
        }

        // Reabrir contexto limpio (sin interceptor): Duplicate recupera EntityId.
        await using (var db = CreateContext())
        {
            var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
            var devices = new FileDeviceIdStore(Path.Combine(_dir, "d-retry"));
            var api = new FakeApi();
            var engine = new SyncEngine(db, sessions, devices, api, new StaticCookie("RegNeps.Auth=valid"));

            var op = await db.PendingOperations.SingleAsync();
            var localId = op.LocalNepRecordId!.Value;
            var serverId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            // El EntityId del Duplicate debe ser el del servidor (simulado).
            // En el fallo anterior el servidor ya habría creado el registro; aquí fijamos un Guid estable.
            // Recuperamos ClientOperationId real:
            var clientOp = op.ClientOperationId;
            // Usamos cualquier serverId distinto del local.
            serverId = Guid.NewGuid();
            while (serverId == localId)
            {
                serverId = Guid.NewGuid();
            }

            api.OnPush = req => new ClientSyncPushResponse
            {
                Results = req.Operations.Select(o => new ClientSyncOperationResultDto
                {
                    ClientOperationId = o.ClientOperationId,
                    Result = ClientSyncResultNames.Duplicate,
                    EntityId = serverId,
                    ConcurrencyStamp = "stamp-dup"
                }).ToList()
            };
            api.OnPull = r => Empty(r.Cursor);

            var run2 = await engine.SyncAsync();
            Assert.Equal(1, run2.PushedDuplicate);

            var record = await db.LocalNepRecords.SingleAsync();
            Assert.Equal(localId, record.Id);
            Assert.Equal(serverId, record.ServerRecordId);
            Assert.Equal("stamp-dup", record.ConcurrencyStamp);
            Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations.SingleAsync()).Status);
            Assert.Equal(1, await db.LocalNepRecords.CountAsync());
            Assert.Equal(clientOp, record.ClientOperationId);
        }
    }

    [Fact]
    public async Task Pull_Before_ServerRecordId_Matches_By_ClientOperationId_No_Duplicate()
    {
        await using var db = CreateContext();
        var (engine, capture, api) = await BootAsync(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "12", Neps = 20 });
        var localId = created.Record.Id;
        var serverId = Guid.NewGuid();
        Assert.NotEqual(localId, serverId);
        Assert.Null(created.Record.ServerRecordId);

        // Servidor ya tiene el Create (p. ej. Accept perdido localmente); llega Pull primero.
        api.OnPush = _ => new ClientSyncPushResponse { Results = [] }; // no pending enviados si... aún hay Pending
        // Push se ejecuta primero: devolver Duplicate también estaría OK; aquí forzamos Pull path
        // dejando Pending y haciendo que Push sea Transient para que Pull ejecute el vínculo.
        api.OnPush = req => new ClientSyncPushResponse
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.TransientError,
                ErrorCode = "TRANSIENT"
            }).ToList()
        };
        api.OnPull = req => req.Cursor > 0
            ? Empty(req.Cursor)
            : new ClientSyncPullResponse
            {
                NextCursor = 1,
                HasMore = false,
                Changes =
                [
                    new ClientSyncChangeDto
                    {
                        Sequence = 1,
                        EntityId = serverId,
                        ChangeType = SyncConstants.ChangeRecordUpserted,
                        EntityType = SyncConstants.EntityNepRecord,
                        OccurredAtUtc = DateTime.UtcNow,
                        Payload = Stable(new ClientNepRecordSnapshot
                        {
                            Id = serverId,
                            Telar = "12",
                            Neps = 20,
                            ConcurrencyStamp = "from-pull",
                            ClientOperationId = created.Operation.ClientOperationId,
                            OwnerUserId = "user-a"
                        })
                    }
                ]
            };

        await engine.SyncAsync();

        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
        var record = await db.LocalNepRecords.SingleAsync();
        Assert.Equal(localId, record.Id);
        Assert.Equal(serverId, record.ServerRecordId);
        Assert.Equal("from-pull", record.ConcurrencyStamp);
        Assert.Equal(PendingOperationStatus.Synced, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Http_401_Leaves_Outbox_Pending_And_Is_AuthRequired()
    {
        await using var db = CreateContext();
        var handler = new FixedStatusHandler(HttpStatusCode.Unauthorized);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new HttpSyncApiClient(http);

        var (engine, capture, _) = await BootAsync(db, apiOverride: client);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "13", Neps = 21 });

        var run = await engine.SyncAsync();
        Assert.True(run.AuthRequired);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);
        Assert.Null((await db.LocalNepRecords.SingleAsync()).ServerRecordId);
    }

    [Fact]
    public async Task Missing_Cookie_Does_Not_Mark_Synced()
    {
        await using var db = CreateContext();
        var (engine, capture, api) = await BootAsync(db, cookie: null);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "14", Neps = 22 });
        var run = await engine.SyncAsync();
        Assert.True(run.AuthRequired);
        Assert.Equal(0, api.PushCalls);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Pull_Upsert_Is_Idempotent_Second_Page_No_Duplicate()
    {
        await using var db = CreateContext();
        var (engine, _, api) = await BootAsync(db);
        var serverId = Guid.NewGuid();
        var payload = Stable(new ClientNepRecordSnapshot
        {
            Id = serverId,
            Telar = "T",
            Neps = 10,
            ConcurrencyStamp = "c1",
            ClientOperationId = "idem-op-1",
            OwnerUserId = "user-a"
        });

        var pulls = 0;
        api.OnPush = _ => new ClientSyncPushResponse();
        api.OnPull = req =>
        {
            pulls++;
            if (req.Cursor >= 1)
            {
                // Reentrega de la misma página (simulando retry tras crash antes de cursor).
                // Con cursor ya avanzado el motor pide la siguiente; simulamos re-sync desde 0
                // en segunda SyncAsync.
                return Empty(req.Cursor);
            }

            return new ClientSyncPullResponse
            {
                NextCursor = 1,
                HasMore = false,
                Changes =
                [
                    new ClientSyncChangeDto
                    {
                        Sequence = 1,
                        EntityId = serverId,
                        ChangeType = SyncConstants.ChangeRecordUpserted,
                        EntityType = SyncConstants.EntityNepRecord,
                        OccurredAtUtc = DateTime.UtcNow,
                        Payload = payload
                    }
                ]
            };
        };

        await engine.SyncAsync();
        // Simular crash: cursor vuelve a 0 y se re-aplica la misma página.
        var state = await db.SyncStates.SingleAsync();
        state.LastPulledSequence = 0;
        await db.SaveChangesAsync();
        await engine.SyncAsync();

        Assert.Equal(1, await db.LocalNepRecords.CountAsync());
        Assert.Equal(1, (await db.SyncStates.SingleAsync()).LastPulledSequence);
    }

    [Fact]
    public async Task Pending_Update_Plus_Pull_Does_Not_Overwrite_Local_Edit()
    {
        await using var db = CreateContext();
        var (engine, capture, api) = await BootAsync(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "ORIG", Neps = 10 });
        var serverId = created.Record.Id;
        created.Record.ServerRecordId = serverId;
        created.Record.ConcurrencyStamp = "old";
        var createOp = await db.PendingOperations.SingleAsync();
        createOp.Status = PendingOperationStatus.Synced;

        created.Record.Telar = "LOCAL-EDIT";
        created.Record.Neps = 88;
        created.Record.SyncStatus = LocalSyncStatus.PendingSync;
        db.PendingOperations.Add(new PendingOperation
        {
            Id = Guid.NewGuid(),
            ClientOperationId = Guid.NewGuid().ToString("N"),
            OperationType = OfflineOperationType.UpdateRecord,
            PayloadJson = JsonSerializer.Serialize(new { entityId = serverId, telar = "LOCAL-EDIT", neps = 88 }, ClientSyncJson.Options),
            ProtocolVersion = 1,
            CreatedAtUtc = DateTime.UtcNow,
            Status = PendingOperationStatus.Pending,
            UserId = "user-a",
            DeviceId = "dev",
            LocalNepRecordId = created.Record.Id,
            TargetServerRecordId = serverId,
            ExpectedConcurrencyStamp = "old"
        });
        await db.SaveChangesAsync();

        api.OnPush = req => new ClientSyncPushResponse
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.TransientError
            }).ToList()
        };
        api.OnPull = req => req.Cursor > 0
            ? Empty(req.Cursor)
            : new ClientSyncPullResponse
            {
                NextCursor = 1,
                HasMore = false,
                Changes =
                [
                    new ClientSyncChangeDto
                    {
                        Sequence = 1,
                        EntityId = serverId,
                        ChangeType = SyncConstants.ChangeRecordUpserted,
                        EntityType = SyncConstants.EntityNepRecord,
                        OccurredAtUtc = DateTime.UtcNow,
                        Payload = Stable(new ClientNepRecordSnapshot
                        {
                            Id = serverId,
                            Telar = "REMOTE",
                            Neps = 1,
                            ConcurrencyStamp = "new",
                            OwnerUserId = "user-a",
                            ClientOperationId = "remote-op"
                        })
                    }
                ]
            };

        await engine.SyncAsync();
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.Equal("LOCAL-EDIT", local.Telar);
        Assert.Equal(88, local.Neps);
        Assert.Equal("new", local.ConcurrencyStamp);
        var update = await db.PendingOperations.SingleAsync(o => o.OperationType == OfflineOperationType.UpdateRecord);
        Assert.Equal(PendingOperationStatus.Pending, update.Status);
        Assert.Equal("new", update.ExpectedConcurrencyStamp);
    }

    [Fact]
    public async Task Pending_Delete_Plus_Pull_Tombstone_Is_Consistent()
    {
        await using var db = CreateContext();
        var (engine, capture, api) = await BootAsync(db);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "DEL", Neps = 10 });
        var serverId = created.Record.Id;
        created.Record.ServerRecordId = serverId;
        created.Record.ConcurrencyStamp = "s1";
        var createOp = await db.PendingOperations.SingleAsync();
        createOp.Status = PendingOperationStatus.Synced;

        db.PendingOperations.Add(new PendingOperation
        {
            Id = Guid.NewGuid(),
            ClientOperationId = Guid.NewGuid().ToString("N"),
            OperationType = OfflineOperationType.DeleteRecord,
            PayloadJson = JsonSerializer.Serialize(new { entityId = serverId }, ClientSyncJson.Options),
            ProtocolVersion = 1,
            CreatedAtUtc = DateTime.UtcNow,
            Status = PendingOperationStatus.Pending,
            UserId = "user-a",
            DeviceId = "dev",
            LocalNepRecordId = created.Record.Id,
            TargetServerRecordId = serverId,
            ExpectedConcurrencyStamp = "s1"
        });
        await db.SaveChangesAsync();

        api.OnPush = req => new ClientSyncPushResponse
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.TransientError
            }).ToList()
        };
        api.OnPull = req => req.Cursor > 0
            ? Empty(req.Cursor)
            : new ClientSyncPullResponse
            {
                NextCursor = 1,
                HasMore = false,
                Changes =
                [
                    new ClientSyncChangeDto
                    {
                        Sequence = 1,
                        EntityId = serverId,
                        ChangeType = SyncConstants.ChangeRecordDeleted,
                        EntityType = SyncConstants.EntityNepRecord,
                        OccurredAtUtc = DateTime.UtcNow,
                        Payload = Stable(new ClientNepRecordDeletedSnapshot
                        {
                            Id = serverId,
                            OwnerUserId = "user-a",
                            DeletedAtUtc = DateTime.UtcNow,
                            LastConcurrencyStamp = "s1"
                        })
                    }
                ]
            };

        await engine.SyncAsync();
        var local = await db.LocalNepRecords.SingleAsync();
        Assert.True(local.IsDeleted);
        var del = await db.PendingOperations.SingleAsync(o => o.OperationType == OfflineOperationType.DeleteRecord);
        Assert.Equal(PendingOperationStatus.Conflict, del.Status);
        Assert.Equal("ENTITY_DELETED", del.LastServerErrorCode);
    }

    [Fact]
    public async Task User_B_Does_Not_Push_Outbox_Of_User_A_After_Logout()
    {
        await using var db = CreateContext();
        var (engine, capture, api) = await BootAsync(db);
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "A1", Neps = 10 });
        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        await sessions.ClearUxSnapshotAsync();

        await sessions.UpsertUxSnapshotAsync(
            "user-b", "bob", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://192.0.2.10:5080");

        api.OnPush = req =>
        {
            if (req.Operations.Count > 0)
            {
                Assert.Fail("No debe enviar operaciones de usuario A con sesión de B.");
            }

            return new ClientSyncPushResponse();
        };
        api.OnPull = r => Empty(r.Cursor);

        // Recrear engine con misma API tras cambio de sesión
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "d-b"));
        engine = new SyncEngine(db, sessions, devices, api, new StaticCookie("RegNeps.Auth=b"));
        var run = await engine.SyncAsync();
        Assert.Equal(1, run.SkippedOtherUser);
        Assert.Equal(0, run.PushedAccepted);
        Assert.Equal(PendingOperationStatus.Pending,
            (await db.PendingOperations.SingleAsync(o => o.UserId == "user-a")).Status);
    }

    [Fact]
    public async Task Cookie_Header_Is_Forwarded_By_HttpSyncApiClient()
    {
        string? seenCookie = null;
        var handler = new CaptureCookieHandler(async (req, ct) =>
        {
            seenCookie = req.Headers.TryGetValues("Cookie", out var v) ? string.Join("; ", v) : null;
            var json = JsonSerializer.Serialize(new ClientSyncPushResponse { Results = [] }, ClientSyncJson.Options);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var http = new HttpClient(handler);
        var client = new HttpSyncApiClient(http);
        await client.PushAsync(
            "http://192.0.2.10:5080",
            "RegNeps.Auth=abc123; path=/",
            new ClientSyncPushRequest { DeviceId = "d", Operations = [] },
            CancellationToken.None);

        Assert.Contains("RegNeps.Auth=abc123", seenCookie);
    }

    private static ClientSyncPushResponse Accepted(ClientSyncPushRequest req, Guid entityId, string stamp) =>
        new()
        {
            Results = req.Operations.Select(o => new ClientSyncOperationResultDto
            {
                ClientOperationId = o.ClientOperationId,
                Result = ClientSyncResultNames.Accepted,
                EntityId = entityId,
                ConcurrencyStamp = stamp
            }).ToList()
        };

    private static ClientSyncPullResponse Empty(long cursor) =>
        new() { NextCursor = cursor, HasMore = false, Changes = [] };

    private static JsonElement Stable<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, ClientSyncJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class StaticCookie(string? cookie) : ISyncAuthCookieProvider
    {
        public Task<string?> GetCookieHeaderAsync(string serverBaseUrl, CancellationToken ct = default) =>
            Task.FromResult(cookie);
    }

    private sealed class FakeApi : ISyncApiClient
    {
        public int PushCalls;
        public Func<ClientSyncPushRequest, ClientSyncPushResponse>? OnPush;
        public Func<ClientSyncPullRequest, ClientSyncPullResponse>? OnPull;

        public Task<ClientSyncPushResponse> PushAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPushRequest request, CancellationToken ct = default)
        {
            PushCalls++;
            return Task.FromResult(OnPush?.Invoke(request) ?? new ClientSyncPushResponse());
        }

        public Task<ClientSyncPullResponse> PullAsync(
            string serverBaseUrl, string? cookieHeader, ClientSyncPullRequest request, CancellationToken ct = default) =>
            Task.FromResult(OnPull?.Invoke(request) ?? Empty(request.Cursor));
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class CaptureCookieHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    /// <summary>Falla el SaveChanges del lote Push (Pending→Synced), no el de captura inicial.</summary>
    private sealed class FailNextSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed;

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            if (ShouldFail(eventData))
            {
                Armed = false;
                throw new InvalidOperationException("forced local persist failure after Accepted");
            }

            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (ShouldFail(eventData))
            {
                Armed = false;
                throw new InvalidOperationException("forced local persist failure after Accepted");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private bool ShouldFail(DbContextEventData eventData)
        {
            if (!Armed || eventData.Context is null)
            {
                return false;
            }

            // Solo cuando hay PendingOperation pasando a Synced (resultado de Push).
            return eventData.Context.ChangeTracker.Entries<PendingOperation>()
                .Any(e => e.State == EntityState.Modified
                          && e.Entity.Status == PendingOperationStatus.Synced);
        }
    }
}
