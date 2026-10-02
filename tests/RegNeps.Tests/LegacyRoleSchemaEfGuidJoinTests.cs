using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Permissions;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;
using Xunit;

namespace RegNeps.Tests;

/// <summary>
/// Tras migrar BD legacy, valida con EF (sin SQL crudo) que los Guid de Roles
/// casan con RolePermissions y que PermissionService carga la matriz esperada.
/// Falla si Roles.Id se sembró con lower(hex(randomblob(16))) sin guiones.
/// </summary>
public sealed class LegacyRoleSchemaEfGuidJoinTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"regneps-ef-guid-{Guid.NewGuid():N}.db");

    private readonly Guid _superId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _operarioId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly Guid _supervisorId = Guid.Parse("33333333-3333-3333-3333-333333333333");

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
    public async Task Legacy_Migration_Ef_Joins_And_PermissionService_Use_Dashed_Guids()
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

        // 1) Cinco roles de sistema materializados por EF.
        var roles = await verify.Roles.AsNoTracking().Where(r => r.IsSystem).ToListAsync();
        Assert.Equal(SystemRoleCodes.Definitions.Count, roles.Count);
        Assert.All(SystemRoleCodes.Definitions, def =>
            Assert.Contains(roles, r => r.Code == def.Code));

        // 2) Formato TEXT con guiones (el que escribe EF Core en SQLite).
        await using (var fmt = conn.CreateCommand())
        {
            fmt.CommandText = """SELECT "Id" FROM "Roles" WHERE "Code" = 'Supervisor' LIMIT 1""";
            var rawId = Convert.ToString(await fmt.ExecuteScalarAsync());
            Assert.False(string.IsNullOrWhiteSpace(rawId));
            Assert.Contains('-', rawId!);
            Assert.Equal(36, rawId!.Length);
            Assert.True(Guid.TryParse(rawId, out _), $"Id no parseable como Guid: {rawId}");
        }

        // 3) Join EF RolePermissions ↔ Roles (SQL; falla si RoleId hex no casa con Guid con guiones).
        var supervisorRole = await verify.Roles.AsNoTracking()
            .SingleAsync(r => r.Code == SystemRoleCodes.Supervisor);
        var linkedCount = await verify.RolePermissions.AsNoTracking()
            .CountAsync(rp => rp.RoleId == supervisorRole.Id);
        Assert.True(
            linkedCount >= RolePermissions.ForRole(AppUserRole.Supervisor).Count,
            $"EF no une RolePermissions con Role (count={linkedCount}). ¿Guid hex sin guiones?");

        var joined = await (
            from rp in verify.RolePermissions.AsNoTracking()
            join r in verify.Roles.AsNoTracking() on rp.RoleId equals r.Id
            where r.Code == SystemRoleCodes.Supervisor
            select new { rp.Permission, rp.IsEnabled, RoleCode = r.Code }).ToListAsync();
        Assert.NotEmpty(joined);
        Assert.All(joined, row => Assert.Equal(SystemRoleCodes.Supervisor, row.RoleCode));
        Assert.Contains(joined, row => row.Permission == AppPermission.ExportReports && !row.IsEnabled);
        Assert.Contains(joined, row => row.Permission == AppPermission.ManageReports && row.IsEnabled);

        // 4) PermissionService.ReloadAsync (vía RefreshMatrixAsync) — permisos editados intactos.
        var permissions = new PermissionService(
            new RolePermissionRepository(verify),
            new RoleRepository(verify),
            new PermissionMatrix());
        await permissions.RefreshMatrixAsync();

        Assert.False(permissions.HasPermissionByRoleCode(
            SystemRoleCodes.Supervisor, false, true, AppPermission.ExportReports));
        Assert.True(permissions.HasPermissionByRoleCode(
            SystemRoleCodes.Supervisor, false, true, AppPermission.ManageReports));
        Assert.True(permissions.HasPermissionByRoleCode(
            SystemRoleCodes.Operario, false, true, AppPermission.CaptureRecords));
        Assert.False(permissions.HasPermissionByRoleCode(
            SystemRoleCodes.Operario, false, true, AppPermission.ExportReports));

        // 5) Usuarios resuelven rol por RoleCode.
        var super = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == _superId);
        var operario = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == _operarioId);
        var supervisor = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == _supervisorId);
        Assert.Equal(SystemRoleCodes.SuperAdmin, super.EffectiveRoleCode);
        Assert.Equal(SystemRoleCodes.Operario, operario.EffectiveRoleCode);
        Assert.Equal(SystemRoleCodes.Supervisor, supervisor.EffectiveRoleCode);
    }

    private async Task CreateLegacyDatabaseAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Mismo esquema legacy mínimo que LegacyRoleSchemaMigrationTests (pre–Fase 6).
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
        await InsertUserAsync(conn, _superId, "ef_legacy_super", 4, true, hash, now);
        await InsertUserAsync(conn, _operarioId, "ef_legacy_op", 0, false, hash, now);
        await InsertUserAsync(conn, _supervisorId, "ef_legacy_sup", 1, false, hash, now);

        foreach (var perm in RolePermissions.ForRole(AppUserRole.Supervisor))
        {
            var enabled = perm == AppPermission.ExportReports ? 0 : 1;
            await InsertRolePermissionAsync(conn, role: 1, (int)perm, enabled, now);
        }

        await using (var alert = conn.CreateCommand())
        {
            alert.CommandText =
                """
                INSERT INTO "AlertConfigs"
                    ("Id", "LimiteNormalMax", "LimiteAdvertenciaMax", "CantidadReincidenciasCriticas", "DiasParaReincidencia", "AlertasActivas")
                VALUES (1, 30, 60, 3, 1, 1);
                """;
            await alert.ExecuteNonQueryAsync();
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
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
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
