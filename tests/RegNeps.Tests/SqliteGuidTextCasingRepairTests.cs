using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Infrastructure.Persistence;
using Xunit;

namespace RegNeps.Tests;

/// <summary>
/// EF Core en SQLite persiste Guid como TEXT en MAYÚSCULAS. Un INSERT crudo con
/// <c>Guid.ToString("D")</c> (minúsculas) hace que SaveChanges/UPDATE por Id afecte 0 filas.
/// </summary>
public sealed class SqliteGuidTextCasingRepairTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"regneps-guid-case-{Guid.NewGuid():N}.db");

    // Contiene letras a-f: el casing sí importa en SQLite (case-sensitive).
    private readonly Guid _roleId = Guid.Parse("a1b2c3d4-e5f6-4789-abcd-ef0123456789");
    private readonly Guid _userId = Guid.Parse("f0e1d2c3-b4a5-4687-9abc-def012345678");

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
    public async Task Lowercase_Dashed_Guids_Break_Ef_Update_Until_Casing_Repair()
    {
        await CreateDbWithLowercaseGuidsAsync();

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(conn)
            .Options;

        // Evidencia pre-arreglo: EF carga por Username pero falla al UPDATE por Id.
        await using (var db = new RegNepsDbContext(options))
        {
            var user = await db.Users.SingleAsync(u => u.Username == "case_user");
            Assert.Equal(_userId, user.Id);
            user.LastLoginAt = DateTime.UtcNow;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        }

        await using (var db = new RegNepsDbContext(options))
        {
            var role = await db.Roles.SingleAsync(r => r.Code == "case_role");
            Assert.Equal(_roleId, role.Id);
            role.Name = "renamed";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        }

        // Parche (incluye RepairSqliteGuidTextCasingAsync).
        await using (var db = new RegNepsDbContext(options))
        {
            await DatabaseInitializer.ApplySchemaPatchesAsync(db);
        }

        // Disco: mayúsculas (mismo casing que EF).
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = """SELECT "Id" FROM "Users" WHERE "Username" = 'case_user'""";
            var userRaw = Convert.ToString(await check.ExecuteScalarAsync());
            Assert.Equal(_userId.ToString("D").ToUpperInvariant(), userRaw);

            check.CommandText = """SELECT "Id" FROM "Roles" WHERE "Code" = 'case_role'""";
            var roleRaw = Convert.ToString(await check.ExecuteScalarAsync());
            Assert.Equal(_roleId.ToString("D").ToUpperInvariant(), roleRaw);

            check.CommandText =
                """SELECT "RoleId" FROM "RolePermissions" WHERE "RoleId" = $id LIMIT 1""";
            check.Parameters.Clear();
            check.Parameters.AddWithValue("$id", _roleId.ToString("D").ToUpperInvariant());
            var rp = Convert.ToString(await check.ExecuteScalarAsync());
            Assert.Equal(_roleId.ToString("D").ToUpperInvariant(), rp);
        }

        // Post-arreglo: UPDATE por Id funciona.
        await using (var db = new RegNepsDbContext(options))
        {
            var user = await db.Users.SingleAsync(u => u.Username == "case_user");
            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await using (var db = new RegNepsDbContext(options))
        {
            var role = await db.Roles.SingleAsync(r => r.Code == "case_role");
            role.Name = "renamed-ok";
            await db.SaveChangesAsync();
            Assert.Equal("renamed-ok", (await db.Roles.SingleAsync(r => r.Code == "case_role")).Name);
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

    private async Task CreateDbWithLowercaseGuidsAsync()
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
        var roleLower = _roleId.ToString("D"); // minúsculas — bug histórico del seed crudo
        var userLower = _userId.ToString("D");
        Assert.Contains(roleLower, char.IsAsciiLetterLower);
        Assert.Contains(userLower, char.IsAsciiLetterLower);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO "Roles"
                    ("Id", "Code", "Name", "IsActive", "IsSystem", "SeesAllRecords", "CreatedAt", "UpdatedAt")
                VALUES ($id, 'case_role', 'Case Role', 1, 0, 0, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$id", roleLower);
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
            cmd.Parameters.AddWithValue("$id", roleLower);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
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
