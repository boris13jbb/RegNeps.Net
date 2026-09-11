using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RegNeps.Infrastructure.Migration;

namespace RegNeps.Infrastructure.Persistence;

/// <summary>
/// Crea la BD, aplica parches aditivos (SQLite y SQL Server) y repara ownership migrado.
/// Los parches son idempotentes: reiniciar la app no falla si columnas/índices ya existen.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RegNepsDbContext>();
        await db.Database.EnsureCreatedAsync();
        await ApplySchemaPatchesAsync(db);
        await DbSeeder.SeedAsync(db);

        var migration = scope.ServiceProvider.GetRequiredService<HistoricalDataMigrationService>();
        var repaired = await migration.RepairRecordOwnershipAsync();
        if (repaired > 0)
        {
            Console.WriteLine($"[RegNeps] Reparados {repaired} registros (ownership Firebase → SQL).");
        }
    }

    /// <summary>
    /// Parches aditivos idempotentes. Seguro llamar en cada arranque (dev/test).
    /// No sustituye migraciones formales de producción; no se ejecuta contra prod desde aquí.
    /// </summary>
    public static async Task ApplySchemaPatchesAsync(RegNepsDbContext db)
    {
        if (db.Database.IsSqlite())
        {
            await ApplySqlitePatchesAsync(db);
            return;
        }

        if (db.Database.IsSqlServer())
        {
            await ApplySqlServerPatchesAsync(db);
        }
    }

    private static async Task ApplySqlitePatchesAsync(RegNepsDbContext db)
    {
        if (!await SqliteColumnExistsAsync(db, "Users", "ExternalUserId"))
        {
            await TryExecuteAsync(db, """ALTER TABLE "Users" ADD COLUMN "ExternalUserId" TEXT NULL""");
        }

        if (!await SqliteColumnExistsAsync(db, "SavedReports", "SnapshotJson"))
        {
            await TryExecuteAsync(db, """ALTER TABLE "SavedReports" ADD COLUMN "SnapshotJson" TEXT NULL""");
        }

        if (!await SqliteColumnExistsAsync(db, "NepRecords", "ConcurrencyStamp"))
        {
            await TryExecuteAsync(db,
                """ALTER TABLE "NepRecords" ADD COLUMN "ConcurrencyStamp" TEXT NOT NULL DEFAULT ''""");
        }

        // Backfill stamps vacíos (históricos / default '').
        await TryExecuteAsync(db,
            """UPDATE "NepRecords" SET "ConcurrencyStamp" = lower(hex(randomblob(16))) WHERE "ConcurrencyStamp" IS NULL OR "ConcurrencyStamp" = ''""");

        if (!await SqliteColumnExistsAsync(db, "NepRecords", "ClientOperationId"))
        {
            await TryExecuteAsync(db,
                """ALTER TABLE "NepRecords" ADD COLUMN "ClientOperationId" TEXT NULL""");
        }

        // Índice único parcial: permite muchos históricos con ClientOperationId NULL.
        if (!await SqliteIndexExistsAsync(db, "IX_NepRecords_CreatedBy_ClientOperation"))
        {
            await TryExecuteAsync(db,
                """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_NepRecords_CreatedBy_ClientOperation"
                ON "NepRecords" ("CreatedByUserId", "ClientOperationId")
                WHERE "ClientOperationId" IS NOT NULL AND "ClientOperationId" <> ''
                """);
        }

        if (!await SqliteColumnExistsAsync(db, "NepRecords", "CaptureSessionId"))
        {
            await TryExecuteAsync(db,
                """ALTER TABLE "NepRecords" ADD COLUMN "CaptureSessionId" TEXT NULL""");
        }

        if (!await SqliteIndexExistsAsync(db, "IX_NepRecords_CreatedBy_CaptureSession"))
        {
            await TryExecuteAsync(db,
                """
                CREATE INDEX IF NOT EXISTS "IX_NepRecords_CreatedBy_CaptureSession"
                ON "NepRecords" ("CreatedByUserId", "CaptureSessionId")
                """);
        }

        // Carreras EnsureActive de telas (comparación case-insensitive).
        if (!await SqliteIndexExistsAsync(db, "IX_Fabrics_Name_Unique"))
        {
            await TryExecuteAsync(db,
                """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Fabrics_Name_Unique"
                ON "Fabrics" ("Name" COLLATE NOCASE)
                """);
        }

        await TryExecuteAsync(db,
            """
            CREATE TABLE IF NOT EXISTS "RolePermissions" (
                "Role" INTEGER NOT NULL,
                "Permission" INTEGER NOT NULL,
                "IsEnabled" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "UpdatedByUserId" TEXT NULL,
                CONSTRAINT "PK_RolePermissions" PRIMARY KEY ("Role", "Permission")
            )
            """);

        await TryExecuteAsync(db,
            """
            CREATE TABLE IF NOT EXISTS "RolePermissionAudits" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_RolePermissionAudits" PRIMARY KEY,
                "Role" INTEGER NOT NULL,
                "Permission" INTEGER NOT NULL,
                "PreviousValue" INTEGER NOT NULL,
                "NewValue" INTEGER NOT NULL,
                "ModifiedByUserId" TEXT NULL,
                "ChangedAt" TEXT NOT NULL
            )
            """);

        await TryExecuteAsync(db,
            """
            CREATE INDEX IF NOT EXISTS "IX_RolePermissionAudits_ChangedAt"
            ON "RolePermissionAudits" ("ChangedAt")
            """);
    }

    private static async Task ApplySqlServerPatchesAsync(RegNepsDbContext db)
    {
        if (!await SqlServerColumnExistsAsync(db, "Users", "ExternalUserId"))
        {
            await TryExecuteAsync(db, """ALTER TABLE [Users] ADD [ExternalUserId] nvarchar(128) NULL""");
        }

        if (!await SqlServerColumnExistsAsync(db, "SavedReports", "SnapshotJson"))
        {
            await TryExecuteAsync(db, """ALTER TABLE [SavedReports] ADD [SnapshotJson] nvarchar(max) NULL""");
        }

        if (!await SqlServerColumnExistsAsync(db, "NepRecords", "ConcurrencyStamp"))
        {
            await TryExecuteAsync(db,
                """ALTER TABLE [NepRecords] ADD [ConcurrencyStamp] nvarchar(64) NOT NULL CONSTRAINT DF_NepRecords_ConcurrencyStamp DEFAULT ('')""");
        }

        await TryExecuteAsync(db,
            """
            UPDATE [NepRecords]
            SET [ConcurrencyStamp] = REPLACE(CONVERT(nvarchar(36), NEWID()), '-', '')
            WHERE [ConcurrencyStamp] IS NULL OR [ConcurrencyStamp] = ''
            """);

        if (!await SqlServerColumnExistsAsync(db, "NepRecords", "ClientOperationId"))
        {
            await TryExecuteAsync(db,
                """ALTER TABLE [NepRecords] ADD [ClientOperationId] nvarchar(64) NULL""");
        }

        if (!await SqlServerIndexExistsAsync(db, "NepRecords", "IX_NepRecords_CreatedBy_ClientOperation"))
        {
            await TryExecuteAsync(db,
                """
                CREATE UNIQUE NONCLUSTERED INDEX [IX_NepRecords_CreatedBy_ClientOperation]
                ON [NepRecords] ([CreatedByUserId], [ClientOperationId])
                WHERE [ClientOperationId] IS NOT NULL AND [ClientOperationId] <> ''
                """);
        }

        if (!await SqlServerColumnExistsAsync(db, "NepRecords", "CaptureSessionId"))
        {
            await TryExecuteAsync(db,
                """ALTER TABLE [NepRecords] ADD [CaptureSessionId] nvarchar(64) NULL""");
        }

        if (!await SqlServerIndexExistsAsync(db, "NepRecords", "IX_NepRecords_CreatedBy_CaptureSession"))
        {
            await TryExecuteAsync(db,
                """
                CREATE NONCLUSTERED INDEX [IX_NepRecords_CreatedBy_CaptureSession]
                ON [NepRecords] ([CreatedByUserId], [CaptureSessionId])
                """);
        }

        if (!await SqlServerIndexExistsAsync(db, "Fabrics", "IX_Fabrics_Name_Unique"))
        {
            await TryExecuteAsync(db,
                """
                CREATE UNIQUE NONCLUSTERED INDEX [IX_Fabrics_Name_Unique]
                ON [Fabrics] ([Name])
                """);
        }

        await TryExecuteAsync(db,
            """
            IF OBJECT_ID(N'[RolePermissions]', N'U') IS NULL
            BEGIN
                CREATE TABLE [RolePermissions] (
                    [Role] int NOT NULL,
                    [Permission] int NOT NULL,
                    [IsEnabled] bit NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [UpdatedAt] datetime2 NOT NULL,
                    [UpdatedByUserId] uniqueidentifier NULL,
                    CONSTRAINT [PK_RolePermissions] PRIMARY KEY ([Role], [Permission])
                );
            END
            """);

        await TryExecuteAsync(db,
            """
            IF OBJECT_ID(N'[RolePermissionAudits]', N'U') IS NULL
            BEGIN
                CREATE TABLE [RolePermissionAudits] (
                    [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_RolePermissionAudits] PRIMARY KEY,
                    [Role] int NOT NULL,
                    [Permission] int NOT NULL,
                    [PreviousValue] bit NOT NULL,
                    [NewValue] bit NOT NULL,
                    [ModifiedByUserId] uniqueidentifier NULL,
                    [ChangedAt] datetime2 NOT NULL
                );
                CREATE INDEX [IX_RolePermissionAudits_ChangedAt] ON [RolePermissionAudits] ([ChangedAt]);
            END
            """);
    }

    private static async Task TryExecuteAsync(RegNepsDbContext db, string sql)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch (Exception ex) when (IsAlreadyExistsError(ex))
        {
            // Idempotente: columna/índice ya presente o carrera entre instancias.
        }
    }

    private static bool IsAlreadyExistsError(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException!)
        {
            var msg = cur.Message;
            if (msg.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("There is already an object named", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("duplicate key name", StringComparison.OrdinalIgnoreCase)
                || (msg.Contains("unique index", StringComparison.OrdinalIgnoreCase)
                    && msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // SQLite: "table X has no column named Y" no es "already exists"; no tragar errores reales.
            if (cur is SqliteException sqliteEx
                && (sqliteEx.SqliteErrorCode == 1 || sqliteEx.SqliteExtendedErrorCode == 1)
                && (msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> SqliteColumnExistsAsync(RegNepsDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync();
        }

        try
        {
            await using var cmd = conn.CreateCommand();
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
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync();
            }
        }
    }

    private static async Task<bool> SqliteIndexExistsAsync(RegNepsDbContext db, string indexName)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync();
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1""";
            var p = cmd.CreateParameter();
            p.ParameterName = "$name";
            p.Value = indexName;
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync();
            return result is not null && result is not DBNull;
        }
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync();
            }
        }
    }

    private static async Task<bool> SqlServerColumnExistsAsync(RegNepsDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync();
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT 1
                FROM sys.columns c
                INNER JOIN sys.tables t ON c.object_id = t.object_id
                WHERE t.name = @table AND c.name = @column
                """;
            var t = cmd.CreateParameter();
            t.ParameterName = "@table";
            t.Value = table;
            cmd.Parameters.Add(t);
            var c = cmd.CreateParameter();
            c.ParameterName = "@column";
            c.Value = column;
            cmd.Parameters.Add(c);
            var result = await cmd.ExecuteScalarAsync();
            return result is not null && result is not DBNull;
        }
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync();
            }
        }
    }

    private static async Task<bool> SqlServerIndexExistsAsync(RegNepsDbContext db, string table, string indexName)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync();
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT 1
                FROM sys.indexes i
                INNER JOIN sys.tables t ON i.object_id = t.object_id
                WHERE t.name = @table AND i.name = @index
                """;
            var t = cmd.CreateParameter();
            t.ParameterName = "@table";
            t.Value = table;
            cmd.Parameters.Add(t);
            var i = cmd.CreateParameter();
            i.ParameterName = "@index";
            i.Value = indexName;
            cmd.Parameters.Add(i);
            var result = await cmd.ExecuteScalarAsync();
            return result is not null && result is not DBNull;
        }
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync();
            }
        }
    }
}
