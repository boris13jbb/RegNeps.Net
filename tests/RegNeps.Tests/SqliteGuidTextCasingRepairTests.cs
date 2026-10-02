using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
using RegNeps.Infrastructure.Persistence;
using Xunit;

namespace RegNeps.Tests;

/// <summary>
/// EF Core en SQLite persiste Guid como TEXT en MAYÚSCULAS. Un INSERT crudo con
/// <c>Guid.ToString("D")</c> (minúsculas) o hex sin guiones rompe UPDATE/joins por Id.
/// </summary>
public sealed class SqliteGuidTextCasingRepairTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"regneps-guid-case-{Guid.NewGuid():N}.db");

    // Contiene letras a-f: el casing sí importa en SQLite (case-sensitive).
    private readonly Guid _roleId = Guid.Parse("a1b2c3d4-e5f6-4789-abcd-ef0123456789");
    private readonly Guid _userId = Guid.Parse("f0e1d2c3-b4a5-4687-9abc-def012345678");
    private readonly Guid _auditId = Guid.Parse("abcdef01-2345-4678-9abc-def012345678");

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
    public async Task Lowercase_Dashed_Role_Guids_Normalized_Idempotently_Including_Audits()
    {
        await CreateDbWithLowercaseRoleGuidsAsync(dashed: true);

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(conn)
            .Options;

        // Pre-arreglo: UPDATE de rol falla (evidencia del bug).
        await using (var db = new RegNepsDbContext(options))
        {
            var role = await db.Roles.SingleAsync(r => r.Code == "case_role");
            role.Name = "renamed";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        }

        await using (var db = new RegNepsDbContext(options))
        {
            await RoleSchemaPatches.RepairSqliteGuidTextCasingAsync(db);
        }

        var expected = _roleId.ToString("D").ToUpperInvariant();
        AssertRoleGuidColumns(conn, expected, expectedAuditPk: _auditId.ToString("D").ToUpperInvariant());

        // Idempotente: segunda pasada no rompe ni deja minúsculas.
        await using (var db = new RegNepsDbContext(options))
        {
            await RoleSchemaPatches.RepairSqliteGuidTextCasingAsync(db);
        }

        AssertRoleGuidColumns(conn, expected, expectedAuditPk: _auditId.ToString("D").ToUpperInvariant());
        Assert.Equal(0, CountLowercaseDashedRoleGuids(conn));

        await using (var db = new RegNepsDbContext(options))
        {
            var role = await db.Roles.SingleAsync(r => r.Code == "case_role");
            role.Name = "renamed-ok";
            await db.SaveChangesAsync();
            Assert.Equal("renamed-ok", (await db.Roles.SingleAsync(r => r.Code == "case_role")).Name);
        }
    }

    /// <summary>
    /// Regresión: un UPDATE fila-a-fila con parámetro Guid (MAYÚSCULAS) no matchea
    /// RoleId en minúsculas en SQLite; <c>upper(col)</c> debe normalizar TODAS las filas.
    /// </summary>
    [Fact]
    public async Task Bulk_Lowercase_RolePermission_RoleIds_All_Uppercased_In_One_Pass()
    {
        await CreateDbWithLowercaseRoleGuidsAsync(dashed: true);

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var roleLower = _roleId.ToString("D"); // minúsculas
        var now = DateTime.UtcNow.ToString("o");

        // Muchas filas FK en minúsculas (el bug de producción dejaba ~30+ sin tocar).
        for (var perm = 3; perm <= 40; perm++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR IGNORE INTO "RolePermissions"
                    ("RoleId", "Permission", "IsEnabled", "CreatedAt", "UpdatedAt", "UpdatedByUserId")
                VALUES ($id, $p, 1, $now, $now, NULL);
                """;
            cmd.Parameters.AddWithValue("$id", roleLower);
            cmd.Parameters.AddWithValue("$p", perm);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.True(CountLowercaseDashedRoleGuids(conn) > 30);

        var options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(conn)
            .Options;

        await using (var db = new RegNepsDbContext(options))
        {
            await RoleSchemaPatches.RepairSqliteGuidTextCasingAsync(db);
        }

        var expected = _roleId.ToString("D").ToUpperInvariant();
        AssertRoleGuidColumns(conn, expected, expectedAuditPk: _auditId.ToString("D").ToUpperInvariant());
        Assert.Equal(0, CountLowercaseDashedRoleGuids(conn));

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT COUNT(*) FROM "RolePermissions"
                WHERE "RoleId" = $e
                """;
            cmd.Parameters.AddWithValue("$e", expected);
            var matched = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.True(matched >= 30, $"expected >=30 RolePermissions with UPPER RoleId, got {matched}");
        }
    }

    [Fact]
    public async Task Undashed_Role_Guids_Become_Uppercase_Dashed_In_All_Three_Tables()
    {
        await CreateDbWithLowercaseRoleGuidsAsync(dashed: false);

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(conn)
            .Options;

        // Sin guiones: el join EF RolePermissions↔Roles falla / UPDATE por Id falla.
        await using (var db = new RegNepsDbContext(options))
        {
            var role = await db.Roles.SingleAsync(r => r.Code == "case_role");
            role.Name = "x";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        }

        await using (var db = new RegNepsDbContext(options))
        {
            await DatabaseInitializer.ApplySchemaPatchesAsync(db);
        }

        var expected = _roleId.ToString("D").ToUpperInvariant();
        // RoleId en las tres tablas (el PK de auditoría puede seguir en formato N si no era Roles.Id).
        AssertRoleGuidColumns(conn, expected, expectedAuditPk: null);
        Assert.Equal(0, CountUndashedRoleIds(conn));
        Assert.Equal(0, CountLowercaseDashedRoleGuids(conn));
    }

    [Fact]
    public async Task Lowercase_Dashed_Guids_Break_Ef_Update_Until_Casing_Repair()
    {
        await CreateDbWithLowercaseRoleGuidsAsync(dashed: true, includeUser: true);

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(conn)
            .Options;

        await using (var db = new RegNepsDbContext(options))
        {
            var user = await db.Users.SingleAsync(u => u.Username == "case_user");
            Assert.Equal(_userId, user.Id);
            user.LastLoginAt = DateTime.UtcNow;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        }

        await using (var db = new RegNepsDbContext(options))
        {
            await DatabaseInitializer.ApplySchemaPatchesAsync(db);
        }

        await using (var check = conn.CreateCommand())
        {
            check.CommandText = """SELECT "Id" FROM "Users" WHERE "Username" = 'case_user'""";
            var userRaw = Convert.ToString(await check.ExecuteScalarAsync());
            Assert.Equal(_userId.ToString("D").ToUpperInvariant(), userRaw);
        }

        await using (var db = new RegNepsDbContext(options))
        {
            var user = await db.Users.SingleAsync(u => u.Username == "case_user");
            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task SeedSystemRolesSqlite_Writes_Uppercase_Guid_Text()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(conn)
            .Options;

        await using (var db = new RegNepsDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await using (var clear = conn.CreateCommand())
            {
                clear.CommandText = """DELETE FROM "RolePermissions"; DELETE FROM "Roles";""";
                await clear.ExecuteNonQueryAsync();
            }

            await RoleSchemaPatches.SeedSystemRolesSqliteAsync(db);
        }

        var lowercaseHits = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT "Id" FROM "Roles"
                WHERE "IsSystem" = 1
                  AND "Id" GLOB '*[a-f]*'
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lowercaseHits++;
            }
        }

        Assert.Equal(0, lowercaseHits);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """SELECT COUNT(*) FROM "Roles" WHERE "IsSystem" = 1""";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(5, count);
        }
    }

    private static void AssertRoleGuidColumns(
        SqliteConnection conn,
        string expectedRoleId,
        string? expectedAuditPk)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Id" FROM "Roles" WHERE "Code" = 'case_role'""";
        Assert.Equal(expectedRoleId, Convert.ToString(cmd.ExecuteScalar()));

        cmd.CommandText = """SELECT "RoleId" FROM "RolePermissions" WHERE "Permission" = 2 LIMIT 1""";
        Assert.Equal(expectedRoleId, Convert.ToString(cmd.ExecuteScalar()));

        cmd.CommandText = """SELECT "RoleId" FROM "RolePermissionAudits" LIMIT 1""";
        Assert.Equal(expectedRoleId, Convert.ToString(cmd.ExecuteScalar()));

        if (expectedAuditPk is not null)
        {
            cmd.CommandText = """SELECT "Id" FROM "RolePermissionAudits" LIMIT 1""";
            Assert.Equal(expectedAuditPk, Convert.ToString(cmd.ExecuteScalar()));
        }
    }

    private static int CountLowercaseDashedRoleGuids(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT
              (SELECT COUNT(*) FROM "Roles" WHERE length("Id")=36 AND instr("Id",'-')>0 AND "Id" GLOB '*[a-f]*')
            + (SELECT COUNT(*) FROM "RolePermissions" WHERE length("RoleId")=36 AND instr("RoleId",'-')>0 AND "RoleId" GLOB '*[a-f]*')
            + (SELECT COUNT(*) FROM "RolePermissionAudits" WHERE length("RoleId")=36 AND instr("RoleId",'-')>0 AND "RoleId" GLOB '*[a-f]*')
            """;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int CountUndashedRoleIds(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT
              (SELECT COUNT(*) FROM "Roles" WHERE length("Id")=32 AND instr("Id",'-')=0)
            + (SELECT COUNT(*) FROM "RolePermissions" WHERE length("RoleId")=32 AND instr("RoleId",'-')=0)
            + (SELECT COUNT(*) FROM "RolePermissionAudits" WHERE length("RoleId")=32 AND instr("RoleId",'-')=0)
            """;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private async Task CreateDbWithLowercaseRoleGuidsAsync(bool dashed, bool includeUser = false)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(conn)
            .Options;

        await using (var db = new RegNepsDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var now = DateTime.UtcNow.ToString("o");
        var roleRaw = dashed ? _roleId.ToString("D") : _roleId.ToString("N");
        var auditRaw = dashed ? _auditId.ToString("D") : _auditId.ToString("N");
        Assert.Contains(roleRaw, char.IsAsciiLetterLower);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO "Roles"
                    ("Id", "Code", "Name", "IsActive", "IsSystem", "SeesAllRecords", "CreatedAt", "UpdatedAt")
                VALUES ($id, 'case_role', 'Case Role', 1, 0, 0, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$id", roleRaw);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO "RolePermissions"
                    ("RoleId", "Permission", "IsEnabled", "CreatedAt", "UpdatedAt", "UpdatedByUserId")
                VALUES ($id, 2, 1, $now, $now, NULL);
                """;
            cmd.Parameters.AddWithValue("$id", roleRaw);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO "RolePermissionAudits"
                    ("Id", "RoleId", "Permission", "PreviousValue", "NewValue", "ModifiedByUserId", "ChangedAt")
                VALUES ($aid, $rid, 2, 0, 1, NULL, $now);
                """;
            cmd.Parameters.AddWithValue("$aid", auditRaw);
            cmd.Parameters.AddWithValue("$rid", roleRaw);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        if (includeUser)
        {
            var userLower = _userId.ToString("D");
            Assert.Contains(userLower, char.IsAsciiLetterLower);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO "Users"
                    ("Id", "Username", "DisplayName", "PasswordHash", "Role", "RoleCode",
                     "IsActive", "IsSuperAdmin", "CreatedAt")
                VALUES ($id, 'case_user', 'case', 'hash', 0, 'Operario', 1, 0, $now);
                """;
            cmd.Parameters.AddWithValue("$id", userLower);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
