using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Permissions;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Permissions;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;
using Xunit;

namespace RegNeps.Tests;

/// <summary>
/// Transacciones del rebuild SQLite y reparación de Guid hex sin guiones.
/// SQL Server: NO VERIFICABLE aquí (sin LocalDB); ver docs/CHECKLIST_DESPLIEGUE_PARIDAD.md.
/// </summary>
public sealed class RoleSchemaPatchTransactionTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"regneps-role-tx-{Guid.NewGuid():N}.db");

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
    public async Task Sqlite_Rebuild_Rollback_Leaves_Enum_Schema_Intact()
    {
        await CreateLegacyWithRolesHexAsync();

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RegNepsDbContext>().UseSqlite(conn).Options;
        await using var db = new RegNepsDbContext(options);

        Assert.True(await DatabaseInitializer.SqliteColumnExistsAsync(db, "RolePermissions", "Role"));

        var boom = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RoleSchemaPatches.RebuildRolePermissionsSqliteAsync(
                db,
                afterDropAsync: _ => throw new InvalidOperationException("fallo-controlado-post-drop")));

        Assert.Equal("fallo-controlado-post-drop", boom.Message);
        Assert.True(await DatabaseInitializer.SqliteColumnExistsAsync(db, "RolePermissions", "Role"));
        Assert.False(await DatabaseInitializer.SqliteTableExistsAsync(db, "RolePermissions_new"));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT COUNT(*) FROM "RolePermissions" WHERE "Role" = 1""";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.True(count > 0, "Tras ROLLBACK deben seguir los permisos enum del Supervisor.");
    }

    [Fact]
    public async Task Sqlite_Repairs_Undashed_Role_Guids_And_Ef_Join_Works()
    {
        await CreateMigratedDbWithHexRoleIdsAsync();

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RegNepsDbContext>().UseSqlite(conn).Options;

        await using (var db = new RegNepsDbContext(options))
        {
            await DatabaseInitializer.ApplySchemaPatchesAsync(db);
            await DbSeeder.SeedAsync(db);
            await DatabaseInitializer.ApplySchemaPatchesAsync(db);
            await DbSeeder.SeedAsync(db);
        }

        await using var verify = new RegNepsDbContext(options);

        await using (var fmt = conn.CreateCommand())
        {
            fmt.CommandText = """SELECT "Id" FROM "Roles" WHERE "Code" = 'Supervisor' LIMIT 1""";
            var rawId = Convert.ToString(await fmt.ExecuteScalarAsync());
            Assert.False(string.IsNullOrWhiteSpace(rawId));
            Assert.Contains('-', rawId!);
            Assert.Equal(36, rawId!.Length);
        }

        var joined = await (
            from rp in verify.RolePermissions.AsNoTracking()
            join r in verify.Roles.AsNoTracking() on rp.RoleId equals r.Id
            where r.Code == SystemRoleCodes.Supervisor
            select rp).CountAsync();
        Assert.True(joined > 0);

        var permissions = new PermissionService(
            new RolePermissionRepository(verify),
            new RoleRepository(verify),
            new PermissionMatrix());
        await permissions.RefreshMatrixAsync();
        Assert.False(permissions.HasPermissionByRoleCode(
            SystemRoleCodes.Supervisor, false, true, AppPermission.ExportReports));
        Assert.True(permissions.HasPermissionByRoleCode(
            SystemRoleCodes.Supervisor, false, true, AppPermission.ManageReports));
    }

    private async Task CreateLegacyWithRolesHexAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var now = DateTime.UtcNow.ToString("o");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"""
            CREATE TABLE "Roles" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Code" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "IsSystem" INTEGER NOT NULL,
                "SeesAllRecords" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX "IX_Roles_Code" ON "Roles" ("Code");
            CREATE TABLE "RolePermissions" (
                "Role" INTEGER NOT NULL,
                "Permission" INTEGER NOT NULL,
                "IsEnabled" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "UpdatedByUserId" TEXT NULL,
                PRIMARY KEY ("Role", "Permission")
            );
            CREATE TABLE "Users" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Username" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "PasswordHash" TEXT NOT NULL,
                "Role" INTEGER NOT NULL,
                "RoleCode" TEXT NULL,
                "IsActive" INTEGER NOT NULL,
                "IsSuperAdmin" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE TABLE "AlertConfigs" (
                "Id" INTEGER NOT NULL PRIMARY KEY,
                "LimiteNormalMax" INTEGER NOT NULL,
                "LimiteAdvertenciaMax" INTEGER NOT NULL,
                "CantidadReincidenciasCriticas" INTEGER NOT NULL,
                "DiasParaReincidencia" INTEGER NOT NULL,
                "AlertasActivas" INTEGER NOT NULL
            );
            INSERT INTO "AlertConfigs" VALUES (1, 30, 60, 3, 1, 1);
            INSERT INTO "Roles" VALUES
                ('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'Operario', 'Operario', 1, 1, 0, '{now}', '{now}'),
                ('bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'Supervisor', 'Supervisor', 1, 1, 1, '{now}', '{now}'),
                ('cccccccccccccccccccccccccccccccc', 'Admin', 'Admin', 1, 1, 1, '{now}', '{now}'),
                ('dddddddddddddddddddddddddddddddd', 'Gerencia', 'Gerencia', 1, 1, 1, '{now}', '{now}'),
                ('eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', 'SuperAdmin', 'SuperAdmin', 1, 1, 1, '{now}', '{now}');
            INSERT INTO "RolePermissions" ("Role", "Permission", "IsEnabled", "CreatedAt", "UpdatedAt")
            VALUES (1, {(int)AppPermission.ExportReports}, 0, '{now}', '{now}'),
                   (1, {(int)AppPermission.ManageReports}, 1, '{now}', '{now}'),
                   (1, {(int)AppPermission.ViewRecords}, 1, '{now}', '{now}');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// BD ya migrada a RoleId con Guid hex (semilla antigua), sin columna Role.
    /// </summary>
    private async Task CreateMigratedDbWithHexRoleIdsAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var now = DateTime.UtcNow.ToString("o");
        const string hexSup = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string hexOp = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"""
                CREATE TABLE "Roles" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "Code" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "IsActive" INTEGER NOT NULL,
                    "IsSystem" INTEGER NOT NULL,
                    "SeesAllRecords" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX "IX_Roles_Code" ON "Roles" ("Code");
                CREATE TABLE "RolePermissions" (
                    "RoleId" TEXT NOT NULL,
                    "Permission" INTEGER NOT NULL,
                    "IsEnabled" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "UpdatedByUserId" TEXT NULL,
                    PRIMARY KEY ("RoleId", "Permission")
                );
                CREATE TABLE "RolePermissionAudits" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "RoleId" TEXT NOT NULL,
                    "Permission" INTEGER NOT NULL,
                    "PreviousValue" INTEGER NOT NULL,
                    "NewValue" INTEGER NOT NULL,
                    "ModifiedByUserId" TEXT NULL,
                    "ChangedAt" TEXT NOT NULL
                );
                CREATE TABLE "Users" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "Username" TEXT NOT NULL,
                    "DisplayName" TEXT NOT NULL,
                    "Email" TEXT NULL,
                    "PasswordHash" TEXT NOT NULL,
                    "Role" INTEGER NOT NULL,
                    "RoleCode" TEXT NULL,
                    "IsActive" INTEGER NOT NULL,
                    "IsSuperAdmin" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NULL,
                    "LastLoginAt" TEXT NULL,
                    "DeletedAt" TEXT NULL,
                    "ExternalUserId" TEXT NULL
                );
                CREATE TABLE "AlertConfigs" (
                    "Id" INTEGER NOT NULL PRIMARY KEY,
                    "LimiteNormalMax" INTEGER NOT NULL,
                    "LimiteAdvertenciaMax" INTEGER NOT NULL,
                    "CantidadReincidenciasCriticas" INTEGER NOT NULL,
                    "DiasParaReincidencia" INTEGER NOT NULL,
                    "AlertasActivas" INTEGER NOT NULL
                );
                CREATE TABLE "Fabrics" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "Name" TEXT NOT NULL,
                    "Code" TEXT NULL,
                    "IsActive" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                CREATE TABLE "LoteTramaItems" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "Code" TEXT NOT NULL,
                    "IsActive" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                CREATE TABLE "SavedReports" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "Name" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "CreatedByUserId" TEXT NULL,
                    "CreatedByName" TEXT NULL,
                    "FiltersJson" TEXT NOT NULL,
                    "RecordCount" INTEGER NOT NULL,
                    "SummaryText" TEXT NULL,
                    "SnapshotJson" TEXT NULL
                );
                CREATE TABLE "NepRecords" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "Telar" TEXT NOT NULL,
                    "Tela" TEXT NOT NULL,
                    "LoteTrama" TEXT NOT NULL,
                    "Neps" REAL NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "ConcurrencyStamp" TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO "AlertConfigs" VALUES (1, 30, 60, 3, 1, 1);
                INSERT INTO "Roles" VALUES
                    ('{hexOp}', 'Operario', 'Operario', 1, 1, 0, '{now}', '{now}'),
                    ('{hexSup}', 'Supervisor', 'Supervisor', 1, 1, 1, '{now}', '{now}'),
                    ('cccccccccccccccccccccccccccccccc', 'Admin', 'Admin', 1, 1, 1, '{now}', '{now}'),
                    ('dddddddddddddddddddddddddddddddd', 'Gerencia', 'Gerencia', 1, 1, 1, '{now}', '{now}'),
                    ('eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', 'SuperAdmin', 'SuperAdmin', 1, 1, 1, '{now}', '{now}');
                INSERT INTO "RolePermissions" ("RoleId", "Permission", "IsEnabled", "CreatedAt", "UpdatedAt")
                VALUES
                    ('{hexSup}', {(int)AppPermission.ExportReports}, 0, '{now}', '{now}'),
                    ('{hexSup}', {(int)AppPermission.ManageReports}, 1, '{now}', '{now}'),
                    ('{hexSup}', {(int)AppPermission.ViewRecords}, 1, '{now}', '{now}'),
                    ('{hexSup}', {(int)AppPermission.ViewDashboard}, 1, '{now}', '{now}'),
                    ('{hexSup}', {(int)AppPermission.EditRecords}, 1, '{now}', '{now}'),
                    ('{hexSup}', {(int)AppPermission.ViewAlerts}, 1, '{now}', '{now}'),
                    ('{hexSup}', {(int)AppPermission.ApplyCorrectiveAction}, 1, '{now}', '{now}'),
                    ('{hexOp}', {(int)AppPermission.CaptureRecords}, 1, '{now}', '{now}'),
                    ('{hexOp}', {(int)AppPermission.ViewRecords}, 1, '{now}', '{now}');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var user = conn.CreateCommand())
        {
            user.CommandText =
                """
                INSERT INTO "Users"
                    ("Id", "Username", "DisplayName", "PasswordHash", "Role", "RoleCode", "IsActive", "IsSuperAdmin", "CreatedAt")
                VALUES ($id, 'hex_super', 'hex_super', $hash, 4, 'SuperAdmin', 1, 1, $now);
                """;
            user.Parameters.AddWithValue("$id", "11111111-1111-1111-1111-111111111111");
            user.Parameters.AddWithValue("$hash", BCrypt.Net.BCrypt.HashPassword("TestOnly123!"));
            user.Parameters.AddWithValue("$now", now);
            await user.ExecuteNonQueryAsync();
        }
    }
}
