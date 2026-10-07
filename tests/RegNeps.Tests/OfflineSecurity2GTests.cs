using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Sync;
using RegNeps.OfflineStore;
using RegNeps.OfflineStore.Abstractions;
using RegNeps.OfflineStore.Device;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;

namespace RegNeps.Tests;

/// <summary>FASE 2G — regresión de seguridad offline/sesión/cursor/secretos.</summary>
public sealed class OfflineSecurity2GTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "regneps-sec2g-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void LocalSession_Has_No_Password_Or_Token_Properties()
    {
        var names = OfflineSessionService.LocalSessionPropertyNames();
        Assert.DoesNotContain(names, n => n.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Cookie", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SecureAuthMaterialGuard_Rejects_Cookie_And_Bearer()
    {
        Assert.True(SecureAuthMaterialGuard.LooksLikeHttpCookieOrBearer("RegNeps.Auth=abc; path=/"));
        Assert.True(SecureAuthMaterialGuard.LooksLikeHttpCookieOrBearer("Bearer eyJhbGciOiJIUzI1NiJ9.x.y"));
        Assert.True(SecureAuthMaterialGuard.LooksLikeHttpCookieOrBearer(".AspNetCore.Cookies=xyz"));
        Assert.False(SecureAuthMaterialGuard.LooksLikeHttpCookieOrBearer("opaque-non-cookie-material"));
        Assert.Throws<ArgumentException>(() =>
            SecureAuthMaterialGuard.EnsureNotAuthCookieOrBearer("RegNeps.Auth=secret"));
    }

    [Fact]
    public async Task SecureStore_Rejects_Auth_Cookie_Material()
    {
        var store = new MemorySecureAuthMaterialStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SetAuthMaterialAsync("user-a", "RegNeps.Auth=abc123; path=/"));
    }

    [Fact]
    public async Task Logout_Resets_Pull_Cursor_But_Keeps_Outbox()
    {
        await using var db = CreateContext();
        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev"));
        var capture = new OfflineCaptureService(db, sessions, devices);

        await sessions.UpsertUxSnapshotAsync(
            "user-a", "alice", "Operario",
            [OfflineStoreConstants.CaptureRecordsPermission],
            "http://localhost:5080");
        await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "1", Neps = 10 });

        db.SyncStates.Add(new SyncState
        {
            Id = 1,
            DeviceId = "dev",
            LastPulledSequence = 42
        });
        await db.SaveChangesAsync();

        var cleared = await sessions.ClearUxSnapshotAsync();
        Assert.Equal(1, cleared.PendingOperationsRetained);
        Assert.Null(await sessions.GetValidSessionAsync());
        Assert.Equal(0, (await db.SyncStates.SingleAsync()).LastPulledSequence);
        Assert.Equal(PendingOperationStatus.Pending, (await db.PendingOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task User_Switch_Resets_Pull_Cursor()
    {
        await using var db = CreateContext();
        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        db.SyncStates.Add(new SyncState { Id = 1, DeviceId = "dev", LastPulledSequence = 99 });
        await db.SaveChangesAsync();

        await sessions.UpsertUxSnapshotAsync(
            "user-a", "a", "Operario", [OfflineStoreConstants.CaptureRecordsPermission], null);
        await sessions.UpsertUxSnapshotAsync(
            "user-b", "b", "Operario", [OfflineStoreConstants.CaptureRecordsPermission], null);

        Assert.Equal(0, (await db.SyncStates.SingleAsync()).LastPulledSequence);
        Assert.Equal("user-b", (await sessions.GetValidSessionAsync())!.UserId);
    }

    [Fact]
    public async Task GetLocalRecord_Does_Not_Expose_Other_User_Row()
    {
        await using var db = CreateContext();
        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        var devices = new FileDeviceIdStore(Path.Combine(_dir, "dev2"));
        var capture = new OfflineCaptureService(db, sessions, devices);

        await sessions.UpsertUxSnapshotAsync(
            "user-a", "a", "Operario", [OfflineStoreConstants.CaptureRecordsPermission], null);
        var created = await capture.CreateRecordAsync(new OfflineCreateRecordRequest { Telar = "A", Neps = 5 });

        await sessions.UpsertUxSnapshotAsync(
            "user-b", "b", "Operario", [OfflineStoreConstants.CaptureRecordsPermission], null);
        var foreign = await capture.GetLocalRecordAsync(created.Record.Id);
        Assert.Null(foreign);
    }

    [Fact]
    public void ValidateCreateRequest_Rejects_Oversized_Observacion()
    {
        var req = new CreateNepRecordRequest
        {
            Telar = "T1",
            Neps = 1,
            Observacion = new string('x', 2001)
        };
        var ex = Assert.Throws<ArgumentException>(() => NepRecordService.ValidateCreateRequest(req));
        Assert.Contains("2000", ex.Message);
    }

    [Fact]
    public void SyncCreatePayload_Does_Not_Declare_CreatedByUserId()
    {
        var props = typeof(RegNeps.Application.Sync.SyncCreateRecordPayload)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("CreatedByUserId", props);
        Assert.DoesNotContain("OwnerUserId", props);
        Assert.DoesNotContain("ActorUserId", props);
    }

    [Fact]
    public void Extra_CreatedByUserId_In_Json_Is_Ignored_By_Create_Payload()
    {
        const string json = """
            {"telar":"T1","neps":10,"createdByUserId":"attacker","ownerUserId":"attacker"}
            """;
        var payload = JsonSerializer.Deserialize<RegNeps.Application.Sync.SyncCreateRecordPayload>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(payload);
        Assert.Equal("T1", payload!.Telar);
        Assert.Equal(10, payload.Neps);
        // No propiedad en DTO → no hay forma de inyectar identidad vía payload tipado.
        Assert.Null(typeof(RegNeps.Application.Sync.SyncCreateRecordPayload).GetProperty("CreatedByUserId"));
    }

    [Fact]
    public void PageSize_Is_Clamped_By_SyncProtocol()
    {
        Assert.Equal(SyncConstants.DefaultPageSize, SyncProtocol.NormalizePageSize(null));
        Assert.Equal(SyncConstants.MaxPageSize, SyncProtocol.NormalizePageSize(500_000));
        Assert.Equal(SyncConstants.DefaultPageSize, SyncProtocol.NormalizePageSize(0));
        Assert.Equal(SyncConstants.MinPageSize, SyncProtocol.NormalizePageSize(1));
    }

    [Fact]
    public async Task PermissionsCsv_Is_Ux_Only_Hint_Not_Authority_Marker()
    {
        // Documenta contrato: HasPermission local no implica autorización de servidor.
        await using var db = CreateContext();
        var sessions = new OfflineSessionService(db, new MemorySecureAuthMaterialStore());
        await sessions.UpsertUxSnapshotAsync(
            "user-a",
            "a",
            "Operario",
            ["CaptureRecords", "ClearAllRecords"], // hint falso: Operario no tiene ClearAll en servidor
            null);
        var session = await sessions.GetValidSessionAsync();
        Assert.NotNull(session);
        Assert.True(sessions.HasPermission(session!, "ClearAllRecords"));
        // La autoridad real sigue en servidor; este test solo prueba que el CSV es un hint local.
        Assert.Contains("ClearAllRecords", session!.PermissionsCsv);
    }
}
