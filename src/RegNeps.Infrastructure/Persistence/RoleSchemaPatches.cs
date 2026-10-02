using Microsoft.EntityFrameworkCore;

namespace RegNeps.Infrastructure.Persistence;

/// <summary>Migración aditiva de RolePermissions (enum) → AppRole + RoleId.</summary>
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

    private static async Task RebuildRolePermissionsSqliteAsync(RegNepsDbContext db)
    {
        await SeedSystemRolesSqliteAsync(db);

        await DatabaseInitializer.TryExecuteAsync(db,
            """
            CREATE TABLE IF NOT EXISTS "RolePermissions_new" (
                "RoleId" TEXT NOT NULL,
                "Permission" INTEGER NOT NULL,
                "IsEnabled" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "UpdatedByUserId" TEXT NULL,
                CONSTRAINT "PK_RolePermissions" PRIMARY KEY ("RoleId", "Permission")
            )
            """);

        await DatabaseInitializer.TryExecuteAsync(db,
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
            """);

        await DatabaseInitializer.TryExecuteAsync(db, "DROP TABLE IF EXISTS \"RolePermissions\"");
        await DatabaseInitializer.TryExecuteAsync(db,
            "ALTER TABLE \"RolePermissions_new\" RENAME TO \"RolePermissions\"");
    }

    private static async Task RebuildRolePermissionAuditsSqliteAsync(RegNepsDbContext db)
    {
        await SeedSystemRolesSqliteAsync(db);

        await DatabaseInitializer.TryExecuteAsync(db,
            """
            CREATE TABLE IF NOT EXISTS "RolePermissionAudits_new" (
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
            """);

        await DatabaseInitializer.TryExecuteAsync(db, "DROP TABLE IF EXISTS \"RolePermissionAudits\"");
        await DatabaseInitializer.TryExecuteAsync(db,
            "ALTER TABLE \"RolePermissionAudits_new\" RENAME TO \"RolePermissionAudits\"");
        await DatabaseInitializer.TryExecuteAsync(db,
            """
            CREATE INDEX IF NOT EXISTS "IX_RolePermissionAudits_ChangedAt"
            ON "RolePermissionAudits" ("ChangedAt")
            """);
    }

    private static async Task SeedSystemRolesSqliteAsync(RegNepsDbContext db)
    {
        foreach (var def in Domain.Constants.SystemRoleCodes.Definitions)
        {
            await DatabaseInitializer.TryExecuteAsync(db,
                $"""
                INSERT OR IGNORE INTO "Roles" ("Id", "Code", "Name", "IsActive", "IsSystem", "SeesAllRecords", "CreatedAt", "UpdatedAt")
                VALUES (lower(hex(randomblob(16))), '{def.Code}', '{def.Name.Replace("'", "''")}', 1, 1, {(def.SeesAllRecords ? 1 : 0)}, datetime('now'), datetime('now'))
                """);
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

    private static async Task RebuildRolePermissionsSqlServerAsync(RegNepsDbContext db)
    {
        await DatabaseInitializer.TryExecuteAsync(db,
            """
            IF OBJECT_ID(N'[RolePermissions_new]', N'U') IS NULL
            BEGIN
                CREATE TABLE [RolePermissions_new] (
                    [RoleId] uniqueidentifier NOT NULL,
                    [Permission] int NOT NULL,
                    [IsEnabled] bit NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [UpdatedAt] datetime2 NOT NULL,
                    [UpdatedByUserId] uniqueidentifier NULL,
                    CONSTRAINT [PK_RolePermissions] PRIMARY KEY ([RoleId], [Permission])
                );
            END
            """);

        await DatabaseInitializer.TryExecuteAsync(db,
            """
            IF NOT EXISTS (SELECT 1 FROM [RolePermissions_new])
            BEGIN
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
            END
            """);

        await DatabaseInitializer.TryExecuteAsync(db, """DROP TABLE [RolePermissions]""");
        await DatabaseInitializer.TryExecuteAsync(db,
            """EXEC sp_rename 'RolePermissions_new', 'RolePermissions'""");
    }

    private static async Task RebuildRolePermissionAuditsSqlServerAsync(RegNepsDbContext db)
    {
        await DatabaseInitializer.TryExecuteAsync(db,
            """
            IF OBJECT_ID(N'[RolePermissionAudits_new]', N'U') IS NULL
            BEGIN
                CREATE TABLE [RolePermissionAudits_new] (
                    [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_RolePermissionAudits] PRIMARY KEY,
                    [RoleId] uniqueidentifier NOT NULL,
                    [Permission] int NOT NULL,
                    [PreviousValue] bit NOT NULL,
                    [NewValue] bit NOT NULL,
                    [ModifiedByUserId] uniqueidentifier NULL,
                    [ChangedAt] datetime2 NOT NULL
                );
            END
            """);

        await DatabaseInitializer.TryExecuteAsync(db,
            """
            IF NOT EXISTS (SELECT 1 FROM [RolePermissionAudits_new])
            BEGIN
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
            END
            """);

        await DatabaseInitializer.TryExecuteAsync(db, """DROP TABLE [RolePermissionAudits]""");
        await DatabaseInitializer.TryExecuteAsync(db,
            """EXEC sp_rename 'RolePermissionAudits_new', 'RolePermissionAudits'""");
        await DatabaseInitializer.TryExecuteAsync(db,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RolePermissionAudits_ChangedAt')
                CREATE INDEX [IX_RolePermissionAudits_ChangedAt] ON [RolePermissionAudits] ([ChangedAt]);
            """);
    }
}
