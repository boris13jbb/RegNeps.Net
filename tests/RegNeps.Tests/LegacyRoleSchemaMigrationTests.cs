using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
using RegNeps.Infrastructure.Persistence;
using Xunit;

namespace RegNeps.Tests;

/// <summary>
/// Simula BD anterior a Fase 6 (sin tabla Roles, sin Users.RoleCode, RolePermissions por enum)
/// y verifica que RoleSchemaPatches + seed son idempotentes.
/// Esquema SQL derivado de origin/main (RegNepsDbContext + RolePermission por AppUserRole).
/// </summary>
public sealed class LegacyRoleSchemaMigrationTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"regneps-legacy-{Guid.NewGuid():N}.db");

    private readonly Guid _superId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _operarioId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly Guid _supervisorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly Guid _reportId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { /* temp */ }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Legacy_Schema_Migrates_Idempotently_Preserving_Edited_Permissions()
    {
        await CreateLegacyDatabaseAsync();

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(conn)
            .Options;

        await using (var db = new RegNepsDbContext(options))
        {
            await DatabaseInitializer.ApplySchemaPatchesAsync(db);
            await DbSeeder.SeedAsync(db);
            await DatabaseInitializer.ApplySchemaPatchesAsync(db);
            await DbSeeder.SeedAsync(db);
        }

        await using var verify = new RegNepsDbContext(options);
        var roles = await verify.Roles.AsNoTracking().ToListAsync();
        Assert.Equal(5, roles.Count);
        Assert.All(SystemRoleCodes.Definitions, def =>
            Assert.Contains(roles, r => r.Code == def.Code && r.IsSystem));

        var roleCounts = await verify.Roles.GroupBy(r => r.Code).Select(g => g.Count()).ToListAsync();
        Assert.All(roleCounts, c => Assert.Equal(1, c));

        var super = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == _superId);
        var operario = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == _operarioId);
        var supervisor = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == _supervisorId);
        Assert.Equal(SystemRoleCodes.SuperAdmin, super.RoleCode);
        Assert.Equal(SystemRoleCodes.Operario, operario.RoleCode);
        Assert.Equal(SystemRoleCodes.Supervisor, supervisor.RoleCode);
        Assert.Equal(AppUserRole.Supervisor, supervisor.Role);

        // Lectura cruda: evita sorpresas de materialización Guid/enum tras el rebuild SQL.
        await using (var raw = conn.CreateCommand())
        {
            raw.CommandText =
                """
                SELECT rp."Permission", rp."IsEnabled"
                FROM "RolePermissions" rp
                INNER JOIN "Roles" r ON r."Id" = rp."RoleId"
                WHERE r."Code" = 'Supervisor'
                ORDER BY rp."Permission";
                """;
            var map = new Dictionary<int, int>();
            await using var reader = await raw.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                map[reader.GetInt32(0)] = reader.GetInt32(1);
            }

            Assert.True(map.Count >= RolePermissions.ForRole(AppUserRole.Supervisor).Count, $"count={map.Count}");
            Assert.True(map.ContainsKey((int)AppPermission.ExportReports), "ExportReports ausente tras migración");
            Assert.Equal(0, map[(int)AppPermission.ExportReports]);
            Assert.Equal(1, map[(int)AppPermission.ManageReports]);
        }

        var report = await verify.SavedReports.AsNoTracking().SingleAsync(r => r.Id == _reportId);
        Assert.Equal(_supervisorId.ToString(), report.CreatedByUserId);
        Assert.Equal("Informe legacy", report.Name);
    }

    private async Task CreateLegacyDatabaseAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Esquema anterior a Fase 6 (sin Roles, sin Users.RoleCode). Fuente: origin/main DbContext.
        cmd.CommandText =
            """
            CREATE TABLE "Users" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY,
                "Username" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "Email" TEXT NULL,
                "PasswordHash" TEXT NOT NULL,
                "Role" INTEGER NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "IsSuperAdmin" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NULL,
                "LastLoginAt" TEXT NULL,
                "DeletedAt" TEXT NULL,
                "ExternalUserId" TEXT NULL
            );
            CREATE UNIQUE INDEX "IX_Users_Username" ON "Users" ("Username");

            CREATE TABLE "RolePermissions" (
                "Role" INTEGER NOT NULL,
                "Permission" INTEGER NOT NULL,
                "IsEnabled" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "UpdatedByUserId" TEXT NULL,
                CONSTRAINT "PK_RolePermissions" PRIMARY KEY ("Role", "Permission")
            );

            CREATE TABLE "RolePermissionAudits" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_RolePermissionAudits" PRIMARY KEY,
                "Role" INTEGER NOT NULL,
                "Permission" INTEGER NOT NULL,
                "PreviousValue" INTEGER NOT NULL,
                "NewValue" INTEGER NOT NULL,
                "ModifiedByUserId" TEXT NULL,
                "ChangedAt" TEXT NOT NULL
            );

            CREATE TABLE "SavedReports" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SavedReports" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CreatedByUserId" TEXT NULL,
                "CreatedByName" TEXT NULL,
                "FiltersJson" TEXT NOT NULL,
                "RecordCount" INTEGER NOT NULL,
                "SummaryText" TEXT NULL,
                "SnapshotJson" TEXT NULL
            );

            CREATE TABLE "AlertConfigs" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AlertConfigs" PRIMARY KEY,
                "LimiteNormalMax" INTEGER NOT NULL,
                "LimiteAdvertenciaMax" INTEGER NOT NULL,
                "CantidadReincidenciasCriticas" INTEGER NOT NULL,
                "DiasParaReincidencia" INTEGER NOT NULL,
                "AlertasActivas" INTEGER NOT NULL
            );

            CREATE TABLE "Fabrics" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Fabrics" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Code" TEXT NULL,
                "IsActive" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );

            CREATE TABLE "LoteTramaItems" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_LoteTramaItems" PRIMARY KEY,
                "Code" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );

            CREATE TABLE "NepRecords" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_NepRecords" PRIMARY KEY,
                "Telar" TEXT NOT NULL,
                "Tela" TEXT NOT NULL,
                "LoteTrama" TEXT NOT NULL,
                "Neps" REAL NOT NULL,
                "Turno" TEXT NULL,
                "Operario" TEXT NULL,
                "Observacion" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CreatedByUserId" TEXT NULL,
                "CreatedByName" TEXT NULL,
                "CreatedByRole" TEXT NULL,
                "ClientOperationId" TEXT NULL,
                "CaptureSessionId" TEXT NULL,
                "ConcurrencyStamp" TEXT NOT NULL DEFAULT ''
            );
            """;
        await cmd.ExecuteNonQueryAsync();

        var hash = BCrypt.Net.BCrypt.HashPassword("TestOnly123!");
        var now = DateTime.UtcNow.ToString("o");

        await InsertUserAsync(conn, _superId, "legacy_super", 4, isSuper: true, hash, now);
        await InsertUserAsync(conn, _operarioId, "legacy_op", 0, isSuper: false, hash, now);
        await InsertUserAsync(conn, _supervisorId, "legacy_sup", 1, isSuper: false, hash, now);

        // Semilla mínima de permisos Supervisor + edición: ExportReports desactivado.
        foreach (var perm in RolePermissions.ForRole(AppUserRole.Supervisor))
        {
            var enabled = perm == AppPermission.ExportReports ? 0 : 1;
            await InsertRolePermissionAsync(conn, role: 1, (int)perm, enabled, now);
        }

        await using (var ins = conn.CreateCommand())
        {
            ins.CommandText =
                """
                INSERT INTO "SavedReports"
                    ("Id", "Name", "CreatedAt", "CreatedByUserId", "CreatedByName", "FiltersJson", "RecordCount", "SummaryText", "SnapshotJson")
                VALUES ($id, 'Informe legacy', $now, $uid, 'Supervisor Legacy', '{}', 0, 'legacy', NULL);
                INSERT INTO "AlertConfigs"
                    ("Id", "LimiteNormalMax", "LimiteAdvertenciaMax", "CantidadReincidenciasCriticas", "DiasParaReincidencia", "AlertasActivas")
                VALUES (1, 30, 60, 3, 1, 1);
                """;
            ins.Parameters.AddWithValue("$id", _reportId.ToString());
            ins.Parameters.AddWithValue("$now", now);
            ins.Parameters.AddWithValue("$uid", _supervisorId.ToString());
            await ins.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertUserAsync(
        SqliteConnection conn, Guid id, string username, int role, bool isSuper, string hash, string now)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO "Users"
                ("Id", "Username", "DisplayName", "Email", "PasswordHash", "Role", "IsActive", "IsSuperAdmin", "CreatedAt")
            VALUES ($id, $u, $d, NULL, $h, $role, 1, $super, $now);
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$d", username);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$super", isSuper ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertRolePermissionAsync(
        SqliteConnection conn, int role, int permission, int enabled, string now)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO "RolePermissions"
                ("Role", "Permission", "IsEnabled", "CreatedAt", "UpdatedAt", "UpdatedByUserId")
            VALUES ($role, $perm, $en, $now, $now, NULL);
            """;
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$perm", permission);
        cmd.Parameters.AddWithValue("$en", enabled);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync();
    }
}
