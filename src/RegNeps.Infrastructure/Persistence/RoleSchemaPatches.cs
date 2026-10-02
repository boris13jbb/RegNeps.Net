using Microsoft.EntityFrameworkCore;

namespace RegNeps.Infrastructure.Persistence;

/// <summary>Migración aditiva de RolePermissions (enum) → AppRole + RoleId.</summary>
/// <remarks>
/// Rebuilds destructivos (DROP + rename) van en transacción explícita vía
/// <see cref="DatabaseFacade.BeginTransactionAsync"/>. El proyecto no usa
/// EnableRetryOnFailure en SQL Server, así que no hace falta CreateExecutionStrategy
/// (mismo patrón que <c>RolePermissionRepository</c>).
/// </remarks>
internal static class RoleSchemaPatches
{
    public static async Task ApplyAsync(RegNepsDbContext db, bool isSqlite)
    {
        if (isSqlite)
        {
            await ApplySqliteAsync(db);
        }
        else
        {
            await ApplySqlServerAsync(db);
        }
    }

    private static async Task ApplySqliteAsync(RegNepsDbContext db)
    {
        await DatabaseInitializer.TryExecuteAsync(db,
            """
            CREATE TABLE IF NOT EXISTS "Roles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Roles" PRIMARY KEY,
                "Code" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "IsSystem" INTEGER NOT NULL,
                "SeesAllRecords" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            )
            """);

        await DatabaseInitializer.TryExecuteAsync(db,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Roles_Code"
            ON "Roles" ("Code")
            """);

        if (!await DatabaseInitializer.SqliteColumnExistsAsync(db, "Users", "RoleCode"))
        {
            await DatabaseInitializer.TryExecuteAsync(db,
                """ALTER TABLE "Users" ADD COLUMN "RoleCode" TEXT NULL""");
        }

        if (await DatabaseInitializer.SqliteColumnExistsAsync(db, "RolePermissions", "Role"))
        {
            await RebuildRolePermissionsSqliteAsync(db);
        }
        else if (!await DatabaseInitializer.SqliteTableExistsAsync(db, "RolePermissions"))
        {
            await DatabaseInitializer.TryExecuteAsync(db,
                """
                CREATE TABLE "RolePermissions" (
                    "RoleId" TEXT NOT NULL,
                    "Permission" INTEGER NOT NULL,
                    "IsEnabled" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "UpdatedByUserId" TEXT NULL,
                    CONSTRAINT "PK_RolePermissions" PRIMARY KEY ("RoleId", "Permission")
                )
                """);
        }

        if (await DatabaseInitializer.SqliteColumnExistsAsync(db, "RolePermissionAudits", "Role"))
        {
            await RebuildRolePermissionAuditsSqliteAsync(db);
        }
        else if (!await DatabaseInitializer.SqliteTableExistsAsync(db, "RolePermissionAudits"))
        {
            await DatabaseInitializer.TryExecuteAsync(db,
                """
                CREATE TABLE "RolePermissionAudits" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_RolePermissionAudits" PRIMARY KEY,
                    "RoleId" TEXT NOT NULL,
                    "Permission" INTEGER NOT NULL,
                    "PreviousValue" INTEGER NOT NULL,
                    "NewValue" INTEGER NOT NULL,
                    "ModifiedByUserId" TEXT NULL,
                    "ChangedAt" TEXT NOT NULL
                )
                """);
            await DatabaseInitializer.TryExecuteAsync(db,
                """
                CREATE INDEX IF NOT EXISTS "IX_RolePermissionAudits_ChangedAt"
                ON "RolePermissionAudits" ("ChangedAt")
                """);
        }

        // BD ya migradas con Guid hex (sin guiones) de la semilla antigua.
        await RepairSqliteUndashedRoleGuidsAsync(db);
        // EF Core SQLite persiste Guid TEXT en mayúsculas; normaliza filas en minúsculas.
        await RepairSqliteGuidTextCasingAsync(db);
    }

    private static async Task ApplySqlServerAsync(RegNepsDbContext db)
    {
        await DatabaseInitializer.TryExecuteAsync(db,
            """
            IF OBJECT_ID(N'[Roles]', N'U') IS NULL
            BEGIN
                CREATE TABLE [Roles] (
                    [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_Roles] PRIMARY KEY,
                    [Code] nvarchar(64) NOT NULL,
                    [Name] nvarchar(128) NOT NULL,
                    [IsActive] bit NOT NULL,
                    [IsSystem] bit NOT NULL,
                    [SeesAllRecords] bit NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [UpdatedAt] datetime2 NOT NULL
                );
                CREATE UNIQUE INDEX [IX_Roles_Code] ON [Roles] ([Code]);
            END
            """);

        if (!await DatabaseInitializer.SqlServerColumnExistsAsync(db, "Users", "RoleCode"))
        {
            await DatabaseInitializer.TryExecuteAsync(db,
                """ALTER TABLE [Users] ADD [RoleCode] nvarchar(64) NULL""");
        }

        await SeedSystemRolesSqlServerAsync(db);

        if (await DatabaseInitializer.SqlServerColumnExistsAsync(db, "RolePermissions", "Role")
            && !await DatabaseInitializer.SqlServerColumnExistsAsync(db, "RolePermissions", "RoleId"))
        {
            await RebuildRolePermissionsSqlServerAsync(db);
        }

        if (await DatabaseInitializer.SqlServerColumnExistsAsync(db, "RolePermissionAudits", "Role")
            && !await DatabaseInitializer.SqlServerColumnExistsAsync(db, "RolePermissionAudits", "RoleId"))
        {
            await RebuildRolePermissionAuditsSqlServerAsync(db);
        }
    }

    /// <summary>
    /// Rebuild enum→RoleId. <paramref name="afterDropAsync"/> solo para pruebas (inyectar fallo post-DROP).
    /// </summary>
    internal static async Task RebuildRolePermissionsSqliteAsync(
        RegNepsDbContext db,
        Func<CancellationToken, Task>? afterDropAsync = null,
        CancellationToken ct = default)
    {
        await SeedSystemRolesSqliteAsync(db);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """DROP TABLE IF EXISTS "RolePermissions_new" """, ct);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "RolePermissions_new" (
                    "RoleId" TEXT NOT NULL,
                    "Permission" INTEGER NOT NULL,
                    "IsEnabled" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "UpdatedByUserId" TEXT NULL,
                    CONSTRAINT "PK_RolePermissions" PRIMARY KEY ("RoleId", "Permission")
                )
                """, ct);

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT OR IGNORE INTO "RolePermissions_new"
                    ("RoleId", "Permission", "IsEnabled", "CreatedAt", "UpdatedAt", "UpdatedByUserId")
                SELECT r."Id", rp."Permission", rp."IsEnabled", rp."CreatedAt", rp."UpdatedAt", rp."UpdatedByUserId"
                FROM "RolePermissions" rp
                INNER JOIN "Roles" r ON r."Code" = CASE rp."Role"
                    WHEN 0 THEN 'Operario'
                    WHEN 1 THEN 'Supervisor'
                    WHEN 2 THEN 'Admin'
                    WHEN 3 THEN 'Gerencia'
                    WHEN 4 THEN 'SuperAdmin'
                    ELSE 'Operario'
                END
                """, ct);

            await db.Database.ExecuteSqlRawAsync("""DROP TABLE IF EXISTS "RolePermissions" """, ct);

            if (afterDropAsync is not null)
            {
                await afterDropAsync(ct);
            }

            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "RolePermissions_new" RENAME TO "RolePermissions" """, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    internal static async Task RebuildRolePermissionAuditsSqliteAsync(
        RegNepsDbContext db,
        Func<CancellationToken, Task>? afterDropAsync = null,
        CancellationToken ct = default)
    {
        await SeedSystemRolesSqliteAsync(db);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """DROP TABLE IF EXISTS "RolePermissionAudits_new" """, ct);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "RolePermissionAudits_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_RolePermissionAudits" PRIMARY KEY,
                    "RoleId" TEXT NOT NULL,
                    "Permission" INTEGER NOT NULL,
                    "PreviousValue" INTEGER NOT NULL,
                    "NewValue" INTEGER NOT NULL,
                    "ModifiedByUserId" TEXT NULL,
                    "ChangedAt" TEXT NOT NULL
                )
                """, ct);

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT OR IGNORE INTO "RolePermissionAudits_new"
                    ("Id", "RoleId", "Permission", "PreviousValue", "NewValue", "ModifiedByUserId", "ChangedAt")
                SELECT a."Id", r."Id", a."Permission", a."PreviousValue", a."NewValue", a."ModifiedByUserId", a."ChangedAt"
                FROM "RolePermissionAudits" a
                INNER JOIN "Roles" r ON r."Code" = CASE a."Role"
                    WHEN 0 THEN 'Operario'
                    WHEN 1 THEN 'Supervisor'
                    WHEN 2 THEN 'Admin'
                    WHEN 3 THEN 'Gerencia'
                    WHEN 4 THEN 'SuperAdmin'
                    ELSE 'Operario'
                END
                """, ct);

            await db.Database.ExecuteSqlRawAsync("""DROP TABLE IF EXISTS "RolePermissionAudits" """, ct);

            if (afterDropAsync is not null)
            {
                await afterDropAsync(ct);
            }

            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "RolePermissionAudits_new" RENAME TO "RolePermissionAudits" """, ct);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE INDEX IF NOT EXISTS "IX_RolePermissionAudits_ChangedAt"
                ON "RolePermissionAudits" ("ChangedAt")
                """, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Convierte Roles.Id / RolePermissions.RoleId / RolePermissionAudits.RoleId
    /// de formato N (32 hex) a formato D (con guiones). Idempotente.
    /// El valor nuevo se pasa como <see cref="Guid"/> para que el proveedor SQLite
    /// de EF lo persista en mayúsculas (mismo casing que SaveChanges).
    /// </summary>
    internal static async Task RepairSqliteUndashedRoleGuidsAsync(
        RegNepsDbContext db,
        CancellationToken ct = default)
    {
        if (!await DatabaseInitializer.SqliteTableExistsAsync(db, "Roles"))
        {
            return;
        }

        var hexIds = await ListSqliteUndashedRoleIdsAsync(db, ct);
        if (hexIds.Count == 0)
        {
            return;
        }

        var hasRolePermissions = await DatabaseInitializer.SqliteTableExistsAsync(db, "RolePermissions")
            && await DatabaseInitializer.SqliteColumnExistsAsync(db, "RolePermissions", "RoleId");
        var hasAudits = await DatabaseInitializer.SqliteTableExistsAsync(db, "RolePermissionAudits")
            && await DatabaseInitializer.SqliteColumnExistsAsync(db, "RolePermissionAudits", "RoleId");

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;", ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                foreach (var hexId in hexIds)
                {
                    if (!Guid.TryParseExact(hexId, "N", out var guid))
                    {
                        continue;
                    }

                    // Ya en formato D (con o sin casing distinto): no reescribir aquí.
                    if (hexId.Contains('-', StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (hasRolePermissions)
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            """UPDATE "RolePermissions" SET "RoleId" = {0} WHERE "RoleId" = {1}""",
                            guid, hexId);
                    }

                    if (hasAudits)
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            """UPDATE "RolePermissionAudits" SET "RoleId" = {0} WHERE "RoleId" = {1}""",
                            guid, hexId);
                    }

                    await db.Database.ExecuteSqlRawAsync(
                        """UPDATE "Roles" SET "Id" = {0} WHERE "Id" = {1}""",
                        guid, hexId);
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", ct);
        }
    }

    /// <summary>
    /// Normaliza Guid TEXT en minúsculas (formato D) al casing mayúsculas que escribe EF Core
    /// en SQLite. Sin esto, SELECT por username funciona pero UPDATE por Id falla
    /// (DbUpdateConcurrencyException: 0 filas). Idempotente; una sola transacción.
    /// </summary>
    /// <remarks>
    /// Usa <c>upper(col)</c> en SQL (no parámetros Guid fila a fila): actualiza de golpe
    /// Roles.Id, RolePermissions.RoleId y RolePermissionAudits.RoleId aunque haya muchas filas.
    /// </remarks>
    internal static async Task RepairSqliteGuidTextCasingAsync(
        RegNepsDbContext db,
        CancellationToken ct = default)
    {
        // Columnas Guid que el parche / seed crudo pueden dejar en minúsculas.
        // Orden: FKs de roles primero, luego PKs de Roles, luego resto.
        var columns = new (string Table, string Column)[]
        {
            ("RolePermissions", "RoleId"),
            ("RolePermissions", "UpdatedByUserId"),
            ("RolePermissionAudits", "RoleId"),
            ("RolePermissionAudits", "Id"),
            ("RolePermissionAudits", "ModifiedByUserId"),
            ("Roles", "Id"),
            ("Users", "Id"),
            ("Fabrics", "Id"),
            ("SavedReports", "Id"),
            ("CorrectiveActions", "Id"),
            ("CorrectiveActions", "NepRecordId"),
            ("NepRecords", "Id"),
            ("LoteTramaItems", "Id"),
        };

        var pending = new List<(string Table, string Column)>();
        foreach (var (table, column) in columns)
        {
            if (!await DatabaseInitializer.SqliteTableExistsAsync(db, table)
                || !await DatabaseInitializer.SqliteColumnExistsAsync(db, table, column))
            {
                continue;
            }

            if (await CountSqliteLowercaseDashedGuidsAsync(db, table, column, ct) > 0)
            {
                pending.Add((table, column));
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        // PRAGMA foreign_keys no tiene efecto dentro de una transacción ya abierta.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;", ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                foreach (var (table, column) in pending)
                {
                    // Identificadores solo desde la lista fija de arriba (no input externo).
                    var sql =
                        $"""
                        UPDATE "{table}"
                        SET "{column}" = upper("{column}")
                        WHERE "{column}" IS NOT NULL
                          AND length("{column}") = 36
                          AND instr("{column}", '-') > 0
                          AND "{column}" GLOB '*[a-f]*'
                        """;
                    await db.Database.ExecuteSqlRawAsync(sql, ct);
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", ct);
        }
    }

    private static async Task<int> CountSqliteLowercaseDashedGuidsAsync(
        RegNepsDbContext db, string table, string column, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync(ct);
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT COUNT(*) FROM "{table}"
                WHERE "{column}" IS NOT NULL
                  AND length("{column}") = 36
                  AND instr("{column}", '-') > 0
                  AND "{column}" GLOB '*[a-f]*'
                """;
            var scalar = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync();
            }
        }
    }

    private static async Task<List<string>> ListSqliteUndashedRoleIdsAsync(
        RegNepsDbContext db, CancellationToken ct)
    {
        var result = new List<string>();
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync(ct);
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            // 32 hex sin guiones (formato Guid "N" / lower(hex(randomblob(16)))).
            cmd.CommandText =
                """
                SELECT "Id" FROM "Roles"
                WHERE length("Id") = 32
                  AND instr("Id", '-') = 0
                  AND "Id" GLOB '[0-9a-fA-F]*'
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    result.Add(id);
                }
            }
        }
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync();
            }
        }

        return result;
    }

    /// <summary>Semilla cruda de roles de sistema (SQLite). Visible a tests de casing.</summary>
    internal static async Task SeedSystemRolesSqliteAsync(RegNepsDbContext db)
    {
        // Pasar Guid (no ToString("D")): el proveedor SQLite de EF persiste TEXT en MAYÚSCULAS,
        // igual que SaveChanges. ToString("D") escribe minúsculas y rompe UPDATE/joins posteriores.
        var now = DateTime.UtcNow.ToString("o");
        foreach (var def in Domain.Constants.SystemRoleCodes.Definitions)
        {
            var id = Guid.NewGuid();
            await DatabaseInitializer.TryExecuteAsync(
                db,
                """
                INSERT OR IGNORE INTO "Roles"
                    ("Id", "Code", "Name", "IsActive", "IsSystem", "SeesAllRecords", "CreatedAt", "UpdatedAt")
                VALUES ({0}, {1}, {2}, 1, 1, {3}, {4}, {5})
                """,
                id,
                def.Code,
                def.Name,
                def.SeesAllRecords ? 1 : 0,
                now,
                now);
        }
    }

    private static async Task SeedSystemRolesSqlServerAsync(RegNepsDbContext db)
    {
        foreach (var def in Domain.Constants.SystemRoleCodes.Definitions)
        {
            await DatabaseInitializer.TryExecuteAsync(db,
                $"""
                IF NOT EXISTS (SELECT 1 FROM [Roles] WHERE [Code] = N'{def.Code}')
                INSERT INTO [Roles] ([Id], [Code], [Name], [IsActive], [IsSystem], [SeesAllRecords], [CreatedAt], [UpdatedAt])
                VALUES (NEWID(), N'{def.Code}', N'{def.Name.Replace("'", "''")}', 1, 1, {(def.SeesAllRecords ? 1 : 0)}, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
        }
    }

    private static async Task RebuildRolePermissionsSqlServerAsync(
        RegNepsDbContext db,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                IF OBJECT_ID(N'[RolePermissions_new]', N'U') IS NOT NULL
                    DROP TABLE [RolePermissions_new];
                """, ct);

            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE [RolePermissions_new] (
                    [RoleId] uniqueidentifier NOT NULL,
                    [Permission] int NOT NULL,
                    [IsEnabled] bit NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [UpdatedAt] datetime2 NOT NULL,
                    [UpdatedByUserId] uniqueidentifier NULL,
                    CONSTRAINT [PK_RolePermissions] PRIMARY KEY ([RoleId], [Permission])
                );
                """, ct);

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO [RolePermissions_new] ([RoleId], [Permission], [IsEnabled], [CreatedAt], [UpdatedAt], [UpdatedByUserId])
                SELECT r.[Id], rp.[Permission], rp.[IsEnabled], rp.[CreatedAt], rp.[UpdatedAt], rp.[UpdatedByUserId]
                FROM [RolePermissions] rp
                INNER JOIN [Roles] r ON r.[Code] = CASE rp.[Role]
                    WHEN 0 THEN 'Operario'
                    WHEN 1 THEN 'Supervisor'
                    WHEN 2 THEN 'Admin'
                    WHEN 3 THEN 'Gerencia'
                    WHEN 4 THEN 'SuperAdmin'
                    ELSE 'Operario'
                END;
                """, ct);

            await db.Database.ExecuteSqlRawAsync("""DROP TABLE [RolePermissions]""", ct);
            await db.Database.ExecuteSqlRawAsync(
                """EXEC sp_rename 'RolePermissions_new', 'RolePermissions'""", ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task RebuildRolePermissionAuditsSqlServerAsync(
        RegNepsDbContext db,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                IF OBJECT_ID(N'[RolePermissionAudits_new]', N'U') IS NOT NULL
                    DROP TABLE [RolePermissionAudits_new];
                """, ct);

            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE [RolePermissionAudits_new] (
                    [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_RolePermissionAudits] PRIMARY KEY,
                    [RoleId] uniqueidentifier NOT NULL,
                    [Permission] int NOT NULL,
                    [PreviousValue] bit NOT NULL,
                    [NewValue] bit NOT NULL,
                    [ModifiedByUserId] uniqueidentifier NULL,
                    [ChangedAt] datetime2 NOT NULL
                );
                """, ct);

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO [RolePermissionAudits_new]
                    ([Id], [RoleId], [Permission], [PreviousValue], [NewValue], [ModifiedByUserId], [ChangedAt])
                SELECT a.[Id], r.[Id], a.[Permission], a.[PreviousValue], a.[NewValue], a.[ModifiedByUserId], a.[ChangedAt]
                FROM [RolePermissionAudits] a
                INNER JOIN [Roles] r ON r.[Code] = CASE a.[Role]
                    WHEN 0 THEN 'Operario'
                    WHEN 1 THEN 'Supervisor'
                    WHEN 2 THEN 'Admin'
                    WHEN 3 THEN 'Gerencia'
                    WHEN 4 THEN 'SuperAdmin'
                    ELSE 'Operario'
                END;
                """, ct);

            await db.Database.ExecuteSqlRawAsync("""DROP TABLE [RolePermissionAudits]""", ct);
            await db.Database.ExecuteSqlRawAsync(
                """EXEC sp_rename 'RolePermissionAudits_new', 'RolePermissionAudits'""", ct);
            await db.Database.ExecuteSqlRawAsync(
                """
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RolePermissionAudits_ChangedAt')
                    CREATE INDEX [IX_RolePermissionAudits_ChangedAt] ON [RolePermissionAudits] ([ChangedAt]);
                """, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
