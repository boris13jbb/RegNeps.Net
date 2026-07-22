using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RegNeps.Infrastructure.Migration;

namespace RegNeps.Infrastructure.Persistence;

/// <summary>Crea la BD, aplica parches SQLite y repara ownership de registros migrados.</summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RegNepsDbContext>();
        await db.Database.EnsureCreatedAsync();
        await ApplySqlitePatchesAsync(db);
        await DbSeeder.SeedAsync(db);

        var migration = scope.ServiceProvider.GetRequiredService<HistoricalDataMigrationService>();
        var repaired = await migration.RepairRecordOwnershipAsync();
        if (repaired > 0)
        {
            Console.WriteLine($"[RegNeps] Reparados {repaired} registros (ownership Firebase → SQL).");
        }
    }

    private static async Task ApplySqlitePatchesAsync(RegNepsDbContext db)
    {
        if (!db.Database.IsSqlite())
        {
            return;
        }

        // Solo añadir ExternalUserId si aún no existe (evita el log fail: de EF en cada arranque).
        if (await SqliteColumnExistsAsync(db, "Users", "ExternalUserId"))
        {
            return;
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "Users" ADD COLUMN "ExternalUserId" TEXT NULL""");
        }
        catch (Exception ex) when (
            ex is SqliteException ||
            ex.InnerException is SqliteException ||
            ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase) ||
            (ex.InnerException?.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            // Condición de carrera / BD ya parchada.
        }
    }

    private static async Task<bool> SqliteColumnExistsAsync(RegNepsDbContext db, string table, string column)
    {
        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        // PRAGMA no admite parámetros de nombre de tabla; valores fijos del código.
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader["name"]?.ToString();
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
