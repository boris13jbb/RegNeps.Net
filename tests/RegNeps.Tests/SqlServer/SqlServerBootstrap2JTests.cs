using RegNeps.Application.Records;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Sync;
using Xunit.Abstractions;

namespace RegNeps.Tests.SqlServer;

/// <summary>FASE 2J — bootstrap Web físico en SQL Server: fresh, arranques repetidos y upgrade.</summary>
public sealed class SqlServerBootstrap2JTests
{
    private static readonly string[] RequiredTables =
    [
        "NepRecords", "CorrectiveActions", "SyncChangeLogs", "Users", "Roles", "RolePermissions",
        "RolePermissionAudits", "Fabrics", "LoteTramaItems", "AlertConfigs", "SavedReports"
    ];

    private static readonly string[] CountedTables =
    [
        "NepRecords", "CorrectiveActions", "SyncChangeLogs", "Users", "Roles", "RolePermissions",
        "Fabrics", "LoteTramaItems", "AlertConfigs"
    ];

    private readonly ITestOutputHelper _output;

    public SqlServerBootstrap2JTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task Fresh_Bootstrap_Creates_Schema_Indexes_Seeds_And_Catalog_Baseline()
    {
        await using var db = SqlServerValidationDatabase.Create("fresh");
        Assert.False(await db.DatabaseExistsAsync());

        await db.BootstrapAsync();

        Assert.True(await db.DatabaseExistsAsync());
        var tables = await db.StringsAsync("SELECT name FROM sys.tables");
        foreach (var table in RequiredTables)
        {
            Assert.Contains(table, tables);
        }

        // Contrato Web: EnsureCreated + patches; Migrate() nunca se ejecutó contra esta BD.
        Assert.DoesNotContain("__EFMigrationsHistory", tables);

        var columns = await db.ColumnSignatureAsync();
        Assert.Contains("NepRecords.Id:uniqueidentifier(16):NOTNULL", columns);
        Assert.Contains("NepRecords.Neps:float(8):NOTNULL", columns);
        Assert.Contains("NepRecords.CreatedAt:datetime2(8):NOTNULL", columns);
        Assert.Contains("NepRecords.ConcurrencyStamp:nvarchar(128):NOTNULL", columns);
        Assert.Contains("NepRecords.ClientOperationId:nvarchar(128):NULL", columns);
        Assert.Contains("NepRecords.CaptureSessionId:nvarchar(128):NULL", columns);
        Assert.Contains("NepRecords.RevisadoPorSupervisor:bit(1):NOTNULL", columns);
        Assert.Contains("SyncChangeLogs.Sequence:bigint(8):NOTNULL:IDENTITY", columns);
        Assert.Contains("SyncChangeLogs.EntityId:uniqueidentifier(16):NOTNULL", columns);
        Assert.Contains("SyncChangeLogs.OccurredAtUtc:datetime2(8):NOTNULL", columns);
        Assert.Contains("SyncChangeLogs.PayloadJson:nvarchar(-1):NOTNULL", columns);

        var indexes = await db.IndexSignatureAsync();
        foreach (var line in indexes)
        {
            _output.WriteLine(line);
        }

        Assert.Contains(indexes, i => i.StartsWith("SyncChangeLogs.PK_SyncChangeLogs:U:CLUSTERED::Sequence", StringComparison.Ordinal));
        Assert.Contains(indexes, i => i.StartsWith("NepRecords.IX_NepRecords_CreatedBy_ClientOperation:U:NONCLUSTERED:", StringComparison.Ordinal)
                                      && i.Contains("ClientOperationId] IS NOT NULL", StringComparison.Ordinal)
                                      && i.EndsWith(":CreatedByUserId,ClientOperationId", StringComparison.Ordinal));
        Assert.Contains(indexes, i => i.StartsWith("SyncChangeLogs.IX_SyncChangeLogs_Actor_ClientOperation:U:NONCLUSTERED:", StringComparison.Ordinal)
                                      && i.Contains("ClientOperationId] IS NOT NULL", StringComparison.Ordinal)
                                      && i.EndsWith(":ActorUserId,ClientOperationId", StringComparison.Ordinal));
        Assert.Contains("SyncChangeLogs.IX_SyncChangeLogs_Owner_Sequence:N:NONCLUSTERED::OwnerUserId,Sequence", indexes);
        Assert.Contains("SyncChangeLogs.IX_SyncChangeLogs_EntityType_EntityId:N:NONCLUSTERED::EntityType,EntityId", indexes);
        Assert.Contains("SyncChangeLogs.IX_SyncChangeLogs_ClientOperationId:N:NONCLUSTERED::ClientOperationId", indexes);
        Assert.Contains("NepRecords.IX_NepRecords_CreatedBy_CaptureSession:N:NONCLUSTERED::CreatedByUserId,CaptureSessionId", indexes);
        Assert.Contains("Fabrics.IX_Fabrics_Name_Unique:U:NONCLUSTERED::Name", indexes);
        Assert.Contains(indexes, i => i.StartsWith("LoteTramaItems.", StringComparison.Ordinal)
                                      && i.EndsWith(":U:NONCLUSTERED::Code", StringComparison.Ordinal));

        Assert.Equal("CASCADE", (await db.StringsAsync(
            """
            SELECT delete_referential_action_desc FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'dbo.CorrectiveActions')
              AND referenced_object_id = OBJECT_ID(N'dbo.NepRecords')
            """)).Single());

        // Seeds.
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM AlertConfigs"));
        Assert.Equal(2, await db.ScalarAsync<int>("SELECT COUNT(*) FROM Fabrics"));
        Assert.Equal(LoteTramaDefaults.Codes.Count, await db.ScalarAsync<int>("SELECT COUNT(*) FROM LoteTramaItems"));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Username = N'admin' AND IsSuperAdmin = 1"));
        Assert.True(await db.ScalarAsync<int>("SELECT COUNT(*) FROM Roles") >= 4);
        Assert.True(await db.ScalarAsync<int>("SELECT COUNT(*) FROM RolePermissions") > 0);

        // Baseline de catálogos: un CatalogUpserted por Fabric/Lote.
        var catalogLogs = await db.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM SyncChangeLogs WHERE EntityType = N'{SyncConstants.EntityCatalogItem}'");
        Assert.Equal(2 + LoteTramaDefaults.Codes.Count, catalogLogs);
        Assert.Equal(catalogLogs, await db.ScalarAsync<int>(
            $"SELECT COUNT(DISTINCT EntityId) FROM SyncChangeLogs WHERE ChangeType = N'{SyncConstants.ChangeCatalogUpserted}'"));
        Assert.Equal(0, await db.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM SyncChangeLogs WHERE EntityType = N'{SyncConstants.EntityNepRecord}'"));

        // EnsureCreated de EF Core (SQL Server) activa READ_COMMITTED_SNAPSHOT al crear la base.
        Assert.True(await db.ScalarAsync<bool>(
            "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));
    }

    [SqlServerFact]
    public async Task Repeated_Bootstrap_Is_Idempotent_And_Preserves_Data()
    {
        await using var db = SqlServerValidationDatabase.Create("repeat");
        await db.BootstrapAsync();
        await db.StartAsync(); // segundo arranque

        var admin = await db.CreateActorAsync("boot_admin", AppUserRole.Admin);
        var records = db.Records();
        for (var i = 0; i < 3; i++)
        {
            var created = await records.CreateAsync(new CreateNepRecordRequest
            {
                Telar = $"BOOT-{i}",
                Neps = 10 + i,
                Tela = "Denim",
                LoteTrama = "L2J",
                ClientOperationId = $"boot-{i}-{Guid.NewGuid():N}"
            }, admin);
            await records.ApplyCorrectiveAsync(new CorrectiveActionRequest
            {
                RecordId = created.Id,
                Accion = "Ajuste",
                Responsable = "QA",
                MarcarRevisado = true
            }, admin);
        }

        var columnsBefore = await db.ColumnSignatureAsync();
        var indexesBefore = await db.IndexSignatureAsync();
        var countsBefore = await CountsAsync(db);
        var logsBefore = await ChangeLogFingerprintAsync(db);
        var stampsBefore = await db.StringsAsync("SELECT CONVERT(nvarchar(36), Id) + '|' + ConcurrencyStamp FROM NepRecords ORDER BY Id");

        await db.BootstrapAsync(); // tercer arranque
        await db.BootstrapAsync(); // cuarto arranque

        Assert.Equal(columnsBefore, await db.ColumnSignatureAsync());
        Assert.Equal(indexesBefore, await db.IndexSignatureAsync());
        Assert.Equal(countsBefore, await CountsAsync(db));
        Assert.Equal(logsBefore, await ChangeLogFingerprintAsync(db));
        Assert.Equal(stampsBefore, await db.StringsAsync("SELECT CONVERT(nvarchar(36), Id) + '|' + ConcurrencyStamp FROM NepRecords ORDER BY Id"));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Username = N'admin'"));
        Assert.Equal(0, await db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM (SELECT RoleId, Permission FROM RolePermissions GROUP BY RoleId, Permission HAVING COUNT(*) > 1) d"));
    }

    /// <summary>
    /// Upgrade: BD con datos y esquema pre-sync (sin SyncChangeLogs, ConcurrencyStamp, ClientOperationId,
    /// CaptureSessionId ni índice único de Fabrics) → arranque actual → los patches completan el esquema
    /// sin perder registros; arranques posteriores son no-op.
    /// </summary>
    [SqlServerFact]
    public async Task Upgrade_From_PreSync_Schema_Preserves_Data_And_Converges_With_Fresh()
    {
        await using var fresh = SqlServerValidationDatabase.Create("upg_ref");
        await fresh.BootstrapAsync();
        var freshColumns = await fresh.ColumnSignatureAsync();
        var freshIndexes = await fresh.IndexSignatureAsync();

        await using var db = SqlServerValidationDatabase.Create("upgrade");
        await db.StartAsync();
        var admin = await db.CreateActorAsync("upg_admin", AppUserRole.Admin);
        var records = db.Records();
        var created = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var r = await records.CreateAsync(new CreateNepRecordRequest
            {
                Telar = $"UPG-{i}",
                Neps = 12 + (i * 15),
                Tela = "Denim",
                LoteTrama = "LUPG",
                ClientOperationId = $"upg-{i}"
            }, admin);
            created.Add(r.Id);
        }

        await records.ApplyCorrectiveAsync(new CorrectiveActionRequest
        {
            RecordId = created[0],
            Accion = "Previo al upgrade",
            Responsable = "QA",
            MarcarRevisado = true
        }, admin);

        // Simula el esquema heredado anterior a FASE 2B/2C.
        await db.ExecuteAsync(
            """
            DROP INDEX [IX_NepRecords_CreatedBy_ClientOperation] ON [NepRecords];
            DROP INDEX [IX_NepRecords_CreatedBy_CaptureSession] ON [NepRecords];
            ALTER TABLE [NepRecords] DROP COLUMN [ClientOperationId], [CaptureSessionId], [ConcurrencyStamp];
            DROP INDEX [IX_Fabrics_Name_Unique] ON [Fabrics];
            DROP TABLE [SyncChangeLogs];
            """);

        // Fila histórica insertada con el esquema legacy (sin stamp ni ClientOperationId).
        var legacyColumns = string.Join(", ", (await db.StringsAsync(
                "SELECT QUOTENAME(name) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.NepRecords') AND name <> 'Id' ORDER BY column_id")));
        await db.ExecuteAsync(
            $"INSERT INTO NepRecords (Id, {legacyColumns}) SELECT NEWID(), {legacyColumns} FROM NepRecords WHERE Id = '{created[1]}'");

        var dataBefore = await db.StringsAsync(
            "SELECT CONVERT(nvarchar(36), Id) + '|' + Telar + '|' + CONVERT(nvarchar(30), Neps) + '|' + ISNULL(AccionCorrectiva, '') FROM NepRecords ORDER BY Id");
        Assert.Equal(5, dataBefore.Count);
        var correctivesBefore = await db.ScalarAsync<int>("SELECT COUNT(*) FROM CorrectiveActions");
        var usersBefore = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users");
        var permsBefore = await db.ScalarAsync<int>("SELECT COUNT(*) FROM RolePermissions");

        await db.BootstrapAsync(); // arranque con binario actual sobre BD heredada

        Assert.Equal(dataBefore, await db.StringsAsync(
            "SELECT CONVERT(nvarchar(36), Id) + '|' + Telar + '|' + CONVERT(nvarchar(30), Neps) + '|' + ISNULL(AccionCorrectiva, '') FROM NepRecords ORDER BY Id"));
        Assert.Equal(correctivesBefore, await db.ScalarAsync<int>("SELECT COUNT(*) FROM CorrectiveActions"));
        Assert.Equal(usersBefore, await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users"));
        Assert.Equal(permsBefore, await db.ScalarAsync<int>("SELECT COUNT(*) FROM RolePermissions"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM NepRecords WHERE ConcurrencyStamp IS NULL OR ConcurrencyStamp = ''"));
        Assert.Equal(5, await db.ScalarAsync<int>("SELECT COUNT(DISTINCT ConcurrencyStamp) FROM NepRecords"));
        Assert.Equal(
            await db.ScalarAsync<int>("SELECT COUNT(*) FROM Fabrics") + await db.ScalarAsync<int>("SELECT COUNT(*) FROM LoteTramaItems"),
            await db.ScalarAsync<int>($"SELECT COUNT(*) FROM SyncChangeLogs WHERE EntityType = N'{SyncConstants.EntityCatalogItem}'"));

        var upgradedColumns = await db.ColumnSignatureAsync();
        var upgradedIndexes = await db.IndexSignatureAsync();
        Assert.Equal(freshColumns, upgradedColumns);
        var onlyFresh = freshIndexes.Except(upgradedIndexes).ToList();
        var onlyUpgraded = upgradedIndexes.Except(freshIndexes).ToList();
        foreach (var line in onlyFresh)
        {
            _output.WriteLine("solo fresh: " + line);
        }

        foreach (var line in onlyUpgraded)
        {
            _output.WriteLine("solo upgrade: " + line);
        }

        // Diferencia conocida y benigna: IX_SyncChangeLogs_Sequence (modelo EF) duplica la PK clustered.
        Assert.All(onlyFresh, i => Assert.StartsWith("SyncChangeLogs.IX_SyncChangeLogs_Sequence:", i));
        Assert.Empty(onlyUpgraded);

        // Arranque posterior: no-op.
        var countsAfterUpgrade = await CountsAsync(db);
        var logsAfterUpgrade = await ChangeLogFingerprintAsync(db);
        await db.BootstrapAsync();
        Assert.Equal(upgradedColumns, await db.ColumnSignatureAsync());
        Assert.Equal(upgradedIndexes, await db.IndexSignatureAsync());
        Assert.Equal(countsAfterUpgrade, await CountsAsync(db));
        Assert.Equal(logsAfterUpgrade, await ChangeLogFingerprintAsync(db));

        // El esquema upgrade es funcional: Create + idempotencia sobre el índice reconstruido.
        var postUpgrade = db.Records();
        var op = "post-upgrade-" + Guid.NewGuid().ToString("N");
        var first = await postUpgrade.CreateAsync(new CreateNepRecordRequest { Telar = "POST", Neps = 20, ClientOperationId = op }, admin);
        var second = await postUpgrade.CreateAsync(new CreateNepRecordRequest { Telar = "POST", Neps = 20, ClientOperationId = op }, admin);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM NepRecords WHERE ClientOperationId = N'{op}'"));
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM SyncChangeLogs WHERE ClientOperationId = N'{op}'"));
    }

    private static async Task<List<string>> CountsAsync(SqlServerValidationDatabase db)
    {
        var counts = new List<string>();
        foreach (var table in CountedTables)
        {
            counts.Add($"{table}={await db.ScalarAsync<int>($"SELECT COUNT(*) FROM [{table}]")}");
        }

        return counts;
    }

    private static Task<List<string>> ChangeLogFingerprintAsync(SqlServerValidationDatabase db) =>
        db.StringsAsync(
            """
            SELECT CONVERT(nvarchar(20), Sequence) + '|' + EntityType + '|' + CONVERT(nvarchar(36), EntityId) + '|'
                   + ChangeType + '|' + CONVERT(nvarchar(20), CHECKSUM(PayloadJson))
            FROM SyncChangeLogs ORDER BY Sequence
            """);
}
